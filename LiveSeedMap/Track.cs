using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LiveSeedMap
{
    /// <summary>The target the page tracks, as the snapshot carries it.</summary>
    public sealed class TrackInfo { public float X, Z, ClearWithin; public string Label; }

    /// <summary>
    /// "Track in game" (mod 1.4): one target from the seed map shown as the game's own ping, local to this game and never
    /// sent to anyone. The ping is Chat's world text of type Talker.Type.Ping, added through the private
    /// Chat.AddInworldText with a fixed sender id of this mod, so it takes the ping's colour, stays clamped to the screen
    /// edge while off screen (Chat.UpdateWorldTexts) and shows on the minimap and large map as a ping pin
    /// (Minimap.UpdatePingPins reads Chat.GetPingWorldTexts). Chat.SendPing, the routed ChatMessage RPC, is never called.
    /// Chat drops a world text after m_worldTextTTL (5 s) and lifts it by 0.15 m a second; every frame here resets its
    /// timer and puts it back at the ground height, so it stays until the page clears it. The ping reads
    /// "label: distance", the game's own "name: text" of a ping (Chat.UpdateWorldTextField, Minimap.UpdatePingPins).
    /// </summary>
    public static class Track
    {
        /// <summary>The world text's sender id; no player's id (a platform user id hash) is this small.</summary>
        public const long SenderId = 0x4C534D54; // "LSMT"
        /// <summary>How far above the ground the ping floats (Chat adds 0.3 m to the text).</summary>
        const float Lift = 1.5f;
        /// <summary>How often the distance in the text is rewritten.</summary>
        const float TextInterval = 0.25f;

        static readonly MethodInfo AddText = AccessTools.Method(typeof(Chat), "AddInworldText");
        static readonly MethodInfo UpdateField = AccessTools.Method(typeof(Chat), "UpdateWorldTextField");

        static TrackArgs target;
        static long world;
        static float groundY = float.NaN;
        static float textWait;
        static string shown;

        /// <summary>A target is shown.</summary>
        public static bool Active => target != null;

        /// <summary>The active target for the snapshot, null without one.</summary>
        public static TrackInfo Current => target == null ? null : new TrackInfo { X = target.X, Z = target.Z, Label = target.Label, ClearWithin = target.ClearWithin ?? 0f };

        /// <summary>POST /v1/track (a target) or DELETE /v1/track (null), on the main thread.</summary>
        public static void Execute(TrackCommand command, float defaultClearWithin)
        {
            try
            {
                var loaded = ZNet.instance ? (World)Capture.WorldField.GetValue(null) : null;
                if (loaded == null || !Chat.instance)
                {
                    command.Status = 409;
                    command.Response = Json.Error("no-world", "Load a world first; the game has no world to show the target in.");
                    return;
                }
                var had = target != null;
                Clear();
                if (command.Target != null)
                {
                    target = command.Target;
                    if (target.ClearWithin == null) target.ClearWithin = Mathf.Max(0f, defaultClearWithin);
                    world = loaded.m_uid;
                    Show();
                }
                command.Status = 200;
                command.Response = command.Target != null ? "{\"ok\":true,\"tracking\":true}" : "{\"ok\":true,\"cleared\":" + (had ? "true" : "false") + "}";
            }
            catch (Exception error)
            {
                command.Status = 500;
                command.Response = Json.Error("failed", error.Message);
            }
        }

        /// <summary>
        /// Every frame: keeps the ping alive and on the ground, rewrites its distance a few times a second, and clears
        /// the target when the world it was set in is left or the player arrives within its clear radius. True when
        /// the target was cleared, so the page hears of it with the next snapshot.
        /// </summary>
        public static bool Tick(float dt)
        {
            if (target == null) return false;
            var loaded = ZNet.instance ? (World)Capture.WorldField.GetValue(null) : null;
            if (loaded == null || loaded.m_uid != world || !Chat.instance) { Clear(); return true; }
            var player = Player.m_localPlayer;
            var distance = float.NaN;
            if (player)
            {
                var p = player.transform.position;
                distance = Mathf.Sqrt((p.x - target.X) * (p.x - target.X) + (p.z - target.Z) * (p.z - target.Z));
                if (target.ClearWithin > 0f && distance <= target.ClearWithin) { Clear(); return true; }
            }
            var text = Find();
            if (text == null) { Show(); text = Find(); if (text == null) return false; }
            text.m_timer = 0f;
            if (float.IsNaN(groundY) || textWait <= 0f) groundY = Ground();
            text.m_position = new Vector3(target.X, groundY + Lift, target.Z);
            textWait -= dt;
            if (textWait <= 0f)
            {
                textWait = TextInterval;
                var line = float.IsNaN(distance) ? "" : TrackArgs.Distance(distance);
                if (line != shown)
                {
                    shown = line;
                    text.m_text = line;
                    UpdateField.Invoke(Chat.instance, new object[] { text });
                }
            }
            return false;
        }

        /// <summary>Removes the ping from Chat's world texts; the minimap drops its ping pin on its next update.</summary>
        public static void Clear()
        {
            target = null;
            shown = null;
            groundY = float.NaN;
            var text = Find();
            if (text == null) return;
            if (text.m_gui) UnityEngine.Object.Destroy(text.m_gui);
            Chat.instance.WorldTexts.Remove(text);
        }

        static void Show()
        {
            groundY = Ground();
            shown = null;
            textWait = 0f;
            var local = UserInfo.GetLocalUser();
            var user = new UserInfo { Name = target.Label.Length > 0 ? target.Label : "?", UserId = local.UserId };
            AddText.Invoke(Chat.instance, new object[] { null, SenderId, new Vector3(target.X, groundY + Lift, target.Z), Talker.Type.Ping, user, "" });
        }

        static Chat.WorldTextInstance Find()
        {
            if (!Chat.instance) return null;
            foreach (var text in Chat.instance.WorldTexts) if (text.m_talkerID == SenderId) return text;
            return null;
        }

        /// <summary>
        /// The ground under the target: the loaded terrain where the game has it (ZoneSystem.GetGroundHeight), else the
        /// generated height (WorldGenerator.GetHeight), never below the water surface.
        /// </summary>
        static float Ground()
        {
            var at = new Vector3(target.X, 0f, target.Z);
            var water = ZoneSystem.instance ? ZoneSystem.instance.m_waterLevel : 30f;
            float height;
            if (!(ZoneSystem.instance && ZoneSystem.instance.GetGroundHeight(at, out height)))
                height = WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight(target.X, target.Z) : water;
            return Mathf.Max(height, water);
        }
    }
}
