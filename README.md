# BatCoop: online co-op for Batman: Arkham City
by NickBin (RealAnxiousGit)

Roam Arkham City together. Every player runs their own game and sees the other players move through the
city in real time. Friends join over Steam without opening ports, or over a direct IP address on a LAN or VPN.

Status: **0.2.0, early**. Players, the story, missions, NPCs and cutscene skips are shared; see "What syncs" below.

## Requirements
- Batman: Arkham City GOTY, Steam version (Epic should work with BmSDK but is untested here; GOG is not supported by BmSDK).
- **BmSDK-AC v0.21.0** or newer, the script loader for Arkham City: https://github.com/Team-BmSDK/BmSDK-AC/releases
- Steam running and signed in (for Steam play). Nobody needs to buy anything extra: online play runs through
  Valve's free Spacewar app (AppID 480), so Steam shows you as "playing Spacewar" while you are in a co-op game.

# It is HIGHLY recommended you setup dgVoodoo2 to prevent memory crashes, since this is a 32-bit game. (Optional)
You can do so by downloading the latest release from the following repository, then copying "MS/x86/D3D9.dll" to "Batman Arkham City GOTY\Binaries\Win32"
https://github.com/dege-diosg/dgVoodoo2/

## Install
1. Install BmSDK: run its installer, or unzip `BmSDK-AC-v0.21.0.zip` so that its `Binaries` and `BmGame` folders merge
   with the ones in your game folder.
