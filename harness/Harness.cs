using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace LiveHarness
{
    /// <summary>
    /// Drives a single-player session in the installed client for the live link proof (mod/run.ps1). Inert unless
    /// harness.txt sits beside the BepInEx folder: the seed, then one survey stop per line ("x z fish"); every save goes
    /// under that folder, never the player's own. At each stop it writes the game's own SpawnSystem.IsSpawnPointGood for
    /// every fish row at every 12 m map pixel centre of the loaded 3 x 3 zones (fish-native.jsonl), the check of
    /// worldgen/src/fish-habitat.ts. The rows are cloned with m_canSpawnCloseToPlayer, and ZoneSystem.IsBlocked is
    /// recorded per point: both are runtime conditions the map does not model. A second player cannot join a harness
    /// session, so ZNet's own player list gains one public and one private player beside the start (AddPlayers);
    /// ZNet.GetOtherPublicPlayers must pass the first and filter the second.
    /// </summary>
    [BepInPlugin("wiki.thevalheim.liveharness", "Live link harness", "1.0.0")]
    public sealed class Harness : BaseUnityPlugin
    {
        string root, seed;
        readonly List<Vector3> stops = new List<Vector3>();
        static Vector3? others;
        int stop = -1;
        bool departed;
        StreamWriter survey;
        bool started;
        float clock, next;
        int step;
        Vector3 start;

        void Awake()
        {
            root = Path.GetDirectoryName(Paths.BepInExRootPath);
            var settings = Path.Combine(root, "harness.txt");
            if (!File.Exists(settings)) { enabled = false; return; }
            var lines = File.ReadAllLines(settings).Where(line => line.Trim().Length > 0).ToArray();
            seed = lines[0].Trim();
            foreach (var line in lines.Skip(1))
            {
                var parts = line.Split(' ');
                stops.Add(new Vector3(float.Parse(parts[0], CultureInfo.InvariantCulture), 0f, float.Parse(parts[1], CultureInfo.InvariantCulture)));
            }
            global::Utils.SetSaveDataPath(Path.Combine(root, "saves"));
            new Harmony("wiki.thevalheim.liveharness").Patch(AccessTools.Method(typeof(ZNet), "UpdatePlayerList"), postfix: new HarmonyMethod(typeof(Harness), nameof(AddPlayers)));
            Logger.LogInfo("harness: saves in " + Path.Combine(root, "saves"));
        }

        void Update()
        {
            if (!started)
            {
                if (!FejdStartup.instance) return;
                started = true;
                var profile = new PlayerProfile("harness", FileHelpers.FileSource.Local);
                profile.SetName("Harness");
                profile.m_firstSpawn = false;
                profile.Save();
                Game.SetProfile("harness", FileHelpers.FileSource.Local);
                var world = new World("harness", seed);
                ZNet.SetServer(server: true, openServer: false, publicServer: false, "harness", "", world);
                ZNet.ResetServerHost();
                AccessTools.Method(typeof(FejdStartup), "LoadMainScene").Invoke(FejdStartup.instance, null);
                Logger.LogInfo("harness: loading world " + seed);
                return;
            }
            var player = Player.m_localPlayer;
            if (!player) return;
            clock += Time.unscaledDeltaTime;
            if (clock < next) return;
            next = clock + 2f;
            Act(player);
        }

        void Act(Player player)
        {
            step++;
            var p = player.transform.position;
            if (step == 1)
            {
                start = p;
                others = start + new Vector3(45f, 0f, 30f);
                Logger.LogInfo($"harness: ready at {p.x:F1},{p.z:F1}");
                Minimap.instance.AddPin(start + new Vector3(30f, 0f, 20f), Minimap.PinType.Icon3, "Harness base", save: true, isChecked: false);
                Minimap.instance.AddPin(start + new Vector3(-40f, 0f, 60f), Minimap.PinType.Icon2, "Copper?", save: true, isChecked: true);
                Spawn("Boar", start + new Vector3(12f, 0f, 6f), 2);
                Spawn("Greyling", start + new Vector3(-18f, 0f, -14f), 1);
                Spawn("Deer", start + new Vector3(20f, 0f, -25f), 1);
                var wolf = Spawn("Wolf", start + new Vector3(-8f, 0f, 16f), 1);
                AccessTools.Method(typeof(Tameable), "Tame").Invoke(wolf.GetComponent<Tameable>(), null);
                Spawn("Wolf_cub", start + new Vector3(-12f, 0f, 22f), 1);
                Spawn("Greydwarf_Shaman", start + new Vector3(26f, 0f, 14f), 3);
                Spawn("Neck", start + new Vector3(-24f, 0f, 4f), 1);
                return;
            }
            if (step <= 10)
            {
                Move(player, start + new Vector3(4f * step, 0f, 3f * step), 40f);
                return;
            }
            if (step == 11 && ZoneSystem.instance.FindClosestLocation("Vendor_BlackForest", p, out var vendor))
            {
                Logger.LogInfo($"harness: nearest Haldor candidate {vendor.m_position.x:F1},{vendor.m_position.z:F1}");
                Move(player, vendor.m_position + new Vector3(24f, 0f, 0f), 270f);
                return;
            }
            if (step >= 20) Survey(player);
        }

        /// <summary>One stop at a time; Player.TeleportTo refuses a teleport within 2 s of the last one, so a refused move is retried.</summary>
        void Survey(Player player)
        {
            if (stop >= 0 && !departed) { departed = Move(player, stops[stop], 0f); return; }
            if (stop >= 0 && !Sample(player)) return;
            stop++;
            if (stop >= stops.Count)
            {
                survey?.Dispose();
                Logger.LogInfo("harness: done");
                Application.Quit(0);
                return;
            }
            departed = Move(player, stops[stop], 0f);
        }

        /// <summary>Samples the stop once its 3 x 3 zones are loaded; false while they are not.</summary>
        bool Sample(Player player)
        {
            if (player.IsTeleporting()) return false;
            var center = ZoneSystem.GetZone(stops[stop]);
            var zones = new List<Vector2s>();
            for (var dz = -1; dz <= 1; dz++) for (var dx = -1; dx <= 1; dx++) zones.Add(new Vector2s(center.x + dx, center.y + dz));
            if (!zones.All(zone => ZoneSystem.instance.IsZoneLoaded(zone))) return false;
            var system = FindObjectOfType<SpawnSystem>();
            var rows = system.m_spawnLists.SelectMany(list => list.m_spawners)
                .Where(row => row.m_enabled && row.m_prefab && row.m_prefab.GetComponent<Fish>()).Select(row =>
                {
                    var clone = row.Clone();
                    clone.m_canSpawnCloseToPlayer = true;
                    clone.m_insidePlayerBase = true;
                    return clone;
                }).ToList();
            var keys = rows.Select(row => row.m_prefab.name).Distinct().ToList();
            var good = AccessTools.Method(typeof(SpawnSystem), "IsSpawnPointGood");
            if (survey == null)
            {
                survey = new StreamWriter(Path.Combine(root, "fish-native.jsonl"));
                survey.WriteLine("{\"keys\":[" + string.Join(",", keys.Select(key => "\"" + key + "\"")) + "]}");
            }
            var points = 0;
            foreach (var zone in zones)
            {
                var origin = ZoneSystem.GetZonePos(zone);
                for (var x = Centre(origin.x - 32f); x < origin.x + 32f; x += 12f)
                for (var z = Centre(origin.z - 32f); z < origin.z + 32f; z += 12f)
                {
                    var bits = 0;
                    foreach (var row in rows)
                    {
                        var args = new object[] { row, new Vector3(x, 0f, z) };
                        if ((bool)good.Invoke(system, args)) bits |= 1 << keys.IndexOf(row.m_prefab.name);
                    }
                    var blocked = ZoneSystem.instance.IsBlocked(new Vector3(x, 0f, z)) ? 1 : 0;
                    survey.WriteLine(string.Format(CultureInfo.InvariantCulture, "{{\"x\":{0},\"z\":{1},\"g\":{2},\"b\":{3}}}", x, z, bits, blocked));
                    points++;
                }
            }
            survey.Flush();
            Logger.LogInfo($"harness: surveyed stop {stop} at {stops[stop].x:F1},{stops[stop].z:F1}: {points} points");
            return true;
        }

        /// <summary>Postfix of ZNet.UpdatePlayerList, which the server runs every 2 s.</summary>
        static void AddPlayers(List<ZNet.PlayerInfo> ___m_players)
        {
            if (!others.HasValue) return;
            ___m_players.Add(new ZNet.PlayerInfo { m_name = "Harness friend", m_characterID = new ZDOID(424242L, 1u), m_publicPosition = true, m_position = others.Value });
            ___m_players.Add(new ZNet.PlayerInfo { m_name = "Harness hidden", m_characterID = new ZDOID(424243L, 1u), m_publicPosition = false, m_position = others.Value + new Vector3(12f, 0f, 0f) });
        }

        /// <summary>The first map pixel centre (6 + 12k m) at or after `edge`.</summary>
        static float Centre(float edge) => 6f + 12f * Mathf.Ceil((edge - 6f) / 12f);

        bool Move(Player player, Vector3 target, float heading)
        {
            ZoneSystem.instance.GetGroundHeight(target, out var height);
            target.y = Mathf.Max(height, ZoneSystem.instance.m_waterLevel) + 1f;
            if (!player.TeleportTo(target, Quaternion.Euler(0f, heading, 0f), distantTeleport: (target - player.transform.position).magnitude > 64f)) return false;
            Logger.LogInfo($"harness: moved to {target.x:F1},{target.z:F1} heading {heading}");
            return true;
        }

        Character Spawn(string prefab, Vector3 at, int level)
        {
            ZoneSystem.instance.GetGroundHeight(at, out var height);
            at.y = height + 0.5f;
            var creature = Instantiate(ZNetScene.instance.GetPrefab(prefab), at, Quaternion.identity).GetComponent<Character>();
            creature.SetLevel(level);
            Logger.LogInfo($"harness: spawned {prefab} level {level} at {at.x:F1},{at.z:F1}");
            return creature;
        }
    }
}
