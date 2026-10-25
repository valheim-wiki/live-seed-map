using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace LiveSeedMap
{
    /// <summary>
    /// Remembers every portal the game has loaded (mod 1.3), per world, across sessions, for GET /v1/portals. Main thread.
    ///
    /// The portals a game holds are ZDOMan.GetPortalList (ZDOMan.m_portalObjects: every ZDO whose prefab is in
    /// Game.PortalPrefabHash, filled by AddIfPortal as ZDOs arrive). A client of a dedicated server holds only what the
    /// server sent it: the portals around the player and those it asked for. For a portal whose connection target it does
    /// not hold, the mod asks the server as TeleportWorld.TargetFound does, through ZDOMan.RequestZDO (routed RPC
    /// "RequestZDO"; the server answers with ZDOPeer.ForceSendZDO), at most every RequestAgain seconds per target.
    /// A portal leaves the list when the game reports it destroyed (ZDOMan.m_onZDODestroyed), or when its ground is loaded
    /// around the player and the game still does not hold it (PortalBook.Sweep). The list is saved as JSON under
    /// BepInEx/config/LiveSeedMap/portals-&lt;world uid&gt;.json.
    /// </summary>
    public static class Portals
    {
        const float ScanEvery = 2f;
        const float SaveEvery = 10f;
        const float RequestAgain = 30f;
        const int RequestsPerScan = 8;

        static PortalBook book;
        static long worldUid;
        static ZDOMan hooked;
        static Game namesOf;
        static readonly Dictionary<int, string> names = new Dictionary<int, string>();
        static readonly Dictionary<ZDOID, float> requested = new Dictionary<ZDOID, float>();
        static float nextScan, nextSave;
        static bool unsaved;
        static volatile string json;
        static ManualLogSource log;

        /// <summary>GET /v1/portals, or null without a world. Read from any thread.</summary>
        public static string Json => json;
        /// <summary>The list's version for the snapshot, or null without a world.</summary>
        public static int? Version => book?.Version;

        public static void Tick(float now, ManualLogSource logger)
        {
            log = logger;
            var world = ZNet.instance ? (World)Capture.WorldField.GetValue(null) : null;
            var zdos = ZDOMan.instance;
            if (world == null || zdos == null || !Game.instance || !ZoneSystem.instance) { Leave(); return; }
            if (book == null || world.m_uid != worldUid) Join(world.m_uid);
            if (hooked != zdos)
            {
                if (hooked != null) hooked.m_onZDODestroyed -= Destroyed;
                zdos.m_onZDODestroyed += Destroyed;
                hooked = zdos;
                requested.Clear();
            }
            if (now >= nextScan)
            {
                nextScan = now + ScanEvery;
                Scan(zdos, now);
            }
            if (book.TakeChanged()) { json = book.ToJson(); unsaved = true; }
            if (unsaved && now >= nextSave) { Save(); nextSave = now + SaveEvery; }
        }

        /// <summary>Saves the list now (the plugin unloads).</summary>
        public static void Flush() => Save();

        static void Join(long uid)
        {
            Leave();
            worldUid = uid;
            var world = uid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string text = null;
            try { if (File.Exists(FileOf(world))) text = File.ReadAllText(FileOf(world)); }
            catch (Exception error) { log?.LogWarning($"Could not read the saved portals: {error.Message}"); }
            book = PortalBook.Load(text, world);
            json = book.ToJson();
            nextScan = 0f;
        }

        static void Leave()
        {
            if (book == null) return;
            Save();
            if (hooked != null) hooked.m_onZDODestroyed -= Destroyed;
            hooked = null;
            book = null;
            json = null;
            requested.Clear();
        }

        static string FileOf(string world) => Path.Combine(Path.Combine(Paths.ConfigPath, "LiveSeedMap"), $"portals-{world}.json");

        static void Save()
        {
            if (book == null || !unsaved) return;
            unsaved = false;
            try
            {
                var file = FileOf(book.World);
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                var temp = file + ".tmp";
                File.WriteAllText(temp, book.ToJson());
                if (File.Exists(file)) File.Delete(file);
                File.Move(temp, file);
            }
            catch (Exception error) { log?.LogWarning($"Could not save the portals: {error.Message}"); }
        }

        /// <summary>Game.m_portalPrefabs by prefab hash, as Game.PortalPrefabHash lists them.</summary>
        static Dictionary<int, string> Names()
        {
            if (namesOf == Game.instance) return names;
            names.Clear();
            foreach (var prefab in Game.instance.m_portalPrefabs)
                if (prefab) names[prefab.name.GetStableHashCode()] = prefab.name;
            namesOf = Game.instance;
            return names;
        }

        static void Scan(ZDOMan zdos, float now)
        {
            var prefabs = Names();
            var present = new HashSet<string>(StringComparer.Ordinal);
            var asks = 0;
            var server = ZNet.instance.IsServer();
            foreach (var zdo in zdos.GetPortalList())
            {
                if (zdo == null || !prefabs.TryGetValue(zdo.GetPrefab(), out var prefab)) continue;
                var position = zdo.GetPosition();
                var record = new PortalRecord { Prefab = prefab, X = position.x, Y = position.y, Z = position.z, Tag = zdo.GetString(ZDOVars.s_tag) };
                var key = record.Key;
                present.Add(key);
                var connection = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
                if (!connection.IsNone())
                {
                    record.Linked = true;
                    var target = zdos.GetZDO(connection);
                    if (target != null && prefabs.TryGetValue(target.GetPrefab(), out var targetPrefab))
                    {
                        var at = target.GetPosition();
                        record.To = PortalBook.Key(targetPrefab, at.x, at.y, at.z);
                    }
                    else
                    {
                        /* Seen in an earlier session; kept until the target arrives. */
                        var known = book.Get(key);
                        if (known != null && known.Linked && known.Tag == record.Tag) record.To = known.To;
                        if (target == null && !server && asks < RequestsPerScan && (!requested.TryGetValue(connection, out var asked) || now - asked >= RequestAgain))
                        {
                            zdos.RequestZDO(connection);
                            requested[connection] = now;
                            asks++;
                        }
                    }
                }
                book.Observe(record);
            }
            var player = Player.m_localPlayer;
            var reach = Capture.Reach(ZNet.instance.GetSyncedSimulationDistance(), ZoneSystem.instance.m_zoneSize) * 0.9f;
            var here = player ? player.transform.position : Vector3.zero;
            book.Sweep(present, portal => player && Sq(portal.X - here.x) + Sq(portal.Z - here.z) <= reach * reach, ScanEvery, server);
        }

        static float Sq(float value) => value * value;

        static void Destroyed(ZDO zdo)
        {
            try
            {
                if (book == null || zdo == null || !Names().TryGetValue(zdo.GetPrefab(), out var prefab)) return;
                var position = zdo.GetPosition();
                book.Remove(PortalBook.Key(prefab, position.x, position.y, position.z));
            }
            catch (Exception error) { log?.LogError(error); }
        }
    }
}
