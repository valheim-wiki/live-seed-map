using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LiveSeedMap
{
    public sealed class WorldInfo { public string Name, Seed; public int Hash, Generation; public long Uid; }
    public sealed class SelfInfo { public string Name; public float X, Y, Z, Heading; public bool Dead; }
    public sealed class PlayerSpot { public string Name; public float X, Z; }
    public sealed class Creature { public string Prefab, Name; public int Level; public float Health, MaxHealth, X, Z; public bool Tamed, Boss, Hostile; }
    public sealed class Pin { public int Type; public string Name; public float X, Z; public bool Checked, Shared; }
    public sealed class Icon { public string Prefab; public float X, Z; }
    /// <summary>Mod 1.2: the ship the player steers or stands on; Name is its Piece.m_name ($ship_karve). Sail is Ship.Speed lowercased: stop, back, slow, half, full.</summary>
    public sealed class ShipInfo { public string Prefab, Name, Sail; public float X, Z, Heading, Speed; }
    /// <summary>Mod 1.2: the world yaw the wind blows toward (0 north, clockwise) and its strength 0..1.</summary>
    public sealed class WindInfo { public float Angle, Intensity; }

    /// <summary>One frame of the game as the player's own map knows it. Immutable once published.</summary>
    public sealed class Snapshot
    {
        public WorldInfo World;
        public SelfInfo Self;
        public float Reach;
        public PlayerSpot[] Players = new PlayerSpot[0];
        public Creature[] Creatures = new Creature[0];
        public Pin[] Pins = new Pin[0];
        public Icon[] Icons = new Icon[0];
        /// <summary>The AllowPinWrites setting, so the page knows before it tries.</summary>
        public bool PinWrites;
        /// <summary>Mod 1.2.1: the ShowOnMapKey setting as the player reads it ("J", "LeftControl + J"); null when set to None.</summary>
        public string FocusKey;
        public ShipInfo Ship;
        public WindInfo Wind;
        /// <summary>Version of the explored planes (Explored.Version); null without a map.</summary>
        public int? ExploredVersion;
        /// <summary>Mod 1.3: version of the remembered portals (GET /v1/portals, Portals.Version); null without a world.</summary>
        public int? PortalsVersion;
        /// <summary>Mod 1.5: version of the remembered buildings (GET /v1/buildings, Buildings.Version); null without a world.</summary>
        public int? BuildingsVersion;
        /// <summary>Mod 1.4: the AllowTracking setting, so the page knows before it tries.</summary>
        public bool Tracking;
        /// <summary>Mod 1.4: the target shown in the game as a ping (POST /v1/track); null without one.</summary>
        public TrackInfo Track;
    }

    /// <summary>
    /// Reads the same state the game's Minimap and ZNet expose, on the main thread. A creature is hostile when
    /// BaseAI.IsEnemy holds against the player and its AI hunts (MonsterAI); AnimalAI creatures such as deer flee.
    /// </summary>
    public static class Capture
    {
        internal static readonly FieldInfo WorldField = AccessTools.Field(typeof(ZNet), "m_world");
        internal static readonly FieldInfo PinsField = AccessTools.Field(typeof(Minimap), "m_pins");

        public static Snapshot Take()
        {
            var snapshot = new Snapshot();
            var world = ZNet.instance ? (World)WorldField.GetValue(null) : null;
            if (world == null || !ZoneSystem.instance) return snapshot;
            snapshot.World = new WorldInfo { Name = world.m_name, Seed = world.m_seedName, Hash = world.m_seed, Uid = world.m_uid, Generation = world.m_worldGenVersion };
            snapshot.Reach = Reach(ZNet.instance.GetSyncedSimulationDistance(), ZoneSystem.instance.m_zoneSize);

            var player = Player.m_localPlayer;
            if (player)
            {
                var position = player.transform.position;
                snapshot.Self = new SelfInfo { Name = player.GetPlayerName(), X = position.x, Y = position.y, Z = position.z,
                    Heading = player.transform.rotation.eulerAngles.y, Dead = player.IsDead() };
                snapshot.Ship = ShipOf(player);
            }
            snapshot.Wind = Wind();
            snapshot.ExploredVersion = Explored.Version(Time.unscaledTime);
            snapshot.PortalsVersion = Portals.Version;
            snapshot.BuildingsVersion = Buildings.Version;
            snapshot.Track = LiveSeedMap.Track.Current;

            var others = new List<ZNet.PlayerInfo>();
            ZNet.instance.GetOtherPublicPlayers(others);
            snapshot.Players = others.Select(info => new PlayerSpot {
                Name = CensorShittyWords.FilterUGC(info.m_name, UGCType.CharacterName, info.m_characterID.UserID),
                X = info.m_position.x, Z = info.m_position.z }).ToArray();

            snapshot.Creatures = Character.GetAllCharacters()
                .Where(character => character && !character.IsPlayer() && !character.IsDead())
                .Select(character => {
                    var position = character.transform.position;
                    return new Creature { Prefab = global::Utils.GetPrefabName(character.gameObject), Name = character.m_name,
                        Level = character.GetLevel(), Health = character.GetHealth(), MaxHealth = character.GetMaxHealth(),
                        X = position.x, Z = position.z, Tamed = character.IsTamed(), Boss = character.IsBoss(),
                        Hostile = player && BaseAI.IsEnemy(character, player) && character.GetBaseAI() is MonsterAI };
                }).ToArray();

            if (Minimap.instance)
                snapshot.Pins = ((List<Minimap.PinData>)PinsField.GetValue(Minimap.instance))
                    .Where(pin => pin.m_save)
                    .Select(pin => new Pin { Type = (int)pin.m_type, Name = pin.m_name, X = pin.m_pos.x, Z = pin.m_pos.z,
                        Checked = pin.m_checked, Shared = pin.m_ownerID != 0L }).ToArray();

            var icons = new Dictionary<Vector3, string>();
            ZoneSystem.instance.GetLocationIcons(icons);
            snapshot.Icons = icons.Select(icon => new Icon { Prefab = icon.Value, X = icon.Key.x, Z = icon.Key.z }).ToArray();
            return snapshot;
        }

        /// <summary>
        /// The ship the player steers (Player.GetControlledShip, the one Minimap draws its ship marker for), else the
        /// one whose volume the player stands in (Ship.GetLocalShip).
        /// </summary>
        static ShipInfo ShipOf(Player player)
        {
            var ship = player.GetControlledShip();
            if (!ship) ship = global::Ship.GetLocalShip();
            if (!ship) return null;
            var transform = ship.transform;
            var piece = ship.GetComponent<Piece>();
            return new ShipInfo { Prefab = global::Utils.GetPrefabName(ship.gameObject), Name = piece ? piece.m_name : "", X = transform.position.x, Z = transform.position.z,
                Heading = transform.rotation.eulerAngles.y, Speed = ship.GetSpeed(), Sail = ship.GetSpeedSetting().ToString().ToLowerInvariant() };
        }

        /// <summary>EnvMan.GetWindDir as a yaw, as Minimap.UpdateWindMarker turns its marker (Quaternion.LookRotation(dir).eulerAngles.y).</summary>
        static WindInfo Wind()
        {
            var env = EnvMan.instance;
            if (!env) return null;
            var dir = env.GetWindDir();
            var angle = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            return new WindInfo { Angle = angle < 0f ? angle + 360f : angle, Intensity = Mathf.Clamp01(env.GetWindIntensity()) };
        }

        /// <summary>
        /// Distance from anywhere in the player's zone that is always inside the zones ZDOMan.FindSectorObjects
        /// instantiates: the zone square of the near distance in classic mode, else zone centres within
        /// ZoneSystem.ZonesWithinRadius, less the half diagonal of the player's zone and of the far zone.
        /// </summary>
        public static float Reach(SimulationDistance distance, float zone) =>
            distance.IsClassic
                ? distance.NearSimulationDistance * zone
                : Mathf.Max(0f, (distance.NearSimulationDistance + 0.5f) * zone - zone * Mathf.Sqrt(2f));
    }
}
