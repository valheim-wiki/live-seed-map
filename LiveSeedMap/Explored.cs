using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace LiveSeedMap
{
    /// <summary>
    /// The ground the player's map shows explored: Minimap's private BitArrays m_explored (the player's own) and
    /// m_exploredOthers (from shared cartography tables), index y * m_textureSize + x as Minimap.Explore(x, y) writes them.
    /// Read on the main thread only. The version is a hash of both planes, recomputed at most every HashEvery seconds, so
    /// a page refetches the planes only after they changed.
    /// </summary>
    public static class Explored
    {
        static readonly FieldInfo ExploredField = AccessTools.Field(typeof(Minimap), "m_explored");
        static readonly FieldInfo OthersField = AccessTools.Field(typeof(Minimap), "m_exploredOthers");
        const float HashEvery = 2f;

        static int[] words;
        static Minimap owner;
        static float next;
        static int? version;

        /// <summary>The planes' version, or null without a map (main menu). Main thread.</summary>
        public static int? Version(float now, bool force = false)
        {
            var map = Minimap.instance;
            if (!map) { owner = null; version = null; return null; }
            if (!force && owner == map && version.HasValue && now < next) return version;
            owner = map;
            next = now + HashEvery;
            var explored = ExploredField?.GetValue(map) as BitArray;
            var others = OthersField?.GetValue(map) as BitArray;
            version = explored == null || others == null ? (int?)null : unchecked(Hash(explored) * 31 + Hash(others));
            return version;
        }

        /// <summary>Both planes packed eight pixels a byte, least significant bit first (BitArray.CopyTo). Main thread.</summary>
        public static ExploredPlanes Read(float now)
        {
            var map = Minimap.instance;
            if (!map) return null;
            var explored = ExploredField?.GetValue(map) as BitArray;
            var others = OthersField?.GetValue(map) as BitArray;
            if (explored == null || others == null) return null;
            var planes = new ExploredPlanes { Size = map.m_textureSize, Explored = Pack(explored), Others = Pack(others) };
            planes.Version = Version(now, force: true) ?? 0;
            return planes;
        }

        static byte[] Pack(BitArray bits)
        {
            var bytes = new byte[(bits.Length + 7) / 8];
            bits.CopyTo(bytes, 0);
            return bytes;
        }

        /// <summary>FNV-1a over the plane's 32-bit words.</summary>
        static int Hash(BitArray bits)
        {
            var count = (bits.Length + 31) / 32;
            if (words == null || words.Length != count) words = new int[count];
            bits.CopyTo(words, 0);
            unchecked
            {
                var hash = (int)2166136261;
                foreach (var word in words) hash = (hash ^ word) * 16777619;
                return hash;
            }
        }
    }
}
