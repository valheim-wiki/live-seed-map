using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace LiveSeedMap
{
    /// <summary>
    /// Remembers every player-built piece the game has loaded (mod 1.5), per world, across sessions, for GET /v1/buildings.
    /// Main thread.
    ///
    /// The game keeps no list of pieces, so every ScanEvery seconds the mod walks the ZDOs it holds (ZDOMan.m_objectsByID,
    /// private) and keeps those whose prefab carries a Piece and whose ZDO names a creator (ZDOVars.s_creator, set by
    /// Piece.SetCreator when a player places it): generated ruins and camps have none. A client of a dedicated server
    /// holds only the zones around the player, so as with the portals (Portals.cs) the book remembers what it saw and
    /// drops a piece when the game reports it destroyed or when its ground is loaded and the game still does not hold it.
    ///
    /// Each piece type's top-down shape comes from its prefab's solid colliders: every collider's local box, as eight
    /// corners in the prefab's own space, which the page turns by the piece's rotation and projects onto the ground. The
    /// type also carries its localized name, a map group read from its components and its WearNTear material.
    /// </summary>
    public static class Buildings
    {
        const float ScanEvery = 3f;
        const float SaveEvery = 30f;

        static readonly FieldInfo ObjectsField = AccessTools.Field(typeof(ZDOMan), "m_objectsByID");

        static BuildingBook book;
        static long worldUid;
        static ZDOMan hooked;
        static ZNetScene piecesOf;
        static readonly Dictionary<int, string> pieceNames = new Dictionary<int, string>();
        /* The crops a planted sapling grows into (Plant.m_grownPrefabs that are Pickables): no Piece and no creator, but
         * only a player plants them. Their wild kin are other prefabs (Pickable_Barley_Wild). */
        static readonly HashSet<int> crops = new HashSet<int>();
        static readonly Dictionary<string, string> types = new Dictionary<string, string>(StringComparer.Ordinal);
        static readonly HashSet<PieceKey> present = new HashSet<PieceKey>();
        static float nextScan, nextSave;
        static bool unsaved;
        static volatile FrozenBuildings frozen;
        static volatile string[] frozenTypes;
        static volatile string self;
        static string cachedJson;
        static int cachedVersion;
        static readonly object cacheGate = new object();
        static ManualLogSource log;

        /// <summary>The book's version for the snapshot, or null without a world.</summary>
        public static int? Version => book?.Version;

        public static void Tick(float now, ManualLogSource logger)
        {
            log = logger;
            var world = ZNet.instance ? (World)Capture.WorldField.GetValue(null) : null;
            var zdos = ZDOMan.instance;
            if (world == null || zdos == null || !ZNetScene.instance || !ZoneSystem.instance) { Leave(); return; }
            if (book == null || world.m_uid != worldUid) Join(world.m_uid);
            if (hooked != zdos)
            {
                if (hooked != null) hooked.m_onZDODestroyed -= Destroyed;
                zdos.m_onZDODestroyed += Destroyed;
                hooked = zdos;
            }
            if (now >= nextScan)
            {
                nextScan = now + ScanEvery;
                Scan(zdos);
            }
            if (book.TakeChanged()) { Publish(); unsaved = true; }
            if (unsaved && now >= nextSave) { Save(); nextSave = now + SaveEvery; }
        }

        /// <summary>Saves the book now (the plugin unloads).</summary>
        public static void Flush() => Save(wait: true);

        static void Join(long uid)
        {
            Leave();
            worldUid = uid;
            var world = uid.ToString(CultureInfo.InvariantCulture);
            string text = null;
            try { if (File.Exists(FileOf(world))) text = File.ReadAllText(FileOf(world)); }
            catch (Exception error) { log?.LogWarning($"Could not read the saved buildings: {error.Message}"); }
            book = BuildingBook.Load(text, world);
            Publish();
            nextScan = 0f;
        }

        static void Leave()
        {
            if (book == null) return;
            Save();
            if (hooked != null) hooked.m_onZDODestroyed -= Destroyed;
            hooked = null;
            book = null;
            frozen = null;
            frozenTypes = null;
        }

        static string FileOf(string world) => Path.Combine(Path.Combine(Paths.ConfigPath, "LiveSeedMap"), $"buildings-{world}.json");

        /// <summary>Hands the current book to other threads: the rows and each prefab's shape, as they stand now.</summary>
        static void Publish()
        {
            var copy = book.Freeze();
            var shapes = new string[copy.Prefabs.Length];
            for (var i = 0; i < shapes.Length; i++) shapes[i] = TypeOf(copy.Prefabs[i]);
            var player = Player.m_localPlayer;
            self = player ? player.GetPlayerID().ToString(CultureInfo.InvariantCulture) : null;
            frozenTypes = shapes;
            frozen = copy;
        }

        /// <summary>Writes the file off the main thread: a large base is megabytes of JSON.</summary>
        static void Save(bool wait = false)
        {
            if (book == null || !unsaved) return;
            unsaved = false;
            var copy = book.Freeze();
            void Write()
            {
                try
                {
                    var file = FileOf(copy.World);
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    var temp = file + ".tmp";
                    File.WriteAllText(temp, BuildingBook.ToFile(copy));
                    if (File.Exists(file)) File.Delete(file);
                    File.Move(temp, file);
                }
                catch (Exception error) { log?.LogWarning($"Could not save the buildings: {error.Message}"); }
            }
            if (wait) Write(); else ThreadPool.QueueUserWorkItem(_ => { lock (cacheGate) Write(); });
        }

        /// <summary>ZNetScene.m_prefabs that carry a Piece, by prefab hash.</summary>
        static Dictionary<int, string> PieceNames()
        {
            if (piecesOf == ZNetScene.instance) return pieceNames;
            pieceNames.Clear();
            crops.Clear();
            foreach (var prefab in ZNetScene.instance.m_prefabs)
            {
                if (!prefab || !prefab.GetComponent<Piece>()) continue;
                pieceNames[prefab.name.GetStableHashCode()] = prefab.name;
                var plant = prefab.GetComponent<Plant>();
                if (plant == null) continue;
                foreach (var grown in plant.m_grownPrefabs)
                {
                    if (!grown || !grown.GetComponent<Pickable>()) continue;
                    var hash = grown.name.GetStableHashCode();
                    pieceNames[hash] = grown.name;
                    crops.Add(hash);
                }
            }
            piecesOf = ZNetScene.instance;
            return pieceNames;
        }

        static void Scan(ZDOMan zdos)
        {
            if (!(ObjectsField?.GetValue(zdos) is Dictionary<ZDOID, ZDO> objects)) return;
            var names = PieceNames();
            present.Clear();
            foreach (var zdo in objects.Values)
            {
                if (zdo == null || !names.TryGetValue(zdo.GetPrefab(), out var prefab)) continue;
                var creator = zdo.GetLong(ZDOVars.s_creator, 0L);
                if (creator == 0L && !crops.Contains(zdo.GetPrefab())) continue;
                var at = zdo.GetPosition();
                var turn = zdo.GetRotation();
                present.Add(book.Observe(prefab, at.x, at.y, at.z, turn.x, turn.y, turn.z, turn.w, creator));
            }
            var player = Player.m_localPlayer;
            var reach = Capture.Reach(ZNet.instance.GetSyncedSimulationDistance(), ZoneSystem.instance.m_zoneSize) * 0.9f;
            var here = player ? player.transform.position : Vector3.zero;
            book.Sweep(present, (x, z) => player && (x - here.x) * (x - here.x) + (z - here.z) * (z - here.z) <= reach * reach, ScanEvery, ZNet.instance.IsServer());
        }

        static void Destroyed(ZDO zdo)
        {
            try
            {
                if (book == null || zdo == null || !PieceNames().TryGetValue(zdo.GetPrefab(), out var prefab)) return;
                var at = zdo.GetPosition();
                book.Remove(prefab, at.x, at.y, at.z);
            }
            catch (Exception error) { log?.LogError(error); }
        }

        /// <summary>
        /// GET /v1/buildings: <c>{ v, world, version, self, creators: ["id"], prefabs: [{ id, name, group, material, boxes }],
        /// pieces: [[prefab, creator, x, y, z, qx, qy, qz, qw]] }</c>; null without a world. Read from any thread; the
        /// answer is kept until the book changes.
        /// </summary>
        public static string Json()
        {
            var copy = frozen;
            var shapes = frozenTypes;
            if (copy == null || shapes == null) return null;
            lock (cacheGate)
            {
                if (cachedJson != null && cachedVersion == copy.Version) return cachedJson;
                var text = new StringWriter(CultureInfo.InvariantCulture);
                using (var w = new JsonTextWriter(text))
                {
                    w.WriteStartObject();
                    w.WritePropertyName("v"); w.WriteValue(LiveSeedMap.Json.Protocol);
                    w.WritePropertyName("world"); w.WriteValue(copy.World);
                    w.WritePropertyName("version"); w.WriteValue(copy.Version);
                    w.WritePropertyName("self"); if (self == null) w.WriteNull(); else w.WriteValue(self);
                    w.WritePropertyName("creators"); w.WriteStartArray();
                    foreach (var c in copy.Creators) w.WriteValue(c.ToString(CultureInfo.InvariantCulture));
                    w.WriteEndArray();
                    w.WritePropertyName("prefabs"); w.WriteStartArray();
                    for (var i = 0; i < copy.Prefabs.Length; i++)
                        if (i < shapes.Length && shapes[i] != null) w.WriteRawValue(shapes[i]);
                        else { w.WriteStartObject(); w.WritePropertyName("id"); w.WriteValue(copy.Prefabs[i]); w.WriteEndObject(); }
                    w.WriteEndArray();
                    w.WritePropertyName("pieces"); BuildingBook.WritePieces(w, copy.Rows);
                    w.WriteEndObject();
                }
                cachedJson = text.ToString();
                cachedVersion = copy.Version;
                return cachedJson;
            }
        }

        /// <summary>One prefab's shape and labels as a JSON object, computed once per prefab name on the main thread.</summary>
        static string TypeOf(string name)
        {
            if (types.TryGetValue(name, out var known)) return known;
            var prefab = ZNetScene.instance ? ZNetScene.instance.GetPrefab(name) : null;
            string json = null;
            if (prefab)
            {
                try { json = Describe(name, prefab); }
                catch (Exception error) { log?.LogWarning($"Could not read the shape of {name}: {error.Message}"); }
            }
            if (json != null || !prefab) types[name] = json;
            return json;
        }

        static string Describe(string name, GameObject prefab)
        {
            var piece = prefab.GetComponent<Piece>();
            var wear = prefab.GetComponent<WearNTear>();
            var text = new StringWriter(CultureInfo.InvariantCulture);
            using (var w = new JsonTextWriter(text))
            {
                w.WriteStartObject();
                w.WritePropertyName("id"); w.WriteValue(name);
                w.WritePropertyName("name"); w.WriteValue(piece && Localization.instance != null ? Localization.instance.Localize(piece.m_name) : name);
                var group = GroupOf(name, prefab, piece);
                w.WritePropertyName("group"); w.WriteValue(group);
                w.WritePropertyName("material"); if (wear) w.WriteValue(wear.m_materialType.ToString()); else w.WriteNull();
                /* Mod 1.5: the circle in which the piece stops creatures spawning, [x, z, radius] in prefab space: an
                 * EffectArea of type PlayerBase (SpawnSystem and CreatureSpawner skip a point inside one), its sphere. */
                if (BaseArea(prefab, out var area))
                {
                    w.WritePropertyName("base"); w.WriteStartArray();
                    w.WriteValue(Math.Round(area.x, 2)); w.WriteValue(Math.Round(area.y, 2)); w.WriteValue(Math.Round(area.z, 2));
                    w.WriteEndArray();
                }
                w.WritePropertyName("boxes"); w.WriteStartArray();
                var toRoot = prefab.transform.worldToLocalMatrix;
                var boxes = 0;
                var seen = SeenExtent(prefab, toRoot);
                foreach (var collider in prefab.GetComponentsInChildren<Collider>(true))
                {
                    /* A sapling or a crop has only a trigger to pick it by; its footprint is what the player sees. */
                    if (!collider || collider.isTrigger && group != "plant" || !BoundsOf(collider, out var bounds)) continue;
                    var m = toRoot * collider.transform.localToWorldMatrix;
                    /* A helper collider far past the piece's own model (the artisan table carries a 40 m one) is not its shape. */
                    if (seen > 0f && GroundExtent(bounds, m) > seen + 2f) continue;
                    boxes++;
                    w.WriteStartArray();
                    for (var i = 0; i < 8; i++)
                    {
                        var corner = bounds.center + Vector3.Scale(bounds.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                        var p = m.MultiplyPoint3x4(corner);
                        w.WriteValue(Math.Round(p.x, 3)); w.WriteValue(Math.Round(p.y, 3)); w.WriteValue(Math.Round(p.z, 3));
                    }
                    /* A stair or ramp: the rise of its walking surface, per metre along the prefab's x and z. */
                    if (collider is MeshCollider mesh && SlopeOf(mesh.sharedMesh, m, out var rise))
                    {
                        w.WriteValue(Math.Round(rise.x, 3)); w.WriteValue(Math.Round(rise.y, 3));
                    }
                    w.WriteEndArray();
                }
                /* Nothing solid or pickable at all: a plant still takes half a metre of its row. */
                if (boxes == 0 && group == "plant")
                {
                    w.WriteStartArray();
                    for (var i = 0; i < 8; i++) { w.WriteValue((i & 1) == 0 ? -0.25 : 0.25); w.WriteValue((i & 2) == 0 ? 0.0 : 0.5); w.WriteValue((i & 4) == 0 ? -0.25 : 0.25); }
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return text.ToString();
        }

        /// <summary>
        /// The slope of a mesh collider's top surface, in the prefab's space: the mesh's vertices (a collider mesh must
        /// be readable for PhysX) over an 8×8 grid of its ground extent, the highest per cell, and the least-squares
        /// plane y = a·x + b·z + c through those tops. (a, b) is the rise per metre. False for a flat or unreadable mesh.
        /// </summary>
        static bool SlopeOf(Mesh mesh, Matrix4x4 m, out Vector2 rise)
        {
            rise = default;
            if (!mesh || !mesh.isReadable) return false;
            var vertices = mesh.vertices;
            if (vertices.Length < 4) return false;
            var points = new Vector3[vertices.Length];
            float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
            for (var i = 0; i < vertices.Length; i++)
            {
                var p = points[i] = m.MultiplyPoint3x4(vertices[i]);
                x0 = Mathf.Min(x0, p.x); x1 = Mathf.Max(x1, p.x); z0 = Mathf.Min(z0, p.z); z1 = Mathf.Max(z1, p.z);
            }
            const int N = 8;
            var top = new float[N * N];
            for (var i = 0; i < top.Length; i++) top[i] = float.NaN;
            float sx = Mathf.Max(1e-3f, x1 - x0) / N, sz = Mathf.Max(1e-3f, z1 - z0) / N;
            foreach (var p in points)
            {
                var c = Mathf.Min(N - 1, (int)((p.x - x0) / sx)) + N * Mathf.Min(N - 1, (int)((p.z - z0) / sz));
                if (float.IsNaN(top[c]) || p.y > top[c]) top[c] = p.y;
            }
            /* Normal equations of the plane fit over the cells that hold a vertex. */
            double n = 0, X = 0, Z = 0, Y = 0, XX = 0, ZZ = 0, XZ = 0, XY = 0, ZY = 0;
            for (var c = 0; c < top.Length; c++)
            {
                if (float.IsNaN(top[c])) continue;
                double x = x0 + (c % N + 0.5) * sx, z = z0 + (c / N + 0.5) * sz, y = top[c];
                n++; X += x; Z += z; Y += y; XX += x * x; ZZ += z * z; XZ += x * z; XY += x * y; ZY += z * y;
            }
            if (n < 4) return false;
            double cxx = XX - X * X / n, czz = ZZ - Z * Z / n, cxz = XZ - X * Z / n, cxy = XY - X * Y / n, czy = ZY - Z * Y / n;
            var det = cxx * czz - cxz * cxz;
            if (Math.Abs(det) < 1e-9) return false;
            var a = (cxy * czz - czy * cxz) / det;
            var b = (czy * cxx - cxy * cxz) / det;
            if (a * a + b * b < 0.04) return false;
            rise = new Vector2((float)a, (float)b);
            return true;
        }

        /// <summary>The largest PlayerBase effect area's ground circle: (x, z) of its centre and its radius, prefab space.</summary>
        static bool BaseArea(GameObject prefab, out Vector3 area)
        {
            area = default;
            var toRoot = prefab.transform.worldToLocalMatrix;
            foreach (var effect in prefab.GetComponentsInChildren<EffectArea>(true))
            {
                if (!effect || (effect.m_type & EffectArea.Type.PlayerBase) == 0) continue;
                /* Most are a sphere trigger; any other collider counts by its ground extent. */
                foreach (var collider in effect.GetComponents<Collider>())
                {
                    if (!collider || !BoundsOf(collider, out var bounds)) continue;
                    var scale = effect.transform.lossyScale;
                    var radius = collider is SphereCollider sphere
                        ? sphere.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z))
                        : Mathf.Max(bounds.extents.x * Mathf.Abs(scale.x), bounds.extents.z * Mathf.Abs(scale.z));
                    if (radius <= area.z) continue;
                    var centre = toRoot.MultiplyPoint3x4(effect.transform.TransformPoint(bounds.center));
                    area = new Vector3(centre.x, centre.z, radius);
                }
                if (area.z <= 0f) log?.LogWarning($"{prefab.name}: its PlayerBase area has no collider the map can read ({string.Join(", ", effect.GetComponents<Component>().Select(c => c ? c.GetType().Name : "null"))})");
            }
            return area.z > 0f;
        }

        /// <summary>The widest ground extent (x or z, prefab space) of the prefab's visible models; 0 without any.</summary>
        static float SeenExtent(GameObject prefab, Matrix4x4 toRoot)
        {
            var extent = 0f;
            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) continue;
                var b = renderer.bounds;
                extent = Mathf.Max(extent, GroundExtent(new Bounds(b.center, b.size), toRoot));
            }
            return extent;
        }

        /// <summary>The wider of a box's x and z spans once <paramref name="m"/> places it.</summary>
        static float GroundExtent(Bounds bounds, Matrix4x4 m)
        {
            float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            for (var i = 0; i < 8; i++)
            {
                var p = m.MultiplyPoint3x4(bounds.center + Vector3.Scale(bounds.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                x0 = Mathf.Min(x0, p.x); x1 = Mathf.Max(x1, p.x); z0 = Mathf.Min(z0, p.z); z1 = Mathf.Max(z1, p.z);
            }
            return Mathf.Max(x1 - x0, z1 - z0);
        }

        /// <summary>A collider's box in its own transform's space.</summary>
        static bool BoundsOf(Collider collider, out Bounds bounds)
        {
            switch (collider)
            {
                case BoxCollider box: bounds = new Bounds(box.center, box.size); return true;
                case MeshCollider mesh when mesh.sharedMesh: bounds = mesh.sharedMesh.bounds; return true;
                case SphereCollider sphere: bounds = new Bounds(sphere.center, Vector3.one * sphere.radius * 2f); return true;
                case CapsuleCollider capsule:
                    var size = Vector3.one * capsule.radius * 2f;
                    size[Mathf.Clamp(capsule.direction, 0, 2)] = Mathf.Max(capsule.height, capsule.radius * 2f);
                    bounds = new Bounds(capsule.center, size);
                    return true;
                default: bounds = default; return false;
            }
        }

        /// <summary>The map group a piece belongs to, from what it does in the game, then from its name.</summary>
        static string GroupOf(string name, GameObject prefab, Piece piece)
        {
            var lower = name.ToLowerInvariant();
            if (prefab.GetComponent<TeleportWorld>()) return "portal";
            if (prefab.GetComponent<Ship>()) return "ship";
            if (prefab.GetComponent<PrivateArea>()) return "ward";
            if (prefab.GetComponent<CraftingStation>() || prefab.GetComponent<StationExtension>()) return "station";
            if (prefab.GetComponent<Smelter>() || prefab.GetComponent<Fermenter>() || prefab.GetComponent<CookingStation>() || prefab.GetComponent<Beehive>()) return "production";
            if (prefab.GetComponent<Bed>()) return "bed";
            if (prefab.GetComponent<Container>()) return "chest";
            if (prefab.GetComponent<Plant>() || !piece && prefab.GetComponent<Pickable>()) return "plant";
            if (prefab.GetComponent<Door>()) return "door";
            if (prefab.GetComponent<Fireplace>())
                return lower.Contains("torch") || lower.Contains("sconce") || lower.Contains("brazier") || lower.Contains("lantern") || lower.Contains("candle") ? "light" : "fire";
            if (lower.Contains("roof")) return "roof";
            if (lower.Contains("stake") || lower.Contains("sharp") || lower.Contains("palisade") || lower.Contains("spike")) return "defense";
            if (piece)
            {
                var category = piece.m_category;
                if (piece.m_comfort > 0 || category == Piece.PieceCategory.Furniture || category == Piece.PieceCategory.Misc && !prefab.GetComponent<WearNTear>()) return "furniture";
                if (category == Piece.PieceCategory.Crafting) return "production";
            }
            return prefab.GetComponent<WearNTear>() ? "structure" : "furniture";
        }
    }
}
