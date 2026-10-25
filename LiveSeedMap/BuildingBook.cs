using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiveSeedMap
{
    /// <summary>One building piece the mod has seen (mod 1.5): prefab and creator as indexes into the book's tables, position and rotation.</summary>
    public struct PieceRow
    {
        public int Prefab, Creator;
        public float X, Y, Z, QX, QY, QZ, QW;

        public bool Same(PieceRow other) =>
            other.Prefab == Prefab && other.Creator == Creator && other.X == X && other.Y == Y && other.Z == Z &&
            other.QX == QX && other.QY == QY && other.QZ == QZ && other.QW == QW;
    }

    /// <summary>
    /// Names a piece across sessions: its prefab and its position to the centimetre. As with portals (PortalBook), a ZDOID
    /// names an object for one server session only, and a placed piece does not move.
    /// </summary>
    public readonly struct PieceKey : IEquatable<PieceKey>
    {
        public readonly int Prefab, X, Y, Z;

        public PieceKey(int prefab, float x, float y, float z)
        {
            Prefab = prefab;
            X = (int)Math.Round(x * 100f); Y = (int)Math.Round(y * 100f); Z = (int)Math.Round(z * 100f);
        }

        public bool Equals(PieceKey other) => Prefab == other.Prefab && X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object obj) => obj is PieceKey other && Equals(other);
        public override int GetHashCode() { unchecked { return ((Prefab * 397 ^ X) * 397 ^ Y) * 397 ^ Z; } }
    }

    /// <summary>The book as it stood at one version, handed to other threads (HTTP answers, the saved file).</summary>
    public sealed class FrozenBuildings
    {
        public string World;
        public int Version;
        public string[] Prefabs;
        public long[] Creators;
        public PieceRow[] Rows;
    }

    /// <summary>
    /// Every player-built piece of one world the mod has seen (mod 1.5), kept like the portals: a client of a dedicated
    /// server only holds the pieces around the player, so the book remembers what it saw and drops a piece only when the
    /// game destroys it, or when its ground is loaded and the game still does not hold it. Free of Unity types, so
    /// mod/check runs it. Not thread-safe: the main thread owns it and hands <see cref="Freeze"/> copies out.
    /// </summary>
    public sealed class BuildingBook
    {
        public const float MissingSeconds = 10f;

        readonly List<string> prefabs = new List<string>();
        readonly Dictionary<string, int> prefabIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly List<long> creators = new List<long>();
        readonly Dictionary<long, int> creatorIndex = new Dictionary<long, int>();
        readonly Dictionary<PieceKey, PieceRow> pieces = new Dictionary<PieceKey, PieceRow>();
        readonly Dictionary<PieceKey, float> missing = new Dictionary<PieceKey, float>();
        bool changed;

        public BuildingBook(string world) { World = world; Version = Environment.TickCount; }

        public string World { get; }
        public int Count => pieces.Count;
        /// <summary>Changes whenever a piece is added, removed or turned.</summary>
        public int Version { get; private set; }

        public int PrefabIndex(string prefab)
        {
            if (prefabIndex.TryGetValue(prefab, out var index)) return index;
            prefabs.Add(prefab);
            return prefabIndex[prefab] = prefabs.Count - 1;
        }

        int CreatorIndex(long creator)
        {
            if (creatorIndex.TryGetValue(creator, out var index)) return index;
            creators.Add(creator);
            return creatorIndex[creator] = creators.Count - 1;
        }

        /// <summary>Adds or updates a piece the game holds now and returns its key.</summary>
        public PieceKey Observe(string prefab, float x, float y, float z, float qx, float qy, float qz, float qw, long creator)
        {
            var row = new PieceRow { Prefab = PrefabIndex(prefab), Creator = CreatorIndex(creator), X = x, Y = y, Z = z, QX = qx, QY = qy, QZ = qz, QW = qw };
            var key = new PieceKey(row.Prefab, x, y, z);
            missing.Remove(key);
            if (pieces.TryGetValue(key, out var known) && known.Same(row)) return key;
            pieces[key] = row;
            Changed();
            return key;
        }

        /// <summary>Forgets a piece (the game destroyed it). True when it was known.</summary>
        public bool Remove(string prefab, float x, float y, float z)
        {
            if (!prefabIndex.TryGetValue(prefab, out var index)) return false;
            var key = new PieceKey(index, x, y, z);
            missing.Remove(key);
            if (!pieces.Remove(key)) return false;
            Changed();
            return true;
        }

        /// <summary>
        /// Drops remembered pieces the game no longer has, as PortalBook.Sweep: with <paramref name="complete"/> any piece
        /// not in <paramref name="present"/> is gone; otherwise only one missing for <see cref="MissingSeconds"/> while
        /// <paramref name="loaded"/> says its ground (x, z) is loaded around the player.
        /// </summary>
        public bool Sweep(HashSet<PieceKey> present, Func<float, float, bool> loaded, float elapsed, bool complete)
        {
            var gone = new List<PieceKey>();
            foreach (var pair in pieces)
            {
                if (present.Contains(pair.Key)) continue;
                if (complete) { gone.Add(pair.Key); continue; }
                if (!loaded(pair.Value.X, pair.Value.Z)) { missing.Remove(pair.Key); continue; }
                missing.TryGetValue(pair.Key, out var seconds);
                seconds += elapsed;
                if (seconds >= MissingSeconds) gone.Add(pair.Key); else missing[pair.Key] = seconds;
            }
            foreach (var key in gone) { pieces.Remove(key); missing.Remove(key); }
            if (gone.Count > 0) Changed();
            return gone.Count > 0;
        }

        /// <summary>True once after the book changed.</summary>
        public bool TakeChanged()
        {
            var was = changed;
            changed = false;
            return was;
        }

        void Changed() { changed = true; unchecked { Version++; } }

        public FrozenBuildings Freeze() => new FrozenBuildings
        {
            World = World, Version = Version, Prefabs = prefabs.ToArray(), Creators = creators.ToArray(), Rows = pieces.Values.ToArray(),
        };

        /// <summary>
        /// The pieces array both the saved file and GET /v1/buildings carry: <c>[prefab, creator, x, y, z, qx, qy, qz, qw]</c>
        /// per piece, indexes into the prefab and creator tables. Positions to the centimetre, rotations to 1e-4.
        /// </summary>
        public static void WritePieces(JsonWriter w, PieceRow[] rows)
        {
            w.WriteStartArray();
            foreach (var r in rows)
            {
                w.WriteStartArray();
                w.WriteValue(r.Prefab); w.WriteValue(r.Creator);
                w.WriteValue(Math.Round(r.X, 2)); w.WriteValue(Math.Round(r.Y, 2)); w.WriteValue(Math.Round(r.Z, 2));
                w.WriteValue(Math.Round(r.QX, 4)); w.WriteValue(Math.Round(r.QY, 4)); w.WriteValue(Math.Round(r.QZ, 4)); w.WriteValue(Math.Round(r.QW, 4));
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }

        /// <summary>The saved file: <c>{ world, prefabs: [name], creators: ["id"], pieces: [...] }</c>.</summary>
        public static string ToFile(FrozenBuildings frozen)
        {
            var text = new StringWriter(CultureInfo.InvariantCulture);
            using (var w = new JsonTextWriter(text))
            {
                w.WriteStartObject();
                w.WritePropertyName("world"); w.WriteValue(frozen.World);
                w.WritePropertyName("prefabs"); w.WriteStartArray(); foreach (var p in frozen.Prefabs) w.WriteValue(p); w.WriteEndArray();
                w.WritePropertyName("creators"); w.WriteStartArray(); foreach (var c in frozen.Creators) w.WriteValue(c.ToString(CultureInfo.InvariantCulture)); w.WriteEndArray();
                w.WritePropertyName("pieces"); WritePieces(w, frozen.Rows);
                w.WriteEndObject();
            }
            return text.ToString();
        }

        /// <summary>A saved book; an unreadable file or another world's gives an empty book.</summary>
        public static BuildingBook Load(string text, string world)
        {
            var book = new BuildingBook(world);
            if (string.IsNullOrEmpty(text)) return book;
            try
            {
                var root = JObject.Parse(text);
                if ((string)root["world"] != world || !(root["prefabs"] is JArray names) || !(root["creators"] is JArray ids) || !(root["pieces"] is JArray rows)) return book;
                var prefabNames = names.Select(n => (string)n).ToArray();
                var creatorIds = ids.Select(i => long.TryParse((string)i, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0L).ToArray();
                foreach (var row in rows.OfType<JArray>())
                {
                    if (row.Count < 9) continue;
                    var p = (int)row[0]; var c = (int)row[1];
                    if (p < 0 || p >= prefabNames.Length || string.IsNullOrEmpty(prefabNames[p]) || c < 0 || c >= creatorIds.Length) continue;
                    book.Observe(prefabNames[p], (float)row[2], (float)row[3], (float)row[4], (float)row[5], (float)row[6], (float)row[7], (float)row[8], creatorIds[c]);
                }
            }
            catch (Exception error) when (error is JsonException || error is FormatException || error is InvalidCastException || error is OverflowException) { return new BuildingBook(world); }
            book.changed = false;
            return book;
        }
    }
}