2. Copy the `BatCoop` folder into `<game folder>\BmGame\Mods\`, so you have
   `BmGame\Mods\BatCoop\mod.toml`, `scripts\` and `link\BatCoopLink.exe`.
3. Start the game normally. BmSDK compiles BatCoop at start-up; `Binaries\Win32\BmSDK.log` says
   `[BatCoop] ... compiled` and `BatCoop 0.2.0 ... loaded`.

To uninstall, delete `BmGame\Mods\BatCoop`.

## Playing
Load your story save (everyone in the open world), then:

| Key | What it does |
|---|---|
| **F5** | Host. Steam: opens a friends-only lobby. Direct: listens on `DirectPort` (27961). |
| **F6** | Join. Steam: joins the Steam friend who is hosting (no code needed), or the lobby id on your clipboard. Direct: joins `DirectAddress:DirectPort`. |
| **F7** | Leave the co-op game. |
| **F8** | Go to your friend (teleports you next to the nearest player). |
| **F10** | Host only: copy the lobby id to the clipboard for a friend who cannot find you with F6. |
| **F9** | Hide or show the BatCoop overlay. |

Steam invites also work: when a hosting friend invites you from the Steam friends list (as Spacewar) and you
accept while the game is running, you join automatically.

Settings are in `BmGame\Mods\BatCoop\BatCoop.ini` (created on first start): your name, `Transport=steam|direct`,
`DirectAddress`, `DirectPort`, `MaxPlayers` (2-8, default 4) and `LinkPort`.

## How a co-op game works
**The host's game is the story.** When you join, your story state (flags, chapter, side missions, objectives) is
replaced by the host's, and your game follows the host's map. Play on the host's save; a guest's own save is not
changed by the co-op game until that guest saves it.

- **Missions:** everyone is always on the same mission. If any player starts a mission (walks into its trigger,
  finishes a step), the host takes it over and it starts for everyone at the same time. If the host goes into
  another map, guests are loaded into the same map.
- **NPCs:** the host's game runs every enemy and character near any player. On a guest, those NPCs move the way
  the host's do, and the guest's own copies of them stand still under the host's control. When a guest hits an
  enemy, the hit is sent to the host, which applies it; the result (health, knocked out) comes back to everyone.
- **No pause:** in a co-op game nobody can pause the world, from the pause menu, the map, or by switching windows.
  Menus still open; the city keeps moving behind them.
- **Cutscenes:** pressing skip is a vote. The overlay shows "Cutscene: 1/2 voted to skip"; when everyone watching
  has voted, the cutscene is skipped for everyone at the same moment.

## What syncs (0.2.0)
| Synced | How |
|---|---|
| Every player's position, facing and velocity | 20 times a second, smoothed on the other machines (100 ms interpolation, short extrapolation, teleports snap) |
| Which character each player is | Remote players appear as that character's player model when it is loaded in your world, otherwise as Batman |
| Story flags (missions, doors, events), chapter, 26 side-mission chapters, objectives | Host reads them 4 times a second and sends every change as one numbered step; guests apply steps in order. Joiners get a snapshot; every 5 s guests send a check value and the host repairs any difference |
| A guest's own progress (a mission it starts, a step it completes) | Sent to the host as a request; the host applies it and it comes back to everyone (flags only switch on, chapters only go up this way; anything a guest loses is restored by the host) |
| The host's map | Guests travel to it with the host's own start URL |
| NPCs within 60 m of any player | Host streams position, facing, speed, health, knocked-out and hidden 10 times a second; guests drive the same level NPC, or spawn one of the same class and character |
| Hits on NPCs | Guest hits become requests the host applies once; stale requests from before a reshuffle are ignored |
| Pause | Refused in a co-op game; focus pause is switched off |
| Cutscene skip | Vote; skipped everywhere once all watching players voted |
| Names, ping, chat | Overlay and reliable messages |

**Not synced yet:** remote players' animations (they move along the right path but hold an idle pose), combat
animations of NPCs on guests (they move and get knocked out, but do not play the host's attack animations), the
moment-to-moment state of physics objects and gadgets, and Riddler collectibles beyond their story flags.

## How it works
- `scripts\Core`: game-independent code (wire format, session, interpolation, world/story sync, NPC sync, cutscene
  vote). The same files are built into the link and the lab, where they are tested without the game.
- `scripts\Game`: the BmSDK side. Reads the local pawn every frame, spawns one collision-free puppet pawn per remote
  player and moves it, draws the overlay through a redirect of `RHUD.PostRender`.
- `link\BatCoopLink.exe`: a small helper the mod starts when you host or join. Arkham City itself is Steam app
  200260, and a process can only be one Steam app, so the Steam networking (lobbies, invites,
  ISteamNetworkingMessages relayed by Valve) runs in this separate process as Spacewar. The mod talks to it over
  `127.0.0.1:LinkPort`. It exits when the game exits.
- Star topology: the host's link numbers the players and forwards each guest's updates to the other guests.
- Story flags: Arkham City sets most of its story flags natively, out of reach of script hooks. The mod collects
  every flag name the loaded Kismet scripts mention (`RSeqVar_GlobalFlag`, `RSeqAct_SwitchFlags`,
  `RSeqAct_SetMapFlag`; about 240 in the open world) and the host polls them (under 2 ms).
- Design follows coop-anything's playbook (effect capture, actor ownership with epochs, intents, digest repair).

## Troubleshooting
- **Nothing happens on F5/F6**: read `BmGame\Mods\BatCoop\logs\batcoop.log` and `logs\link.log`.
- **"Steam is not running"**: start Steam before the game.
- **"No Steam friend is hosting BatCoop right now"**: your friend must press F5 first, and you must be Steam friends.
  Otherwise ask them for the lobby id (F10), copy it, and press F6.
- **"Version mismatch"**: everyone needs the same BatCoop version.
- **Firewall prompt for BatCoopLink**: needed only for direct (IP) play as host.

## Building from source and the testing lab
See `lab/README.md`. In short:

```
python -I lab/build.py all          # compile check against BmSDK, link helper, offline lab
python -I lab/run_offline.py        # offline lab: real link processes + bots + animcheck
python -I lab/ingame.py             # in-game lab: launches the game, a bot joins, judged from logs and probes
python -I lab/deploy.py install     # package to dist/BatCoop and install into the game
```

## Credits
- BmSDK-AC by Team-BmSDK (MIT): the script loader this mod runs on.
- Steamworks.NET by Riley Labrecque (MIT), `steam_api64.dll` from Valve's Steamworks SDK.
- coop-anything (cranebeaker, MIT): the Spacewar networking pattern, gameslot and animcheck used by the lab.
- universal-modder (rehan-remade) knowledge base: the Arkham City split-screen field note.
- Built with AI assistance (Claude Code).

Batman: Arkham City is a trademark of its owners. This mod ships no game files.
