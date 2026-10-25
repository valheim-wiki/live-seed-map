using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BepInEx.Logging;

namespace LiveSeedMap
{
    /// <summary>
    /// HTTP/1.1 on 127.0.0.1 only. GET /v1/state answers with the next snapshot; GET /v1/stream sends every snapshot as a
    /// server-sent event, and a "focus" event when the player presses the show-on-map key. GET /v1/explored answers with
    /// the explored planes of the player's map, and GET /v1/nearby (debug) the pickables, trees and rocks loaded around
    /// the player, both read on the main thread. POST and DELETE /v1/track (mod 1.4) show or clear a target in the game as
    /// a ping, on the main thread, only while the AllowTracking setting is on. GET /v1/portals answers with every portal
    /// the mod remembers for the loaded world (Portals.Json, kept current by the main thread). POST /v1/pins,
    /// DELETE /v1/pins and POST /v1/pins/checked change the player's own pins on the main thread, only while the
    /// AllowPinWrites setting is on. A browser page reaches it only from an allowed Origin, and the Host must name the
    /// loopback address so a rebound domain cannot.
    /// </summary>
    public sealed class LiveServer
    {
        // Node.js's default --max-http-header-size; browsers send far less.
        const int MaxHeaderBytes = 16384;
        // A pin write is one small JSON object.
        const int MaxBodyBytes = 4096;
        // A larger body is read and dropped up to here so its 413 arrives; beyond it the connection just closes.
        const int MaxDiscardBytes = 65536;
        // How long a write waits for the main thread; a loading screen can hold frames for a few seconds.
        const int WriteTimeoutMs = 5000;

        readonly int port;
        readonly HashSet<string> origins;
        readonly ManualLogSource log;
        readonly object gate = new object();
        TcpListener listener;
        volatile bool running;
        Snapshot latest = new Snapshot();
        long revision;
        string focus;
        long focusRevision;
        int clients;
        readonly ConcurrentQueue<PinCommand> commands = new ConcurrentQueue<PinCommand>();
        readonly ConcurrentQueue<ExploredRequest> exploredRequests = new ConcurrentQueue<ExploredRequest>();
        readonly ConcurrentQueue<NearbyRequest> nearbyRequests = new ConcurrentQueue<NearbyRequest>();
        readonly ConcurrentQueue<TrackCommand> trackCommands = new ConcurrentQueue<TrackCommand>();

        /// <summary>The AllowPinWrites setting, read on the request thread.</summary>
        public Func<bool> PinWritesAllowed = () => false;
        /// <summary>GET /v1/portals (mod 1.3): the remembered portals of the loaded world, null without one. Read on the request thread.</summary>
        public Func<string> PortalsJson = () => null;
        /// <summary>GET /v1/buildings (mod 1.5): the remembered pieces of the loaded world, null without one. Read on the request thread.</summary>
        public Func<string> BuildingsJson = () => null;
        /// <summary>The AllowTracking setting (mod 1.4), read on the request thread.</summary>
        public Func<bool> TrackingAllowed = () => false;

        public LiveServer(int port, IEnumerable<string> origins, ManualLogSource log)
        {
            this.port = port;
            this.origins = new HashSet<string>(origins.Select(o => o.Trim().TrimEnd('/')).Where(o => o.Length > 0), StringComparer.OrdinalIgnoreCase);
            this.log = log;
        }

        /// <summary>A stream is open or a state request waits: the game should capture.</summary>
        public bool HasClients => Volatile.Read(ref clients) > 0;

        public void Start()
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            running = true;
            new Thread(Accept) { IsBackground = true, Name = "LiveSeedMap accept" }.Start();
            log.LogInfo($"Listening on http://127.0.0.1:{port}");
        }

        public void Stop()
        {
            running = false;
            listener?.Stop();
            lock (gate) Monitor.PulseAll(gate);
        }

        public void Publish(Snapshot snapshot)
        {
            lock (gate) { latest = snapshot; revision++; Monitor.PulseAll(gate); }
        }

