using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace LiveSeedMap
{
    /// <summary>A track request the server thread hands to the main thread and waits on (mod 1.4).</summary>
    public sealed class TrackCommand
    {
        /// <summary>The target to show; null clears it (DELETE /v1/track).</summary>
        public TrackArgs Target;
        public int Status = 500;
        public string Response = "";
        /// <summary>The server stopped waiting; the main thread skips it.</summary>
        public volatile bool Abandoned;
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
    }

    /// <summary>
    /// The body of POST /v1/track, checked before the game sees it: <c>{ x, z, label, clearWithin? }</c>. Free of Unity
    /// types so it can be checked outside the game (mod/check).
    /// </summary>
    public sealed class TrackArgs
    {
        public const int MaxLabel = 64;
        /// <summary>The farthest arrival radius a page may ask for; 0 never clears on arrival.</summary>
        public const float MaxClearWithin = 1000f;

        public float X, Z;
        public string Label;
        /// <summary>Metres from the target at which the game clears it; null takes the ClearWithin setting, 0 never.</summary>
        public float? ClearWithin;

        /// <summary>The parsed target, or null with <paramref name="problem"/> set to the JSON error to answer.</summary>
        public static TrackArgs Parse(string body, out string problem)
        {
            problem = null;
            JObject row;
            try { row = JObject.Parse(body ?? ""); }
            catch (Exception) { problem = Json.Error("bad-request", "The body is not a JSON object."); return null; }
            if (!Coordinate(row["x"], PinArgs.MaxCoordinate, out var x) || !Coordinate(row["z"], PinArgs.MaxCoordinate, out var z))
            { problem = Json.Error("bad-request", $"\"x\" and \"z\" must be numbers within {PinArgs.MaxCoordinate} m of the world centre."); return null; }
            var label = "";
            var token = row["label"];
            if (token != null && token.Type != JTokenType.Null)
            {
                if (token.Type != JTokenType.String) { problem = Json.Error("bad-request", "\"label\" must be a string."); return null; }
                label = Clean(token.Value<string>());
                if (label.Length > MaxLabel) { problem = Json.Error("bad-request", $"\"label\" is longer than {MaxLabel} characters."); return null; }
            }
            float? clearWithin = null;
            var clear = row["clearWithin"];
            if (clear != null && clear.Type != JTokenType.Null)
            {
                if (!Coordinate(clear, MaxClearWithin, out var metres) || metres < 0f)
                { problem = Json.Error("bad-request", $"\"clearWithin\" must be a number from 0 to {MaxClearWithin} m."); return null; }
                clearWithin = metres;
            }
            return new TrackArgs { X = x, Z = z, Label = label, ClearWithin = clearWithin };
        }

        /// <summary>
        /// Control characters dropped and angle brackets blanked, as Chat.OnNewChatMessage blanks them before a chat
        /// line reaches the world text, so a label cannot carry TextMeshPro rich-text tags.
        /// </summary>
        public static string Clean(string text) =>
            new string((text ?? "").Where(c => !char.IsControl(c)).Select(c => c == '<' || c == '>' ? ' ' : c).ToArray()).Trim();

        /// <summary>A distance as the ping reads it: whole metres below 1000 m, else kilometres with one decimal.</summary>
        public static string Distance(float metres)
        {
            if (float.IsNaN(metres) || metres < 0f) metres = 0f;
            return metres < 999.5f
                ? Math.Round(metres).ToString("0", CultureInfo.InvariantCulture) + " m"
                : (metres / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + " km";
        }

        static bool Coordinate(JToken token, float max, out float value)
        {
            value = 0f;
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)) return false;
            var number = token.Value<double>();
            if (double.IsNaN(number) || double.IsInfinity(number) || Math.Abs(number) > max) return false;
            value = (float)number;
            return true;
        }
    }
}
