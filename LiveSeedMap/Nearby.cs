using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace LiveSeedMap
{
    /// <summary>
    /// GET /v1/nearby (debug, mod 1.2): the pickables, trees, rocks and destructibles the game has instantiated around the
    /// player (ZNetScene.m_instances, private), by prefab name and ZDO position, as ground truth for the site's
    /// vegetation port. Main thread only; never part of the snapshot.
    /// </summary>
    public static class Nearby
    {
        static readonly FieldInfo InstancesField = AccessTools.Field(typeof(ZNetScene), "m_instances");
        /// <summary>The farthest a request may ask for: beyond the loaded zones nothing is instantiated anyway.</summary>
        public const float MaxRange = 400f;

        /// <summary>The answer's JSON, or null without a player (main menu).</summary>
        public static string Read(float range)
        {
            var player = Player.m_localPlayer;
            var scene = ZNetScene.instance;
            if (!player || !scene || !(InstancesField?.GetValue(scene) is Dictionary<ZDO, ZNetView> instances)) return null;
            /* Without ?range= the reach the snapshot reports: the zones the game keeps loaded around the player. */
            var reach = ZNet.instance && ZoneSystem.instance ? Capture.Reach(ZNet.instance.GetSyncedSimulationDistance(), ZoneSystem.instance.m_zoneSize) : MaxRange;
            var radius = Mathf.Min(float.IsInfinity(range) ? reach : range, MaxRange);
            var centre = player.transform.position;
            var rows = new List<(string Prefab, string Kind, Vector3 At)>();
            foreach (var pair in instances)
            {
                var view = pair.Value;
                if (!view || pair.Key == null) continue;
                var at = pair.Key.GetPosition();
                var dx = at.x - centre.x; var dz = at.z - centre.z;
                if (dx * dx + dz * dz > radius * radius) continue;
                var kind = KindOf(view.gameObject);
                /* The ZDO's prefab, not the object's name: MineRock5 renames a hit vein's object ("___MineRock5"). */
                var prefab = scene.GetPrefab(pair.Key.GetPrefab());
                if (kind != null) rows.Add((prefab ? prefab.name : global::Utils.GetPrefabName(view.gameObject), kind, at));
            }

            var text = new StringWriter(CultureInfo.InvariantCulture);
            using (var w = new JsonTextWriter(text))
            {
                w.WriteStartObject();
                w.WritePropertyName("v"); w.WriteValue(Json.Protocol);
                w.WritePropertyName("x"); w.WriteValue(System.Math.Round(centre.x, 1));
                w.WritePropertyName("z"); w.WriteValue(System.Math.Round(centre.z, 1));
                w.WritePropertyName("radius"); w.WriteValue(System.Math.Round(radius, 1));
                w.WritePropertyName("objects"); w.WriteStartArray();
                foreach (var row in rows.OrderBy(r => r.Prefab, System.StringComparer.Ordinal))
                {
                    w.WriteStartObject();
                    w.WritePropertyName("prefab"); w.WriteValue(row.Prefab);
                    w.WritePropertyName("kind"); w.WriteValue(row.Kind);
                    w.WritePropertyName("x"); w.WriteValue(System.Math.Round(row.At.x, 2));
                    w.WritePropertyName("y"); w.WriteValue(System.Math.Round(row.At.y, 2));
                    w.WritePropertyName("z"); w.WriteValue(System.Math.Round(row.At.z, 2));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return text.ToString();
        }

        static string KindOf(GameObject go)
        {
            if (go.GetComponent<Pickable>() || go.GetComponent<PickableItem>()) return "pickable";
            if (go.GetComponent<TreeBase>()) return "tree";
            if (go.GetComponent<MineRock>() || go.GetComponent<MineRock5>()) return "rock";
            if (go.GetComponent<Destructible>()) return "destructible";
            return null;
        }
    }
}
