# Live Seed Map

A realtime map of your Valheim server in a browser, connected to the game data at [thevalheim.wiki](https://thevalheim.wiki/map) through the Live Seed Map companion mod.

- Where you stand and which way you face, four times a second.
- Other players, when they show their position on the in-game map.
- Creatures around you within the range you choose, with their stars.
- Your map pins, with optional editing from the browser.
- Portals and their connections, your ship and wind, and optional fog of war.
- Point at an item or creature and press **J** to find it on the map; track a map target as a ping in your game.
- Experimental building view: player-built pieces and crops your game has loaded.
- Which Haldor, Hildir and Bog Witch camp your world kept. Each has ten possible sites; the first one your world generates stays and the others are removed.
- Nearest-first search from where you stand: copper, a Troll cave, Northern Salmon water.

## Install

1. Install BepInExPack Valheim. On Apple Silicon, follow [Valheim mods on Mac](https://thevalheim.wiki/guides/mods-on-mac) for the native loader instead.
2. Install this mod, or extract the archive into your Valheim folder so the DLL sits in `BepInEx/plugins/LiveSeedMap/`.
3. Start Valheim and load your world.
4. Open [thevalheim.wiki/map](https://thevalheim.wiki/map) and press **Connect game**. Chrome and Edge ask once whether the page may reach apps on this device.

## On your computer only

The mod answers only on `127.0.0.1`, only to pages from thevalheim.wiki, and sends nothing over the internet. Gameplay is read-only by default. Enabling `AllowPinWrites` lets the browser add, remove and check off your own map pins. Portal and building observations are kept locally in `BepInEx/config/LiveSeedMap/`; tracking draws a ping visible only to you.

## Settings

`BepInEx/config/wiki.thevalheim.liveseedmap.cfg`: `Interval` in seconds (`0.25`), `Origins` (`https://thevalheim.wiki`), `AllowPinWrites` (`false`), `ShowOnMapKey` (`J`), and `AllowTracking` (`true`).

Guide: [thevalheim.wiki/guides/live-seed-map](https://thevalheim.wiki/guides/live-seed-map)
