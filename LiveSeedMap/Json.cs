using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;

namespace LiveSeedMap
{
    /// <summary>Protocol v1, written with the game's own Newtonsoft.Json. Creatures farther than the page's range from the player are left out.</summary>
    public static class Json
    {
        public const int Protocol = 1;

        public static string Write(Snapshot s, float range)
        {
            var text = new StringWriter(CultureInfo.InvariantCulture);
            using (var w = new JsonTextWriter(text))
            {
                w.WriteStartObject();
                w.WritePropertyName("v"); w.WriteValue(Protocol);
                /* Mod 1.5: the mod's own version, so the page can say a newer one is out. */
                Field(w, "mod", Plugin.Version);
                w.WritePropertyName("world");
                if (s.World == null) w.WriteNull();
                else
                {
                    w.WriteStartObject();
                    Field(w, "name", s.World.Name); Field(w, "seed", s.World.Seed);
                    w.WritePropertyName("hash"); w.WriteValue(s.World.Hash);
                    Field(w, "uid", s.World.Uid.ToString(CultureInfo.InvariantCulture));
                    w.WritePropertyName("gen"); w.WriteValue(s.World.Generation);
                    w.WriteEndObject();
                }
                w.WritePropertyName("reach"); w.WriteValue(Round(s.Reach));
                w.WritePropertyName("self");
                if (s.Self == null) w.WriteNull();
                else
                {
                    w.WriteStartObject();
                    Field(w, "name", s.Self.Name);
                    Number(w, "x", s.Self.X); Number(w, "y", s.Self.Y); Number(w, "z", s.Self.Z);
                    Number(w, "heading", s.Self.Heading);
                    w.WritePropertyName("dead"); w.WriteValue(s.Self.Dead);
                    w.WriteEndObject();
                }

                w.WritePropertyName("players"); w.WriteStartArray();
                foreach (var p in s.Players)
                {
                    w.WriteStartObject(); Field(w, "name", p.Name); Number(w, "x", p.X); Number(w, "z", p.Z); w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WritePropertyName("creatures"); w.WriteStartArray();
                foreach (var c in s.Creatures)
                {
                    if (s.Self == null || !Within(c.X - s.Self.X, c.Z - s.Self.Z, range)) continue;
                    w.WriteStartObject();
                    Field(w, "prefab", c.Prefab); Field(w, "name", c.Name);
                    w.WritePropertyName("level"); w.WriteValue(c.Level);
                    Number(w, "hp", c.Health); Number(w, "max", c.MaxHealth);
                    Number(w, "x", c.X); Number(w, "z", c.Z);
                    w.WritePropertyName("tamed"); w.WriteValue(c.Tamed);
                    w.WritePropertyName("boss"); w.WriteValue(c.Boss);
                    w.WritePropertyName("hostile"); w.WriteValue(c.Hostile);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WritePropertyName("pins"); w.WriteStartArray();
                foreach (var p in s.Pins)
                {
                    w.WriteStartObject();
                    w.WritePropertyName("type"); w.WriteValue(p.Type);
                    Field(w, "name", p.Name); Number(w, "x", p.X); Number(w, "z", p.Z);
                    w.WritePropertyName("checked"); w.WriteValue(p.Checked);
                    w.WritePropertyName("shared"); w.WriteValue(p.Shared);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WritePropertyName("pinWrites"); w.WriteValue(s.PinWrites);
                /* Mod 1.2.1: the show-on-map key, so the page can say which key to press; null when it is None. */
                w.WritePropertyName("focusKey");
                if (s.FocusKey == null) w.WriteNull(); else w.WriteValue(s.FocusKey);

                /* Mod 1.2: the ship the player is on, the wind, and the version of the explored planes (GET /v1/explored). */
                w.WritePropertyName("ship");
                if (s.Ship == null) w.WriteNull();
                else
                {
                    w.WriteStartObject();
                    Field(w, "prefab", s.Ship.Prefab); Field(w, "name", s.Ship.Name);
                    Number(w, "x", s.Ship.X); Number(w, "z", s.Ship.Z);
                    Number(w, "heading", s.Ship.Heading); Number(w, "speed", s.Ship.Speed);
                    Field(w, "sail", s.Ship.Sail);
                    w.WriteEndObject();
                }
                w.WritePropertyName("wind");
                if (s.Wind == null) w.WriteNull();
                else
                {
                    w.WriteStartObject();
                    Number(w, "angle", s.Wind.Angle);
                    w.WritePropertyName("intensity"); w.WriteValue(Math.Round(s.Wind.Intensity, 2));
                    w.WriteEndObject();
                }
                w.WritePropertyName("exploredVersion");
                if (s.ExploredVersion.HasValue) w.WriteValue(s.ExploredVersion.Value); else w.WriteNull();
                /* Mod 1.3: the version of the portal list (GET /v1/portals), so the page refetches it only on change. */
                w.WritePropertyName("portalsVersion");
                if (s.PortalsVersion.HasValue) w.WriteValue(s.PortalsVersion.Value); else w.WriteNull();
                /* Mod 1.5: the version of the remembered buildings (GET /v1/buildings). */
                w.WritePropertyName("buildingsVersion");
                if (s.BuildingsVersion.HasValue) w.WriteValue(s.BuildingsVersion.Value); else w.WriteNull();
                /* Mod 1.4: whether the page may track a target in the game, and the target it tracks. */
                w.WritePropertyName("tracking"); w.WriteValue(s.Tracking);
                w.WritePropertyName("track");
                if (s.Track == null) w.WriteNull();
                else
                {
                    w.WriteStartObject();
                    Number(w, "x", s.Track.X); Number(w, "z", s.Track.Z);
                    Field(w, "label", s.Track.Label);
                    Number(w, "clearWithin", s.Track.ClearWithin);
                    w.WriteEndObject();
                }

                w.WritePropertyName("icons"); w.WriteStartArray();
                foreach (var i in s.Icons)
                {
                    w.WriteStartObject(); Field(w, "prefab", i.Prefab); Number(w, "x", i.X); Number(w, "z", i.Z); w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return text.ToString();
        }

        /// <summary>The focus event: what the player pointed at in the game when pressing the show-on-map key.</summary>
        public static string Focus(FocusTarget target)
        {
            var text = new StringWriter(CultureInfo.InvariantCulture);
            using (var w = new JsonTextWriter(text))
            {
                w.WriteStartObject();
                w.WritePropertyName("v"); w.WriteValue(Protocol);
                Field(w, "type", "focus");
                Field(w, "kind", target.Kind);
                Field(w, "prefab", target.Prefab);
                w.WriteEndObject();
            }
            return text.ToString();
        }

        /// <summary>GET /v1/explored: <c>{ v, size, version, explored, others }</c>, both planes base64 of the packed bits.</summary>
        public static string Explored(ExploredPlanes planes)
        {
            var text = new StringWriter(CultureInfo.InvariantCulture);
            using (var w = new JsonTextWriter(text))
            {
                w.WriteStartObject();
                w.WritePropertyName("v"); w.WriteValue(Protocol);
                w.WritePropertyName("size"); w.WriteValue(planes.Size);
                w.WritePropertyName("version"); w.WriteValue(planes.Version);
                Field(w, "explored", Convert.ToBase64String(planes.Explored));
                Field(w, "others", Convert.ToBase64String(planes.Others));
                w.WriteEndObject();
            }
            return text.ToString();
        }

        /// <summary>An error answer: <c>{ error, message }</c>, error a stable code the page can branch on.</summary>
        public static string Error(string code, string message)
        {
            var text = new StringWriter(CultureInfo.InvariantCulture);
            using (var w = new JsonTextWriter(text))
            {
                w.WriteStartObject();
                Field(w, "error", code);
                Field(w, "message", message);
                w.WriteEndObject();
            }
            return text.ToString();
        }

        /// <summary>
        /// A KeyboardShortcut as the player reads it: modifiers first, then the main key, by their KeyCode names
        /// ("J", "LeftControl + J"). KeyboardShortcut.ToString puts the main key first and says "Not set" for None.
        /// </summary>
        public static string KeyLabel(BepInEx.Configuration.KeyboardShortcut shortcut)
        {
            if (shortcut.MainKey == UnityEngine.KeyCode.None) return null;
            var keys = new System.Collections.Generic.List<string>();
            foreach (var modifier in shortcut.Modifiers) if (modifier != UnityEngine.KeyCode.None && modifier != shortcut.MainKey) keys.Add(modifier.ToString());
            keys.Add(shortcut.MainKey.ToString());
            return string.Join(" + ", keys);
        }

        static bool Within(float dx, float dz, float range) => dx * dx + dz * dz <= range * range;
        static double Round(float value) => Math.Round(value, 1);
        static void Field(JsonWriter w, string name, string value) { w.WritePropertyName(name); w.WriteValue(value); }
        static void Number(JsonWriter w, string name, float value) { w.WritePropertyName(name); w.WriteValue(Round(value)); }
    }
}
