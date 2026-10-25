using System;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace LiveSeedMap
{
    /// <summary>A pin write the server thread hands to the main thread and waits on.</summary>
    public sealed class PinCommand
    {
        public enum Kind { Add, Remove, Check }
        public Kind Action;
        public string Body;
        public int Status = 500;
        public string Response = "";
        /// <summary>The server stopped waiting; the main thread skips it.</summary>
        public volatile bool Abandoned;
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
    }

    /// <summary>
    /// The body of a pin write, checked before the game sees it: <c>{ type, name, x, z, checked }</c>. Free of Unity
    /// types so it can be checked outside the game (mod/check).
    /// </summary>
    public sealed class PinArgs
    {
        /// <summary>Minimap.PinType Icon0, Icon1, Icon2, Icon3 and Icon4 (fire, house, hammer, dot, portal): the pins
        /// the player places on the large map. Icon4 is 6; Death (4) and Bed (5) sit between.</summary>
        public static readonly int[] Placeable = { 0, 1, 2, 3, 6 };
        public const int MaxName = 64;
        /// <summary>World radius 10 000 m plus the edge the game still draws.</summary>
        public const float MaxCoordinate = 10500f;

        public int Type;
        public string Name;
        public float X, Z;
        public bool? Checked;

        /// <summary>The parsed pin, or null with <paramref name="problem"/> set to the JSON error to answer.</summary>
        public static PinArgs Parse(string body, out string problem)
        {
            problem = null;
            JObject row;
            try { row = JObject.Parse(body ?? ""); }
            catch (Exception) { problem = Json.Error("bad-request", "The body is not a JSON object."); return null; }
            if (row["type"] == null || row["type"].Type != JTokenType.Integer)
            { problem = Json.Error("bad-request", "\"type\" must be a whole number."); return null; }
            var type = row["type"].Value<long>();
            if (type < 0 || type > 64 || Array.IndexOf(Placeable, (int)type) < 0)
            { problem = Json.Error("pin-type", "Only fire, house, hammer, dot and portal pins (types 0, 1, 2, 3, 6) can be written."); return null; }
            var name = "";
            var nameToken = row["name"];
            if (nameToken != null && nameToken.Type != JTokenType.Null)
            {
                if (nameToken.Type != JTokenType.String) { problem = Json.Error("bad-request", "\"name\" must be a string."); return null; }
                name = new string(nameToken.Value<string>().Where(c => !char.IsControl(c)).ToArray());
                if (name.Length > MaxName) { problem = Json.Error("bad-request", $"\"name\" is longer than {MaxName} characters."); return null; }
            }
            if (!Coordinate(row["x"], out var x) || !Coordinate(row["z"], out var z))
            { problem = Json.Error("bad-request", $"\"x\" and \"z\" must be numbers within {MaxCoordinate} m of the world centre."); return null; }
            bool? isChecked = null;
            var checkedToken = row["checked"];
            if (checkedToken != null && checkedToken.Type != JTokenType.Null)
            {
                if (checkedToken.Type != JTokenType.Boolean) { problem = Json.Error("bad-request", "\"checked\" must be true or false."); return null; }
                isChecked = checkedToken.Value<bool>();
            }
            return new PinArgs { Type = (int)type, Name = name, X = x, Z = z, Checked = isChecked };
        }

        static bool Coordinate(JToken token, out float value)
        {
            value = 0f;
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)) return false;
            var number = token.Value<double>();
            if (double.IsNaN(number) || double.IsInfinity(number) || Math.Abs(number) > MaxCoordinate) return false;
            value = (float)number;
            return true;
        }
    }
}
