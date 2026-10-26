# BatCoop: online co-op for Batman: Arkham City
by NickBin (RealAnxiousGit)

Roam Arkham City together. Every player runs their own game and sees the other players move through the
city in real time. Friends join over Steam without opening ports, or over a direct IP address on a LAN or VPN.

Status: **early (0.1.0)**. Players see each other; the world is not shared yet. See "What syncs" below.

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
   `[BatCoop] ... compiled` and `BatCoop 0.1.0 ... loaded`.

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

## What syncs (0.1.0)
| Synced | How |
|---|---|
| Every player's position, facing and velocity | 20 times a second, smoothed on the other machines (100 ms interpolation, short extrapolation, teleports snap) |
| Which character each player is (Batman, Catwoman, Robin, Nightwing) | Remote players appear as that character's player model when it is loaded in your world, otherwise as Batman |
| Crouch, glide, death and hidden (cutscene) flags | Sent with every update; hidden players are hidden on your screen too |
| Who is in the game, names, ping, which map each player is in | Shown in the overlay; players in a different map are listed but not drawn |
| Chat messages (lab and bridge only for now) | Reliable messages |

**Not synced yet:** remote players' animations (they move, turn and glide along the right path, but their body
holds the default idle pose instead of running, gliding or fighting), enemies and fights, story progress and
cutscenes, Riddler trophies and collectibles, gadgets and their effects. Each player's world runs on its own; the
host's world is not forced onto the guests. Those are the next steps, roughly in that order.

**Alt-tab:** the game pauses itself when its window loses focus, which also freezes you for everyone else.
Set `RunInBackground=1` in BatCoop.ini to keep playing in the background.

## How it works
- `scripts\Core`: game-independent code (wire format, session, interpolation). The same files are built into
  the link and the lab.
- `scripts\Game`: the BmSDK side. Reads the local pawn every frame, spawns one collision-free puppet pawn per remote
  player and moves it, draws the overlay through a redirect of `RHUD.PostRender`.
- `link\BatCoopLink.exe`: a small helper the mod starts when you host or join. Arkham City itself is Steam app
  200260, and a process can only be one Steam app, so the Steam networking (lobbies, invites,
  ISteamNetworkingMessages relayed by Valve) runs in this separate process as Spacewar. The mod talks to it over
  `127.0.0.1:LinkPort`. It exits when the game exits.
- Star topology: the host's link numbers the players and forwards each guest's updates to the other guests.

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