        /// <summary>Sends one focus event to every open stream; a newer focus replaces one not yet sent.</summary>
        public void PublishFocus(FocusTarget target)
        {
            var json = Json.Focus(target);
            lock (gate) { focus = json; focusRevision++; Monitor.PulseAll(gate); }
        }

        /// <summary>The next pin write waiting for the main thread.</summary>
        public bool TryTakeCommand(out PinCommand command) => commands.TryDequeue(out command);

        /// <summary>The next read of the explored planes waiting for the main thread.</summary>
        public bool TryTakeExplored(out ExploredRequest request) => exploredRequests.TryDequeue(out request);

        /// <summary>The next read of the objects around the player waiting for the main thread.</summary>
        public bool TryTakeNearby(out NearbyRequest request) => nearbyRequests.TryDequeue(out request);

        /// <summary>The next track request (mod 1.4) waiting for the main thread.</summary>
        public bool TryTakeTrack(out TrackCommand command) => trackCommands.TryDequeue(out command);

        void Accept()
        {
            while (running)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (SocketException) { if (running) continue; return; }
                catch (ObjectDisposedException) { return; }
                new Thread(() => Serve(client)) { IsBackground = true, Name = "LiveSeedMap client" }.Start();
            }
        }

        void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    var stream = client.GetStream();
                    var request = Request.Read(stream);
                    if (request == null || !request.ReadBody(stream)) return;
                    Respond(stream, request);
                }
                catch (IOException) { }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }
        }

        void Respond(Stream stream, Request request)
        {
            request.Headers.TryGetValue("origin", out var origin);
            request.Headers.TryGetValue("host", out var host);
            var hostAllowed = host == $"127.0.0.1:{port}" || host == $"localhost:{port}";
            if (!hostAllowed || (origin != null && !origins.Contains(origin)))
            {
                Write(stream, "403 Forbidden", null, "text/plain", "Forbidden");
                return;
            }
            if (request.Method == "OPTIONS")
            {
                Write(stream, "204 No Content", origin, null, null,
                    "Access-Control-Allow-Methods: GET, POST, DELETE\r\nAccess-Control-Allow-Headers: Content-Type\r\nAccess-Control-Allow-Private-Network: true\r\nAccess-Control-Max-Age: 600\r\n");
                return;
            }
            if ((request.Path == "/v1/pins" && (request.Method == "POST" || request.Method == "DELETE")) || (request.Path == "/v1/pins/checked" && request.Method == "POST"))
            {
                PinWrite(stream, request, origin);
                return;
            }
            if (request.Path == "/v1/track" && (request.Method == "POST" || request.Method == "DELETE"))
            {
                TrackWrite(stream, request, origin);
                return;
            }
            var range = request.Range;
            if (request.Method == "GET" && request.Path == "/v1/state")
            {
                Interlocked.Increment(ref clients);
                try
                {
                    long seen;
                    lock (gate) seen = revision;
                    var snapshot = Next(ref seen);
                    if (snapshot != null) Write(stream, "200 OK", origin, "application/json", Json.Write(snapshot, range));
                }
                finally { Interlocked.Decrement(ref clients); }
                return;
            }
            if (request.Method == "GET" && request.Path == "/v1/stream")
            {
                Stream(stream, origin, range);
                return;
            }
            if (request.Method == "GET" && request.Path == "/v1/explored")
            {
                ExploredRead(stream, origin);
                return;
            }
            if (request.Method == "GET" && request.Path == "/v1/nearby")
            {
                NearbyRead(stream, origin, range);
                return;
            }
            if (request.Method == "GET" && request.Path == "/v1/buildings")
            {
                var buildings = BuildingsJson();
                if (buildings == null) Write(stream, "409 Conflict", origin, "application/json", Json.Error("no-world", "No world is loaded."));
                else Write(stream, "200 OK", origin, "application/json", buildings);
                return;
            }
            if (request.Method == "GET" && request.Path == "/v1/portals")
            {
                var portals = PortalsJson();
                if (portals == null) Write(stream, "409 Conflict", origin, "application/json", Json.Error("no-world", "No world is loaded."));
                else Write(stream, "200 OK", origin, "application/json", portals);
                return;
            }
            Write(stream, "404 Not Found", origin, "text/plain", "Not found");
        }

        void Stream(Stream stream, string origin, float range)
        {
            var head = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\nConnection: keep-alive\r\n" + Cors(origin) + "\r\n";
            var bytes = Encoding.ASCII.GetBytes(head);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
            Interlocked.Increment(ref clients);
            try
            {
                long seen, focusSeen;
                lock (gate) { seen = revision; focusSeen = focusRevision; }
                while (true)
                {
                    string text;
                    Snapshot snapshot = null;
                    lock (gate)
                    {
                        while (running && revision == seen && focusRevision == focusSeen) Monitor.Wait(gate);
                        if (!running) return;
                        if (focusRevision != focusSeen) { focusSeen = focusRevision; text = "event: focus\ndata: " + focus + "\n\n"; }
                        else { seen = revision; snapshot = latest; text = null; }
                    }
                    var frame = Encoding.UTF8.GetBytes(text ?? "data: " + Json.Write(snapshot, range) + "\n\n");
                    stream.Write(frame, 0, frame.Length);
                    stream.Flush();
                }
            }
            finally { Interlocked.Decrement(ref clients); }
        }

        /// <summary>
        /// A pin write: refused with 403 while AllowPinWrites is off, 415 unless the body is JSON (which also makes a
        /// browser ask first with a preflight), then run on the main thread and answered with its result.
        /// </summary>
        void PinWrite(Stream stream, Request request, string origin)
        {
            if (!PinWritesAllowed())
            {
                Write(stream, "403 Forbidden", origin, "application/json",
                    Json.Error("pin-writes-off", "Pin writes are off in this game. Set AllowPinWrites = true in BepInEx/config/" + Plugin.Guid + ".cfg to let the seed map change your pins."));
                return;
            }
            request.Headers.TryGetValue("content-type", out var type);
            if (type == null || !type.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase))
            {
                Write(stream, "415 Unsupported Media Type", origin, "application/json", Json.Error("unsupported-media", "Send the pin as application/json."));
                return;
            }
            if (request.Length < 0)
            {
                Write(stream, "411 Length Required", origin, "application/json", Json.Error("length-required", "Content-Length is required."));
                return;
            }
            if (request.Body == null)
            {
                Write(stream, "413 Payload Too Large", origin, "application/json", Json.Error("too-large", "A pin is at most " + MaxBodyBytes + " bytes."));
                return;
            }
            var text = Encoding.UTF8.GetString(request.Body);
            var action = request.Method == "DELETE" ? PinCommand.Kind.Remove : request.Path == "/v1/pins/checked" ? PinCommand.Kind.Check : PinCommand.Kind.Add;
            var pin = PinArgs.Parse(text, out var problem);
            if (pin == null || (action == PinCommand.Kind.Check && pin.Checked == null))
            {
                Write(stream, "400 Bad Request", origin, "application/json", problem ?? Json.Error("bad-request", "\"checked\" must be true or false."));
                return;
            }
            var command = new PinCommand { Action = action, Body = text };
            commands.Enqueue(command);
            if (!command.Done.Wait(WriteTimeoutMs))
            {
                command.Abandoned = true;
                Write(stream, "503 Service Unavailable", origin, "application/json", Json.Error("busy", "The game did not answer in time; try again."));
                return;
            }
            var status = command.Status == 200 ? "200 OK" : command.Status == 400 ? "400 Bad Request" : command.Status == 409 ? "409 Conflict" : "500 Internal Server Error";
            Write(stream, status, origin, "application/json", command.Response);
        }

        /// <summary>
        /// POST /v1/track shows a target in the game as a ping, DELETE /v1/track clears it (mod 1.4): refused with 403
        /// while AllowTracking is off, then run on the main thread like a pin write. POST takes a JSON body; DELETE takes
        /// none.
        /// </summary>
        void TrackWrite(Stream stream, Request request, string origin)
        {
            if (!TrackingAllowed())
            {
                Write(stream, "403 Forbidden", origin, "application/json",
                    Json.Error("tracking-off", "Tracking is off in this game. Set AllowTracking = true in BepInEx/config/" + Plugin.Guid + ".cfg to let the seed map show a target in the game."));
                return;
            }
            var command = new TrackCommand();
            if (request.Method == "POST")
            {
                request.Headers.TryGetValue("content-type", out var type);
                if (type == null || !type.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase))
                {
                    Write(stream, "415 Unsupported Media Type", origin, "application/json", Json.Error("unsupported-media", "Send the target as application/json."));
                    return;
                }
                if (request.Length < 0)
                {
                    Write(stream, "411 Length Required", origin, "application/json", Json.Error("length-required", "Content-Length is required."));
                    return;
                }
                if (request.Body == null)
                {
                    Write(stream, "413 Payload Too Large", origin, "application/json", Json.Error("too-large", "A target is at most " + MaxBodyBytes + " bytes."));
                    return;
                }
                command.Target = TrackArgs.Parse(Encoding.UTF8.GetString(request.Body), out var problem);
                if (command.Target == null)
                {
                    Write(stream, "400 Bad Request", origin, "application/json", problem);
                    return;
                }
            }
            trackCommands.Enqueue(command);
            if (!command.Done.Wait(WriteTimeoutMs))
            {
                command.Abandoned = true;
                Write(stream, "503 Service Unavailable", origin, "application/json", Json.Error("busy", "The game did not answer in time; try again."));
                return;
            }
            var status = command.Status == 200 ? "200 OK" : command.Status == 409 ? "409 Conflict" : "500 Internal Server Error";
            Write(stream, status, origin, "application/json", command.Response);
        }

        /// <summary>The explored planes: handed to the main thread like a pin write, 409 without a map (main menu).</summary>
        void ExploredRead(Stream stream, string origin)
        {
            var read = new ExploredRequest();
            exploredRequests.Enqueue(read);
            if (!read.Done.Wait(WriteTimeoutMs))
            {
                read.Abandoned = true;
                Write(stream, "503 Service Unavailable", origin, "application/json", Json.Error("busy", "The game did not answer in time; try again."));
                return;
            }
            if (read.Planes == null)
            {
                Write(stream, "409 Conflict", origin, "application/json", Json.Error("no-world", "No world is loaded."));
                return;
            }
            Write(stream, "200 OK", origin, "application/json", Json.Explored(read.Planes));
        }

        /// <summary>The objects around the player (debug), on the main thread; 409 without a player.</summary>
        void NearbyRead(Stream stream, string origin, float range)
        {
            var read = new NearbyRequest { Range = range };
            nearbyRequests.Enqueue(read);
            if (!read.Done.Wait(WriteTimeoutMs))
            {
                read.Abandoned = true;
                Write(stream, "503 Service Unavailable", origin, "application/json", Json.Error("busy", "The game did not answer in time; try again."));
                return;
            }
            if (read.Json == null)
            {
                Write(stream, "409 Conflict", origin, "application/json", Json.Error("no-player", "No character is in a world."));
                return;
            }
            Write(stream, "200 OK", origin, "application/json", read.Json);
        }

        /// <summary>The first snapshot published after revision <paramref name="seen"/>, or null once the server stops.</summary>
        Snapshot Next(ref long seen)
        {
            lock (gate)
            {
                while (running && revision == seen) Monitor.Wait(gate);
                if (!running) return null;
                seen = revision;
                return latest;
            }
        }

        static string Cors(string origin) => origin == null ? "" : $"Access-Control-Allow-Origin: {origin}\r\nVary: Origin\r\n";

        static void Write(Stream stream, string status, string origin, string type, string body, string extra = "")
        {
            var content = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body);
            var head = $"HTTP/1.1 {status}\r\n" + Cors(origin) + extra + (type == null ? "" : $"Content-Type: {type}; charset=utf-8\r\nCache-Control: no-store\r\n") +
                       $"Content-Length: {content.Length}\r\nConnection: close\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(head);
            stream.Write(bytes, 0, bytes.Length);
            stream.Write(content, 0, content.Length);
            stream.Flush();
        }

        sealed class Request
        {
            public string Method, Path;
            public float Range = float.PositiveInfinity;
            public readonly Dictionary<string, string> Headers = new Dictionary<string, string>();
            /// <summary>Content-Length, or -1 without one.</summary>
            public int Length = -1;
            /// <summary>The body when it fits MaxBodyBytes; null when larger (then read and dropped).</summary>
            public byte[] Body = new byte[0];

            /// <summary>
            /// Reads the whole body before any answer, so a refusal never closes a socket with unread bytes (which
            /// resets the connection and loses the answer). A body above MaxDiscardBytes is not read: false, and the
            /// connection closes unanswered.
            /// </summary>
            public bool ReadBody(Stream stream)
            {
                if (!Headers.TryGetValue("content-length", out var text)) return true;
                if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out Length) || Length > MaxDiscardBytes) return false;
                var body = new byte[Length];
                for (var read = 0; read < Length;)
                {
                    var got = stream.Read(body, read, Length - read);
                    if (got == 0) return false;
                    read += got;
                }
                Body = Length <= MaxBodyBytes ? body : null;
                return true;
            }

            public static Request Read(Stream stream)
            {
                var buffer = new List<byte>();
                var one = new byte[1];
                while (!EndsWithBlankLine(buffer))
                {
                    if (buffer.Count >= MaxHeaderBytes || stream.Read(one, 0, 1) == 0) return null;
                    buffer.Add(one[0]);
                }
                var lines = Encoding.ASCII.GetString(buffer.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
                var parts = lines[0].Split(' ');
                if (parts.Length < 2) return null;
                var target = parts[1].Split(new[] { '?' }, 2);
                var request = new Request { Method = parts[0], Path = target[0] };
                if (target.Length == 2)
                    foreach (var pair in target[1].Split('&'))
                    {
                        var kv = pair.Split(new[] { '=' }, 2);
                        if (kv.Length == 2 && kv[0] == "range" && float.TryParse(Uri.UnescapeDataString(kv[1]), NumberStyles.Float, CultureInfo.InvariantCulture, out var range) && range >= 0f)
                            request.Range = range;
                    }
                foreach (var line in lines.Skip(1))
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0) request.Headers[line.Substring(0, colon).Trim().ToLowerInvariant()] = line.Substring(colon + 1).Trim();
                }
                return request;
            }

            static bool EndsWithBlankLine(List<byte> b) =>
                b.Count >= 4 && b[b.Count - 4] == '\r' && b[b.Count - 3] == '\n' && b[b.Count - 2] == '\r' && b[b.Count - 1] == '\n';
        }
    }

    /// <summary>The explored planes of the player's map, packed eight pixels a byte (least significant bit first).</summary>
    public sealed class ExploredPlanes
    {
        public int Size, Version;
        public byte[] Explored, Others;
    }

    /// <summary>A read of the explored planes the server thread hands to the main thread and waits on.</summary>
    public sealed class ExploredRequest
    {
        /// <summary>Null when no map is loaded.</summary>
        public ExploredPlanes Planes;
        public volatile bool Abandoned;
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
    }

    /// <summary>A read of the objects around the player (GET /v1/nearby) the server thread hands to the main thread.</summary>
    public sealed class NearbyRequest
    {
        /// <summary>The ?range= of the request, infinity without one.</summary>
        public float Range;
        /// <summary>The answer; null without a player.</summary>
        public string Json;
        public volatile bool Abandoned;
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
    }
}
