using System;
using System.Linq;
using System.Net.Sockets;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace LiveSeedMap
{
    [BepInPlugin(Guid, "Live Seed Map", Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "wiki.thevalheim.liveseedmap";
        public const string Version = "1.5.0";
        /// <summary>The one port the seed map looks for; a page cannot learn another.</summary>
        public const int Port = 45892;
        /// <summary>The shortest Interval the mod keeps, so a typo such as 0 does not capture every frame.</summary>
        public const float MinInterval = 0.1f;

        ConfigEntry<float> interval;
        ConfigEntry<bool> allowPinWrites;
        ConfigEntry<KeyboardShortcut> showOnMapKey;
        ConfigEntry<bool> allowTracking;
        ConfigEntry<float> clearWithin;
        LiveServer server;
        float wait;
        string portalError;
        string buildingError;
        string trackError;

        void Awake()
        {
            interval = Config.Bind("Link", "Interval", 0.25f, $"Seconds between updates sent to the map; at least {MinInterval}.");
            var origins = Config.Bind("Link", "Origins", "https://thevalheim.wiki", "Web pages allowed to read your game, separated by commas.");
            allowPinWrites = Config.Bind("Pins", "AllowPinWrites", false,
                "Let the seed map add, remove and check off your own map pins (fire, house, hammer, dot and portal). Off: the map only reads them.");
            /* J is free in the game's default bindings (ZInput.Reset) and its debug keys (Player: Z, B, K, L). */
            showOnMapKey = Config.Bind("Keys", "ShowOnMapKey", new KeyboardShortcut(KeyCode.J),
                "Point at an item (inventory, chest, crafting list, on the ground) or a creature and press this key to show it on the seed map.");
            allowTracking = Config.Bind("Track", "AllowTracking", true,
                "Let the seed map show a target in your game as a ping only you see, with its distance. It changes nothing in the world.");
            clearWithin = Config.Bind("Track", "ClearWithin", 0f,
                "Metres from a tracked target at which the game removes it, when the seed map does not say; 0 keeps it until the map removes it.");
            server = new LiveServer(Port, origins.Value.Split(','), Logger) { PinWritesAllowed = () => allowPinWrites.Value, PortalsJson = () => Portals.Json,
                BuildingsJson = Buildings.Json, TrackingAllowed = () => allowTracking.Value };
            try { server.Start(); }
            catch (SocketException error)
            {
                Logger.LogError($"Port {Port} on 127.0.0.1 is taken by another program; the seed map reaches this game once it is free: {error.Message}");
                server = null;
            }
        }

        void Update()
        {
            /* Portals are remembered whether or not a page reads them, so the list is there when one does. */
            try { Portals.Tick(Time.unscaledTime, Logger); portalError = null; }
            catch (Exception error)
            {
                /* Once per distinct failure, not every frame. */
                if (error.Message != portalError) Logger.LogError(error);
                portalError = error.Message;
            }
            /* Buildings too (mod 1.5). */
            try { Buildings.Tick(Time.unscaledTime, Logger); buildingError = null; }
            catch (Exception error)
            {
                if (error.Message != buildingError) Logger.LogError(error);
                buildingError = error.Message;
            }
            /* A tracked target stays in the game whether or not a page reads, until the page clears it. */
            var changed = false;
            try { changed = Track.Tick(Time.unscaledDeltaTime); }
            catch (Exception error)
            {
                if (error.Message != trackError) Logger.LogError(error);
                trackError = error.Message;
            }
            if (server == null) return;
            while (server.TryTakeCommand(out var command))
            {
                if (!command.Abandoned) { PinWrites.Execute(command); changed = true; }
                command.Done.Set();
            }
            while (server.TryTakeTrack(out var track))
            {
                if (!track.Abandoned) { Track.Execute(track, clearWithin.Value); changed = true; }
                track.Done.Set();
            }
            if (!allowTracking.Value && Track.Active) { Track.Clear(); changed = true; }
            while (server.TryTakeExplored(out var read))
            {
                try { if (!read.Abandoned) read.Planes = Explored.Read(Time.unscaledTime); }
                catch (Exception error) { Logger.LogError(error); }
                read.Done.Set();
            }
            while (server.TryTakeNearby(out var nearby))
            {
                try { if (!nearby.Abandoned) nearby.Json = Nearby.Read(nearby.Range); }
                catch (Exception error) { Logger.LogError(error); }
                nearby.Done.Set();
            }
            if (!server.HasClients) return;
            ShowOnMap();
            wait -= Time.unscaledDeltaTime;
            /* A pin write shows on the map with the next frame, not a whole interval later. */
            if (wait > 0f && !changed) return;
            wait = Mathf.Max(MinInterval, interval.Value);
            try
            {
                var snapshot = Capture.Take();
                snapshot.PinWrites = allowPinWrites.Value;
                snapshot.FocusKey = Json.KeyLabel(showOnMapKey.Value);
                snapshot.Tracking = allowTracking.Value;
                server.Publish(snapshot);
            }
            catch (Exception error) { Logger.LogError(error); }
        }

        /// <summary>The show-on-map key, read through the game's own input (ZInput) so it works with its input system.</summary>
        void ShowOnMap()
        {
            var shortcut = showOnMapKey.Value;
            if (shortcut.MainKey == KeyCode.None || ZInput.instance == null || !ZInput.GetKeyDown(shortcut.MainKey, false)) return;
            if (shortcut.Modifiers.Any(modifier => !ZInput.GetKey(modifier, false))) return;
            try
            {
                if (Focus.Typing()) return;
                var target = Focus.Find();
                if (target == null || string.IsNullOrEmpty(target.Prefab)) return;
                server.PublishFocus(target);
            }
            catch (Exception error) { Logger.LogError(error); }
        }

        void OnDestroy()
        {
            Portals.Flush();
            Buildings.Flush();
            try { Track.Clear(); } catch (Exception) { }
            server?.Stop();
        }
    }
}
