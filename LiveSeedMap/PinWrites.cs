using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LiveSeedMap
{
    /// <summary>
    /// The player's own pins changed from the seed map, on the main thread, through the game's own Minimap.AddPin and
    /// Minimap.RemovePin. Only the pin types the player places on the large map (PinArgs.Placeable) and only the
    /// player's own saved pins (m_save, m_ownerID 0) are touched. A pin is matched by type, name and a position within
    /// 1 m, the radius Minimap.HaveSimilarPin uses; the stream rounds positions to 0.1 m. Adding a pin that already
    /// matches adds nothing, so a repeated write is harmless.
    /// </summary>
    public static class PinWrites
    {
        public const float MatchRadius = 1f;
        static readonly FieldInfo UpdateField = AccessTools.Field(typeof(Minimap), "m_pinUpdateRequired");

        public static void Execute(PinCommand command)
        {
            try { Run(command); }
            catch (Exception error)
            {
                command.Status = 500;
                command.Response = Json.Error("failed", error.Message);
            }
        }

        static void Run(PinCommand command)
        {
            if (!Minimap.instance || !Player.m_localPlayer)
            {
                command.Status = 409;
                command.Response = Json.Error("no-world", "Load a world first; the game has no map open.");
                return;
            }
            var args = PinArgs.Parse(command.Body, out var problem);
            if (args == null)
            {
                command.Status = 400;
                command.Response = problem;
                return;
            }
            if (command.Action == PinCommand.Kind.Check && args.Checked == null)
            {
                command.Status = 400;
                command.Response = Json.Error("bad-request", "\"checked\" must be true or false.");
                return;
            }
            var minimap = Minimap.instance;
            var at = new Vector3(args.X, 0f, args.Z);
            var pins = (List<Minimap.PinData>)Capture.PinsField.GetValue(minimap);
            var matches = pins.Where(pin => pin.m_save && pin.m_ownerID == 0L && (int)pin.m_type == args.Type && pin.m_name == args.Name &&
                                            global::Utils.DistanceXZ(pin.m_pos, at) <= MatchRadius).ToList();
            command.Status = 200;
            switch (command.Action)
            {
                case PinCommand.Kind.Add:
                    if (matches.Count == 0) minimap.AddPin(at, (Minimap.PinType)args.Type, args.Name, true, args.Checked ?? false, 0L);
                    else if (args.Checked != null) foreach (var pin in matches) pin.m_checked = args.Checked.Value;
                    command.Response = "{\"ok\":true,\"added\":" + (matches.Count == 0 ? "true" : "false") + "}";
                    break;
                case PinCommand.Kind.Remove:
                    foreach (var pin in matches) minimap.RemovePin(pin);
                    command.Response = "{\"ok\":true,\"removed\":" + matches.Count.ToString(CultureInfo.InvariantCulture) + "}";
                    break;
                default:
                    foreach (var pin in matches) pin.m_checked = args.Checked.Value;
                    command.Response = "{\"ok\":true,\"changed\":" + matches.Count.ToString(CultureInfo.InvariantCulture) + "}";
                    break;
            }
            /* The large map redraws its pins, and their checked crosses, on its next update. */
            try { UpdateField?.SetValue(minimap, true); } catch (Exception) { }
        }
    }
}
