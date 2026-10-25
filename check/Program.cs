using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using LiveSeedMap;
using UnityEngine;

/*
 * Out-of-game check of the mod's server (docs/live-link.mdx#checks): Origin and Host rules, the preflight, the pin-write
 * gate and body checks, the hand-off to the main thread and back, and the focus event on the stream. The main thread is
 * played by a loop here that answers each pin command as PinWrites would, without Unity. It listens on 45893, never on
 * the game's 45892, so a running game is not disturbed.
 *
 *   LIVE_MANAGED=<Valheim>\valheim_Data\Managed LIVE_BEPINEX=<BepInEx>\core dotnet run --project mod/check
 */
static class Program
{
    const int Port = 45893;
    const string Site = "https://thevalheim.wiki";
    static int failures;
    static volatile bool writes;
    static volatile bool mainThread = true;
    static readonly List<PinCommand> seen = new List<PinCommand>();
    /* What the main thread answers GET /v1/explored with; null plays the main menu. */
    static volatile ExploredPlanes planes;
    /* GET /v1/portals (mod 1.3); null plays the main menu. */
    static volatile PortalBook portals;
    static volatile bool tracking;
    static readonly List<TrackCommand> tracks = new List<TrackCommand>();

    static int Main()
    {
        var dirs = new[] { Environment.GetEnvironmentVariable("LIVE_MANAGED"), Environment.GetEnvironmentVariable("LIVE_BEPINEX") }.Where(d => !string.IsNullOrEmpty(d)).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var name = new AssemblyName(e.Name).Name + ".dll";
            var file = dirs.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
            return file == null ? null : Assembly.LoadFrom(file);
        };
        return Run();
    }

    static int Run()
    {
        /* The show-on-map key as the page shows it (mod 1.2.1): modifiers first, None as no key. */
        Check("key label", Json.KeyLabel(new KeyboardShortcut(KeyCode.J)) == "J", Json.KeyLabel(new KeyboardShortcut(KeyCode.J)));
        Check("key label with a modifier", Json.KeyLabel(new KeyboardShortcut(KeyCode.J, KeyCode.LeftControl)) == "LeftControl + J", Json.KeyLabel(new KeyboardShortcut(KeyCode.J, KeyCode.LeftControl)));
        Check("key set to None", Json.KeyLabel(KeyboardShortcut.Empty) == null, Json.KeyLabel(KeyboardShortcut.Empty));

        CheckPortalBook();
        CheckBuildingBook();
        CheckTrackArgs();

        var server = new LiveServer(Port, new[] { Site }, new ManualLogSource("check")) { PinWritesAllowed = () => writes, PortalsJson = () => portals?.ToJson(), TrackingAllowed = () => tracking };
        server.Start();
        new Thread(() =>
        {
            while (true)
            {
                if (mainThread && server.TryTakeCommand(out var command))
                {
                    if (!command.Abandoned)
                    {
                        lock (seen) seen.Add(command);
                        var pin = PinArgs.Parse(command.Body, out var problem);
                        command.Status = pin == null ? 400 : 200;
                        command.Response = pin == null ? problem : "{\"ok\":true,\"action\":\"" + command.Action + "\",\"type\":" + pin.Type + "}";
                    }
                    command.Done.Set();
                }
                if (mainThread && server.TryTakeTrack(out var track))
                {
                    if (!track.Abandoned)
                    {
                        lock (tracks) tracks.Add(track);
                        track.Status = 200;
                        track.Response = track.Target == null ? "{\"ok\":true,\"cleared\":true}" : "{\"ok\":true,\"tracking\":true}";
                    }
                    track.Done.Set();
                }
                if (mainThread && server.TryTakeExplored(out var read))
                {
                    if (!read.Abandoned) read.Planes = planes;
                    read.Done.Set();
                }
                if (mainThread && server.TryTakeNearby(out var nearby))
                {
                    if (!nearby.Abandoned) nearby.Json = planes == null ? null : "{\"v\":1,\"radius\":" + (float.IsInfinity(nearby.Range) ? "\"reach\"" : nearby.Range.ToString(System.Globalization.CultureInfo.InvariantCulture)) + ",\"objects\":[]}";
                    nearby.Done.Set();
                }
                Thread.Sleep(5);
            }
        }) { IsBackground = true }.Start();

        var host = $"127.0.0.1:{Port}";
        /* The game publishes every Interval; /v1/state answers with the next one. */
        var publishing = true;
        new Thread(() => { while (Volatile.Read(ref publishing)) { if (!writes) server.Publish(new Snapshot { PinWrites = false }); Thread.Sleep(100); } }) { IsBackground = true }.Start();

        Expect("state from the site", Send("GET", "/v1/state", host, Site), "200 OK", "\"v\":1", $"\"mod\":\"{Plugin.Version}\"", "\"pinWrites\":false", "\"ship\":null", "\"wind\":null", "\"exploredVersion\":null", "\"focusKey\":null", "\"portalsVersion\":null", "\"tracking\":false", "\"track\":null", "Access-Control-Allow-Origin: https://thevalheim.wiki");
        Expect("foreign Origin", Send("GET", "/v1/state", host, "https://evil.example"), "403 Forbidden");
        Expect("foreign Host", Send("GET", "/v1/state", "evil.example", Site), "403 Forbidden");
        Expect("preflight", Send("OPTIONS", "/v1/pins", host, Site), "204 No Content", "Access-Control-Allow-Methods: GET, POST, DELETE", "Access-Control-Allow-Headers: Content-Type", "Access-Control-Allow-Private-Network: true");
        Expect("write while off", Send("POST", "/v1/pins", host, Site, "{\"type\":3,\"name\":\"a\",\"x\":1,\"z\":2}"), "403 Forbidden", "\"error\":\"pin-writes-off\"", "Access-Control-Allow-Origin: https://thevalheim.wiki");
        Expect("write from a foreign Origin", Send("POST", "/v1/pins", host, "https://evil.example", "{\"type\":3,\"name\":\"a\",\"x\":1,\"z\":2}"), "403 Forbidden");

        writes = true;
        Volatile.Write(ref publishing, false);
        Expect("not JSON", Send("POST", "/v1/pins", host, Site, "{\"type\":3,\"x\":1,\"z\":2}", "text/plain"), "415");
        Expect("too large", Send("POST", "/v1/pins", host, Site, "{\"name\":\"" + new string('a', 5000) + "\"}"), "413");
        Expect("death marker refused", Send("POST", "/v1/pins", host, Site, "{\"type\":4,\"name\":\"\",\"x\":1,\"z\":2}"), "400 Bad Request", "\"error\":\"pin-type\"");
        Expect("bed refused", Send("POST", "/v1/pins", host, Site, "{\"type\":5,\"name\":\"\",\"x\":1,\"z\":2}"), "400 Bad Request", "\"error\":\"pin-type\"");
        Expect("off the world", Send("POST", "/v1/pins", host, Site, "{\"type\":0,\"name\":\"\",\"x\":20000,\"z\":2}"), "400 Bad Request", "\"error\":\"bad-request\"");
        Expect("name too long", Send("POST", "/v1/pins", host, Site, "{\"type\":0,\"name\":\"" + new string('n', 65) + "\",\"x\":1,\"z\":2}"), "400 Bad Request");
        Expect("add a portal", Send("POST", "/v1/pins", host, Site, "{\"type\":6,\"name\":\"Home\",\"x\":10.5,\"z\":-20.25,\"checked\":false}"), "200 OK", "\"action\":\"Add\"", "\"type\":6");
        Expect("add from a program (no Origin)", Send("POST", "/v1/pins", host, null, "{\"type\":0,\"name\":\"Camp\",\"x\":1,\"z\":2}"), "200 OK", "\"action\":\"Add\"");
        Expect("remove", Send("DELETE", "/v1/pins", host, Site, "{\"type\":6,\"name\":\"Home\",\"x\":10.5,\"z\":-20.25}"), "200 OK", "\"action\":\"Remove\"");
        Expect("check without a value", Send("POST", "/v1/pins/checked", host, Site, "{\"type\":6,\"name\":\"Home\",\"x\":10.5,\"z\":-20.25}"), "400 Bad Request");
        Expect("check", Send("POST", "/v1/pins/checked", host, Site, "{\"type\":6,\"name\":\"Home\",\"x\":10.5,\"z\":-20.25,\"checked\":true}"), "200 OK", "\"action\":\"Check\"");
        Expect("GET on the write path", Send("GET", "/v1/pins", host, Site), "404");
        lock (seen) Check("main thread saw 4 writes", seen.Count == 4, $"{seen.Count}");

        /* The explored planes (mod 1.2), read on the main thread. */
        Expect("nearby at the main menu", Send("GET", "/v1/nearby", host, Site), "409 Conflict", "\"error\":\"no-player\"");
        Expect("explored at the main menu", Send("GET", "/v1/explored", host, Site), "409 Conflict", "\"error\":\"no-world\"");
        planes = new ExploredPlanes { Size = 4, Version = 7, Explored = new byte[] { 0x01, 0x80 }, Others = new byte[] { 0x00, 0x01 } };
        Expect("explored", Send("GET", "/v1/explored", host, Site), "200 OK", "\"v\":1", "\"size\":4", "\"version\":7", "\"explored\":\"AYA=\"", "\"others\":\"AAE=\"", "Access-Control-Allow-Origin: https://thevalheim.wiki");
        Expect("nearby with a range", Send("GET", "/v1/nearby?range=50", host, Site), "200 OK", "\"radius\":50", "Access-Control-Allow-Origin: https://thevalheim.wiki");
        Expect("nearby without a range", Send("GET", "/v1/nearby", host, null), "200 OK", "\"radius\":\"reach\"");
        Expect("nearby from a foreign Origin", Send("GET", "/v1/nearby", host, "https://evil.example"), "403 Forbidden");
        Expect("explored from a foreign Origin", Send("GET", "/v1/explored", host, "https://evil.example"), "403 Forbidden");

        /* The remembered portals (mod 1.3): 409 at the main menu, then the book as JSON with CORS. */
        Expect("portals at the main menu", Send("GET", "/v1/portals", host, Site), "409 Conflict", "\"error\":\"no-world\"");
        portals = new PortalBook("-4455");
        portals.Observe(new PortalRecord { Prefab = "portal_wood", X = 10.25f, Y = 31f, Z = -20.5f, Tag = "Home", Linked = true, To = PortalBook.Key("portal_stone", -900f, 40.5f, 1200f) });
        Expect("portals", Send("GET", "/v1/portals", host, Site), "200 OK", "\"v\":1", "\"world\":\"-4455\"", "\"version\":" + portals.Version,
            "\"prefab\":\"portal_wood\",\"x\":10.25,\"y\":31.0,\"z\":-20.5,\"tag\":\"Home\",\"linked\":true,\"to\":\"portal_stone@-900.0,40.5,1200.0\"}",
            "Access-Control-Allow-Origin: https://thevalheim.wiki");
        Expect("portals from a foreign Origin", Send("GET", "/v1/portals", host, "https://evil.example"), "403 Forbidden");

        /* Track in game (mod 1.4): off answers 403 with CORS; on, a target and a clear are handed to the main thread. */
        const string target = "{\"x\":120.5,\"z\":-340,\"label\":\"Troll cave\",\"clearWithin\":10}";
        Expect("track preflight", Send("OPTIONS", "/v1/track", host, Site), "204 No Content", "Access-Control-Allow-Methods: GET, POST, DELETE");
        Expect("track while off", Send("POST", "/v1/track", host, Site, target), "403 Forbidden", "\"error\":\"tracking-off\"", "Access-Control-Allow-Origin: https://thevalheim.wiki");
        Expect("clear while off", Send("DELETE", "/v1/track", host, Site), "403 Forbidden", "\"error\":\"tracking-off\"");
        tracking = true;
        Expect("track from a foreign Origin", Send("POST", "/v1/track", host, "https://evil.example", target), "403 Forbidden");
        Expect("track not JSON", Send("POST", "/v1/track", host, Site, target, "text/plain"), "415");
        Expect("track off the world", Send("POST", "/v1/track", host, Site, "{\"x\":1,\"z\":-20000,\"label\":\"far\"}"), "400 Bad Request", "\"error\":\"bad-request\"");
        Expect("track a negative clear radius", Send("POST", "/v1/track", host, Site, "{\"x\":1,\"z\":2,\"clearWithin\":-1}"), "400 Bad Request");
        Expect("track", Send("POST", "/v1/track", host, Site, target), "200 OK", "\"tracking\":true", "Access-Control-Allow-Origin: https://thevalheim.wiki");
        Expect("clear", Send("DELETE", "/v1/track", host, Site), "200 OK", "\"cleared\":true");
        Expect("GET on the track path", Send("GET", "/v1/track", host, Site), "404");
        lock (tracks) Check("main thread saw the target and the clear", tracks.Count == 2 && tracks[0].Target?.Label == "Troll cave" && tracks[0].Target.X == 120.5f && tracks[0].Target.ClearWithin == 10f && tracks[1].Target == null, $"{tracks.Count}");

        mainThread = false;
        Expect("track while the game does not answer", Send("POST", "/v1/track", host, Site, target), "503", "\"error\":\"busy\"");
        Expect("explored while the game does not answer", Send("GET", "/v1/explored", host, Site), "503", "\"error\":\"busy\"");
        Expect("game not answering", Send("POST", "/v1/pins", host, Site, "{\"type\":1,\"name\":\"late\",\"x\":1,\"z\":2}"), "503", "\"error\":\"busy\"");
        mainThread = true;
        Thread.Sleep(50);
        lock (seen) Check("an abandoned write is skipped", seen.Count == 4, $"{seen.Count}");
        lock (tracks) Check("an abandoned target is skipped", tracks.Count == 2, $"{tracks.Count}");

        /* The stream: a snapshot as a plain message, a focus as a named event that v1.0 pages ignore. */
        using (var client = new TcpClient("127.0.0.1", Port))
        {
            var stream = client.GetStream();
            var head = Encoding.ASCII.GetBytes($"GET /v1/stream HTTP/1.1\r\nHost: {host}\r\nOrigin: {Site}\r\n\r\n");
            stream.Write(head, 0, head.Length);
            Thread.Sleep(100);
            server.Publish(new Snapshot { PinWrites = true, Tracking = true, Track = new TrackInfo { X = 120.5f, Z = -340f, Label = "Troll cave", ClearWithin = 10f }, ExploredVersion = 42, PortalsVersion = -7, FocusKey = Json.KeyLabel(new KeyboardShortcut(KeyCode.J, KeyCode.LeftControl)),
                Ship = new ShipInfo { Prefab = "Karve", Name = "$ship_karve", X = 10f, Z = -5f, Heading = 90f, Speed = 4.25f, Sail = "half" },
                Wind = new WindInfo { Angle = 225f, Intensity = 0.8f } });
            Thread.Sleep(100);
            server.PublishFocus(new FocusTarget { Kind = "item", Prefab = "Chain" });
            Thread.Sleep(100);
            server.PublishFocus(new FocusTarget { Kind = "creature", Prefab = "Troll" });
            Thread.Sleep(200);
            var buffer = new byte[65536];
            client.ReceiveTimeout = 1000;
            var text = new StringBuilder();
            try { int n; while (text.Length < 2000 && (n = stream.Read(buffer, 0, buffer.Length)) > 0) { text.Append(Encoding.UTF8.GetString(buffer, 0, n)); if (text.ToString().Contains("Troll")) break; } }
            catch (IOException) { }
            var got = text.ToString();
            Expect("stream", got, "text/event-stream", "data: {\"v\":1,", "\"pinWrites\":true",
                "\"ship\":{\"prefab\":\"Karve\",\"name\":\"$ship_karve\",\"x\":10.0,\"z\":-5.0,\"heading\":90.0,\"speed\":4.2,\"sail\":\"half\"}",
                "\"wind\":{\"angle\":225.0,\"intensity\":0.8}", "\"exploredVersion\":42", "\"portalsVersion\":-7", "\"focusKey\":\"LeftControl + J\"",
                "\"tracking\":true,\"track\":{\"x\":120.5,\"z\":-340.0,\"label\":\"Troll cave\",\"clearWithin\":10.0}",
                "event: focus\ndata: {\"v\":1,\"type\":\"focus\",\"kind\":\"item\",\"prefab\":\"Chain\"}\n\n",
                "event: focus\ndata: {\"v\":1,\"type\":\"focus\",\"kind\":\"creature\",\"prefab\":\"Troll\"}\n\n");
        }
        server.Stop();
        System.Console.WriteLine(failures == 0 ? "live check: all passed" : $"live check: {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    /* The portal list without Unity: keys by position, change tracking, the loaded-area sweep and the saved file. */
    static void CheckPortalBook()
    {
        var book = new PortalBook("123");
        var a = new PortalRecord { Prefab = "portal_wood", X = 1.04f, Y = 2f, Z = -3.06f, Tag = "Mine" };
        var b = new PortalRecord { Prefab = "portal_stone", X = 500f, Y = 20f, Z = 600f, Tag = "Mine" };
        Check("portal key", a.Key == "portal_wood@1.0,2.0,-3.1", a.Key);
        Check("new portal changes the list", book.Observe(a) && book.TakeChanged(), "");
        var first = book.Version;
        Check("same portal again is no change", !book.Observe(new PortalRecord { Prefab = "portal_wood", X = 1.04f, Y = 2f, Z = -3.06f, Tag = "Mine" }) && !book.TakeChanged(), "");
        a = new PortalRecord { Prefab = "portal_wood", X = 1.04f, Y = 2f, Z = -3.06f, Tag = "Mine", Linked = true, To = b.Key };
        book.Observe(a); book.Observe(b);
        Check("a connection changes the version", book.TakeChanged() && book.Version != first, $"{book.Version}");
        var present = new HashSet<string> { a.Key };
        Check("far missing portal is kept", !book.Sweep(present, _ => false, 60f, false) && book.Count == 2, $"{book.Count}");
        Check("near missing portal waits", !book.Sweep(present, _ => true, 4f, false) && book.Count == 2, $"{book.Count}");
        Check("walking away resets the wait", !book.Sweep(present, _ => false, 2f, false) && !book.Sweep(present, _ => true, 8f, false) && book.Count == 2, $"{book.Count}");
        Check("near missing portal is dropped", book.Sweep(present, _ => true, 2f, false) && book.Get(b.Key) == null && book.Count == 1, $"{book.Count}");
        book.Observe(b);
        Check("a complete world drops what it lacks", book.Sweep(new HashSet<string> { b.Key }, _ => false, 2f, true) && book.Count == 1 && book.Get(b.Key) != null, $"{book.Count}");
        book.Observe(a);
        Check("destroyed portal is removed", book.Remove(b.Key) && !book.Remove(b.Key) && book.Count == 1, $"{book.Count}");
        book.Observe(b);
        var saved = book.ToJson();
        var loaded = PortalBook.Load(saved, "123");
        Check("saved list loads with the same keys", loaded.Count == 2 && loaded.Get(a.Key)?.To == b.Key && loaded.Get(b.Key)?.Tag == "Mine", saved);
        Check("saved list keeps its version", loaded.Version == book.Version, $"{loaded.Version} {book.Version}");
        Check("another world's file is ignored", PortalBook.Load(saved, "999").Count == 0, "");
        Check("an unreadable file is ignored", PortalBook.Load("{nope", "123").Count == 0, "");
        var odd = new PortalRecord { Prefab = "portal", X = 12.35f, Y = -0.05f, Z = 1e-3f, Tag = "<b>x</b> \"q\"" };
        var oddBook = new PortalBook("1"); oddBook.Observe(odd);
        Check("exact floats and any tag survive the file", PortalBook.Load(oddBook.ToJson(), "1").Get(odd.Key)?.Tag == odd.Tag, oddBook.ToJson());
    }

    /* The building book without Unity (mod 1.5): keys to the centimetre, turns, the loaded-area sweep and the saved file. */
    static void CheckBuildingBook()
    {
        var book = new BuildingBook("7");
        var wall = book.Observe("wood_wall", 10.004f, 30f, -5f, 0f, 0.7071f, 0f, 0.7071f, 42L);
        var floor = book.Observe("wood_floor", 10f, 29.5f, -5f, 0f, 0f, 0f, 1f, 42L);
        Check("new pieces change the book", book.TakeChanged() && book.Count == 2, $"{book.Count}");
        var first = book.Version;
        Check("same piece a hair away is the same piece", book.Observe("wood_wall", 10.001f, 30f, -5f, 0f, 0.7071f, 0f, 0.7071f, 42L).Equals(wall) && book.Count == 2, "");
        book.TakeChanged(); first = book.Version;
        Check("same piece again is no change", book.Observe("wood_wall", 10.001f, 30f, -5f, 0f, 0.7071f, 0f, 0.7071f, 42L).Equals(wall) && !book.TakeChanged() && book.Version == first, "");
        book.Observe("wood_wall", 10.004f, 30f, -5f, 0f, 0f, 0f, 1f, 42L);
        Check("a turned piece changes the version", book.TakeChanged() && book.Version != first && book.Count == 2, $"{book.Count}");
        var present = new HashSet<PieceKey> { wall };
        Check("far missing piece is kept", !book.Sweep(present, (x, z) => false, 60f, false) && book.Count == 2, $"{book.Count}");
        Check("near missing piece waits", !book.Sweep(present, (x, z) => true, 4f, false) && book.Count == 2, $"{book.Count}");
        Check("near missing piece is dropped", book.Sweep(present, (x, z) => true, 8f, false) && book.Count == 1, $"{book.Count}");
        book.Observe("wood_floor", 10f, 29.5f, -5f, 0f, 0f, 0f, 1f, 7L);
        Check("destroyed piece is removed", book.Remove("wood_floor", 10f, 29.5f, -5f) && !book.Remove("wood_floor", 10f, 29.5f, -5f) && book.Count == 1, $"{book.Count}");
        book.Observe("stone_wall_2x1", -1234.56f, 80.25f, 987.65f, 0.1f, 0.2f, 0.3f, 0.9274f, 7L);
        var saved = BuildingBook.ToFile(book.Freeze());
        var loaded = BuildingBook.Load(saved, "7");
        Check("saved book loads", loaded.Count == 2 && !loaded.TakeChanged(), saved);
        var rows = loaded.Freeze();
        Check("saved book keeps prefab and creator", rows.Rows.Any(r => rows.Prefabs[r.Prefab] == "stone_wall_2x1" && rows.Creators[r.Creator] == 7L && Math.Abs(r.X + 1234.56f) < 0.01f), saved);
        Check("another world's buildings are ignored", BuildingBook.Load(saved, "8").Count == 0, "");
        Check("an unreadable buildings file is ignored", BuildingBook.Load("{nope", "7").Count == 0, "");
    }

    /* The track body without Unity (mod 1.4): labels cleaned of rich-text brackets, bounds, and the distance text. */
    static void CheckTrackArgs()
    {
        var args = TrackArgs.Parse("{\"x\":-10,\"z\":20.5,\"label\":\"<b>Base</b>\\u0007\"}", out var problem);
        Check("track label loses tags and control characters", args != null && args.Label == "b Base /b" && args.ClearWithin == null && args.X == -10f, problem ?? args?.Label);
        Check("track label too long", TrackArgs.Parse("{\"x\":1,\"z\":2,\"label\":\"" + new string('a', 65) + "\"}", out _) == null, "");
        Check("track without a position", TrackArgs.Parse("{\"label\":\"a\"}", out _) == null, "");
        Check("track label not a string", TrackArgs.Parse("{\"x\":1,\"z\":2,\"label\":5}", out _) == null, "");
        Check("track clear radius 0 is kept", TrackArgs.Parse("{\"x\":1,\"z\":2,\"clearWithin\":0}", out _)?.ClearWithin == 0f, "");
        Check("track clear radius too far", TrackArgs.Parse("{\"x\":1,\"z\":2,\"clearWithin\":5000}", out _) == null, "");
        var distances = new[] { TrackArgs.Distance(0f), TrackArgs.Distance(42.4f), TrackArgs.Distance(999.4f), TrackArgs.Distance(999.6f), TrackArgs.Distance(12345f) };
        Check("track distance text", string.Join("|", distances) == "0 m|42 m|999 m|1.0 km|12.3 km", string.Join("|", distances));
    }

    static string Send(string method, string path, string host, string origin, string body = null, string type = "application/json")
    {
        using var client = new TcpClient("127.0.0.1", Port);
        var stream = client.GetStream();
        var bytes = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body);
        var head = $"{method} {path} HTTP/1.1\r\nHost: {host}\r\n" + (origin == null ? "" : $"Origin: {origin}\r\n") +
                   (body == null ? "" : $"Content-Type: {type}\r\nContent-Length: {bytes.Length}\r\n") + "\r\n";
        var all = Encoding.ASCII.GetBytes(head).Concat(bytes).ToArray();
        stream.Write(all, 0, all.Length);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        client.ReceiveTimeout = 10000;
        try { return reader.ReadToEnd(); } catch (IOException) { return ""; }
    }

    static void Expect(string name, string response, params string[] parts)
    {
        var missing = parts.Where(p => !response.Contains(p)).ToArray();
        Check(name, missing.Length == 0, missing.Length == 0 ? "" : "missing " + string.Join(" | ", missing) + "\n    got: " + response.Replace("\r\n", "\\r\\n").Replace("\n", "\\n"));
    }

    static void Check(string name, bool ok, string detail)
    {
        if (!ok) failures++;
        System.Console.WriteLine((ok ? "ok   " : "FAIL ") + name + (ok ? "" : ": " + detail));
    }
}
