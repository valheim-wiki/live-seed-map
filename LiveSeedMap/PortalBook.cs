using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiveSeedMap
{
    /// <summary>
    /// One portal the mod has seen (mod 1.3): its prefab (a name in Game.m_portalPrefabs), position, tag (ZDOVars.s_tag,
    /// raw) and the portal it connects to. <see cref="To"/> is the other portal's key once the mod has seen it;
    /// <see cref="Linked"/> holds while the game reports a connection (ZDO connection of type Portal), even before the
    /// other end is known.
    /// </summary>
    public sealed class PortalRecord
    {
        public string Prefab;
        public float X, Y, Z;
        public string Tag = "";
        public string To;
        public bool Linked;

        public string Key => PortalBook.Key(Prefab, X, Y, Z);

        public bool Same(PortalRecord other) =>
            other != null && other.Prefab == Prefab && other.X == X && other.Y == Y && other.Z == Z && other.Tag == Tag && other.To == To && other.Linked == Linked;
    }

    /// <summary>
    /// Every portal of one world the mod has seen, kept by position. The game gives every ZDO a new ZDOID each time a
    /// world loads (ZDO.Load: m_uid.SetID(++ZDOID.m_loadID)), so a ZDOID names a portal for one server session only;
    /// portals never move, so prefab and position name it across sessions. Free of Unity types, so mod/check runs it.
    /// The same JSON is GET /v1/portals and the file kept per world.
    /// </summary>
    public sealed class PortalBook
    {
        /// <summary>How long a remembered portal must be missing from the game's loaded area before it is dropped.</summary>
        public const float MissingSeconds = 10f;

        readonly Dictionary<string, PortalRecord> portals = new Dictionary<string, PortalRecord>(StringComparer.Ordinal);
        readonly Dictionary<string, float> missing = new Dictionary<string, float>(StringComparer.Ordinal);

        public PortalBook(string world) { World = world; }

        /// <summary>World.m_uid as a string, as the snapshot's world.uid carries it.</summary>
        public string World { get; }
        public int Count => portals.Count;
        public IEnumerable<PortalRecord> All => portals.Values;
        /// <summary>A hash of the list; changes whenever a portal is added, removed or changes its tag or connection.</summary>
        public int Version { get; private set; }
        /// <summary>Changed since the last <see cref="TakeChanged"/>.</summary>
        bool changed;

        public static string Key(string prefab, float x, float y, float z) =>
            prefab + "@" + x.ToString("F1", CultureInfo.InvariantCulture) + "," + y.ToString("F1", CultureInfo.InvariantCulture) + "," + z.ToString("F1", CultureInfo.InvariantCulture);

        public PortalRecord Get(string key) => key != null && portals.TryGetValue(key, out var record) ? record : null;

        /// <summary>Adds or updates a portal the game has loaded now. True when the list changed.</summary>
        public bool Observe(PortalRecord record)
        {
            var key = record.Key;
            missing.Remove(key);
            if (portals.TryGetValue(key, out var known) && known.Same(record)) return false;
            portals[key] = record;
            return Changed();
        }

        /// <summary>Forgets a portal (the game destroyed it). True when it was known.</summary>
        public bool Remove(string key)
        {
            missing.Remove(key);
            return portals.Remove(key) && Changed();
        }

        /// <summary>
        /// Drops remembered portals the game no longer has. <paramref name="present"/> holds the keys the game has loaded;
        /// with <paramref name="complete"/> (the world runs in this game, which holds every portal) any other is gone.
        /// Otherwise a portal counts as gone only after it was missing for <see cref="MissingSeconds"/> while
        /// <paramref name="loaded"/> said its ground is loaded around the player, where the server would have sent it.
        /// </summary>
        public bool Sweep(ICollection<string> present, Func<PortalRecord, bool> loaded, float elapsed, bool complete)
        {
            var gone = new List<string>();
            foreach (var pair in portals)
            {
                if (present.Contains(pair.Key)) { missing.Remove(pair.Key); continue; }
                if (complete) { gone.Add(pair.Key); continue; }
                if (!loaded(pair.Value)) { missing.Remove(pair.Key); continue; }
                missing.TryGetValue(pair.Key, out var seconds);
                seconds += elapsed;
                if (seconds >= MissingSeconds) gone.Add(pair.Key); else missing[pair.Key] = seconds;
            }
            foreach (var key in gone) { portals.Remove(key); missing.Remove(key); }
            return gone.Count > 0 && Changed();
        }

        /// <summary>True once after the list changed, for saving.</summary>
        public bool TakeChanged()
        {
            var was = changed;
            changed = false;
            return was;
        }

        bool Changed()
        {
            changed = true;
            Version = Hash(PortalsArray());
            return true;
        }

        /// <summary>GET /v1/portals and the saved file: <c>{ v, world, version, portals: [{ id, prefab, x, y, z, tag, linked, to }] }</c>.</summary>
        public string ToJson()
        {
            var text = new StringWriter(CultureInfo.InvariantCulture);
            using (var w = new JsonTextWriter(text))
            {
                w.WriteStartObject();
                w.WritePropertyName("v"); w.WriteValue(Json.Protocol);
                w.WritePropertyName("world"); w.WriteValue(World);
                w.WritePropertyName("version"); w.WriteValue(Version);
                w.WritePropertyName("portals"); w.WriteRawValue(PortalsArray());
                w.WriteEndObject();
            }
            return text.ToString();
        }

        string PortalsArray()
        {
            var text = new StringWriter(CultureInfo.InvariantCulture);
            using (var w = new JsonTextWriter(text))
            {
                w.WriteStartArray();
                foreach (var pair in portals.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    var p = pair.Value;
                    w.WriteStartObject();
                    w.WritePropertyName("id"); w.WriteValue(pair.Key);
                    w.WritePropertyName("prefab"); w.WriteValue(p.Prefab);
                    /* Exact floats, so a saved portal gets the same key when the game loads it again. */
                    w.WritePropertyName("x"); w.WriteValue(p.X);
                    w.WritePropertyName("y"); w.WriteValue(p.Y);
                    w.WritePropertyName("z"); w.WriteValue(p.Z);
                    w.WritePropertyName("tag"); w.WriteValue(p.Tag ?? "");
                    w.WritePropertyName("linked"); w.WriteValue(p.Linked);
                    w.WritePropertyName("to");
                    if (p.To == null) w.WriteNull(); else w.WriteValue(p.To);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            return text.ToString();
        }

        /// <summary>FNV-1a over the UTF-8 of the list, so the same portals give the same version after a restart.</summary>
        static int Hash(string text)
        {
            unchecked
            {
                var hash = (int)2166136261;
                foreach (var b in Encoding.UTF8.GetBytes(text)) hash = (hash ^ b) * 16777619;
                return hash;
            }
        }

        /// <summary>A saved book; an unreadable file or another world's gives an empty book.</summary>
        public static PortalBook Load(string text, string world)
        {
            var book = new PortalBook(world);
            if (string.IsNullOrEmpty(text)) return book;
            try
            {
                var root = JObject.Parse(text);
                if ((string)root["world"] != world || !(root["portals"] is JArray rows)) return book;
                foreach (var row in rows.OfType<JObject>())
                {
                    var prefab = (string)row["prefab"];
                    var x = (float?)row["x"]; var y = (float?)row["y"]; var z = (float?)row["z"];
                    if (string.IsNullOrEmpty(prefab) || x == null || y == null || z == null) continue;
                    var record = new PortalRecord { Prefab = prefab, X = x.Value, Y = y.Value, Z = z.Value, Tag = (string)row["tag"] ?? "",
                        Linked = (bool?)row["linked"] ?? false, To = (string)row["to"] };
                    book.portals[record.Key] = record;
                }
            }
            catch (JsonException) { return new PortalBook(world); }
            if (book.portals.Count > 0) book.Version = Hash(book.PortalsArray());
            return book;
        }
    }
}
