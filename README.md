# BatCoop: online co-op for Batman: Arkham City
by NickBin (RealAnxiousGit)

Roam Arkham City together. Every player runs their own game and sees the other players move through the
city in real time. Friends join over Steam without opening ports, or over a direct IP address on a LAN or VPN.

Status: **0.4.1, early**. Players walk, run and fight with the game's own animations; the story, missions, NPCs (with
their fights and knock-outs), enemies attacking every player, story cutscenes and cutscene skips are shared; markers on screen and a radar show where the other
players are; there is a chat and a host menu. See "What syncs" below. PVP is switched off and kept as a draft for a
later version (see "PVP (draft, off)").

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
   `[BatCoop] ... compiled` and `BatCoop 0.4.1 ... loaded`.

Updating from an older version: replace the whole `BatCoop` folder but keep your `BatCoop.ini`. **Everyone in a game
needs 0.4.1**: 0.4.1 refuses 0.4.0 and older with "Version mismatch" (the world and NPC messages changed).

**0.4.1:**
- **The host's game is the only game.** When the host starts or loads a game, every guest joins it by itself,
  straight from the main menu (on the temporary save slot, so a guest's own saves are never written with the
  host's story). A guest cannot start a game of their own: New Game, Continue, Catwoman's episodes, the story DLC
  and the challenge maps say "Only the host starts games" while you are in a co-op room. A guest's game also no
  longer moves the story on by itself (0.4.0 could leave a guest as Batman inside the host's Catwoman level after
  her opening): in-level story changes happen when the host's do.
- **Enemies fight every player.** A thug in a fight goes for whichever player is nearest, not only the host. Its
  attack plays on that player's own screen as a real thug attack, with the counter icon: counter it, or take the hit.
- **Cutscenes stay together.** Both games play the same story, so a level's cutscenes start in each game from the
  same moment of the story; when the host's cutscene ends or is skipped, a guest still watching the same one comes
  out of it too. A guest who joins after the host's opening movie skips it and arrives while the host plays.
  (Starting a cutscene in a guest's game from the mod crashed the game in testing, so BatCoop never does that.)
- **The tiny window after leaving fullscreen is fixed.** If the game window shrinks to a sliver (leaving fullscreen
  with Alt+Enter or the options), BatCoop gives it your resolution back within a second.

**0.4.0:**
- **Knocked-out enemies now go down for guests too.** The guest's copy of a thug the host knocked out was "knocked
  out" with an empty hit the game ignored, so guests saw the host's knocked-out thugs standing. Now the host also
  reads the game's own "dead / knocked out" marks (not only health), and the guest's copy is knocked out with a real
  strike and goes into the host's ragdoll pose.
- **Enemies fight on the guest's screen the way they do on the host's**: every NPC animation (attacks, hit reactions,
  knock-downs, idle actions) is sent like a player's, and the guest's copies walk and run with the game's own movement
  animations instead of sliding to their spots.
- **The pause menu stops your character.** The world still keeps running for everyone, but while your pause menu
  (or map, upgrades...) is open, your movement, camera and gameplay keys do nothing; menu keys and Esc still work.
  Other players see "(in menu)" next to your name.
- **Find each other:** a marker over every other player shows their name, distance and height, through walls; when
  they are off screen it sits on the edge of the screen pointing the way. A radar on the right of the screen
  shows everyone around you from above (the way you look is up) with names. **F4** switches: markers and radar,
  markers only, off. The game's own map screen cannot show other players (it only draws the game's own mission
  icons), so the radar stands in for it; it is hidden while the map screen is open.

**0.3.2 fixed:** the game crashing a few seconds after loading Catwoman's level at the start of a new game (it was
BatCoop's PVP hooks, which were active even with PVP off); "Click to skip the cutscene" showing on the main menu; a
skip vote that passed without skipping; every left click in a co-op game counting as a skip vote; and the other
player's character (and a hidden PVP thug) sometimes being left behind in a level that had just loaded.

To uninstall, delete `BmGame\Mods\BatCoop`.

## Playing
Load your story save (everyone in the open world), then:

| Key | What it does |
|---|---|
| **F5** | Host. Steam: opens a friends-only lobby. Direct: listens on `DirectPort` (27961). |
| **F6** | Join. Steam: joins the Steam friend who is hosting (no code needed), or the lobby id on your clipboard. Direct: joins `DirectAddress:DirectPort`. |
| **F7** | Leave the co-op game. |
| **F8** | Go to your friend (teleports you next to the nearest player). The host can turn this off. |
| **F10** | Host only: copy the lobby id to the clipboard for a friend who cannot find you with F6. |
| **F9** | Hide or show the BatCoop overlay. |
| **F4** | Player markers: on screen and radar, on screen only, or off (saved as `Markers=` in `BatCoop.ini`). |
| **T** | In a co-op game: open the chat. Type, **Enter** sends, **Esc** cancels. |
| **F3** | In a co-op game: the host menu (the host changes settings; guests see them). |

The chat and host menu keys can be changed in the host menu (rows "Chat key" and "Host menu key": select, press
Enter, press the new key) or in `BatCoop.ini` (`ChatKey=`, `MenuKey=`, Unreal key names such as `T`, `Y`, `F2`).
**Note about T:** in Arkham City's own controls T is the "secondary group combat move" (a special combo
takedown). In a co-op game BatCoop takes T for the chat, so that move needs another key there; outside a co-op game
T works as before. If you would rather keep T for combat, set another chat key. BatCoop's keys are not in the
game's own Controls menu (that menu is a fixed Flash screen); they are changed in the host menu instead.

Steam invites also work: when a hosting friend invites you from the Steam friends list (as Spacewar) and you
accept while the game is running, you join automatically.

Your name is your Steam name unless you set `Name=` in `BatCoop.ini` (0.2 showed "Player" when `Name=` was empty).

Settings are in `BmGame\Mods\BatCoop\BatCoop.ini` (created on first start): your name, `Transport=steam|direct`,
`DirectAddress`, `DirectPort`, `MaxPlayers` (2-8, default 4), `LinkPort`, the chat and menu keys, the host's saved
settings (`Host...` keys), `PuppetMode`, `Markers`, `NpcAnimations`, and (0.4.1, all on by default)
`ThugsTargetGuests` (thugs fight every player), `CutsceneSync` (end cutscenes with the host), `HostMapChanges` (in-level
story changes follow the host) and `DisplayGuard` (fix a tiny window or resolution); `=0` turns one off.

## How a co-op game works
**The host's game is the story.** When you join, your story state (flags, chapter, side missions, objectives) is
replaced by the host's, and your game follows the host's map. Guests wait in the main menu until the host starts
or loads a game, then join it by themselves; a guest cannot start one of their own. Guests play on the temporary
save slot, so their own saves are never touched by the co-op game.

- **Missions:** everyone is always on the same mission. If any player starts a mission (walks into its trigger,
  finishes a step), the host takes it over and it starts for everyone at the same time. If the host goes into
  another map, guests are loaded into the same map.
- **NPCs:** the host's game runs every enemy and character near any player. On a guest, the guest's own copies of
  them have no AI: they walk, fight, react and fall as the host's NPCs do. When a guest hits an enemy, the hit is
  sent to the host, which applies it; the result (the hit reaction, health, knocked out) comes back to everyone.
- **Other players** walk, run, sprint, turn and stop with the game's own movement animations, and their strikes,
  counters, hit reactions, takedowns and stance changes play on their character on your screen. When they glide,
  grapple or fall, their character follows their exact position instead.
- **No pause:** in a co-op game nobody can pause the world, from the pause menu, the map, or by switching windows.
  Menus still open; the city keeps moving behind them, but your own character ignores your keys, mouse and pad until
  you close the menu.
- **Finding each other:** markers on screen and a radar (F4), and F8 to go to your friend.
- **Enemies** go for whichever player is nearest. When one attacks you, you see it coming with the counter icon on
  your own screen: counter it and your hit goes to the host's thug; miss it and you take the hit.
- **Cutscenes:** when the host's story cutscene ends or is skipped, guests watching it come out of it too. During a story cutscene in a level, pressing skip is a vote. The overlay shows "Cutscene: 1/2 voted
  to skip"; when everyone watching has voted, the cutscene is skipped for everyone at the same moment. The host can
  set this to "host only". Anywhere else (menus, loading movies, normal play) the skip key works as in the normal
  game and is not a vote. If the game will not skip a cutscene, the overlay says so and stops counting votes for it.
- **Chat:** press T, type, Enter. The chat box also shows when someone joins or leaves, when someone completes a
  mission objective or story chapter, and when a player is taken down by enemies.

### The host menu (F3)
| Setting | Default | What it does |
|---|---|---|
| Go to friend (F8) | ALLOWED | Lets players teleport to each other. |
| Name tags | ON | Names above other players. |
| Cutscene skip | VOTE | VOTE: everyone watching must press skip. HOST ONLY: the host's skip skips for everyone. |
| Event messages in chat | ON | Mission and "taken down" lines in the chat. |

Up/Down choose, Left/Right/Enter change, Esc closes. The host's choices are saved in its `BatCoop.ini` and sent to
every player (also to anyone who joins later).

### PVP (draft, off)
PVP is off in 0.3.2: its game hooks crashed Arkham City at the start of a new game, even with PVP turned off, so they
are no longer built into the mod (the menu rows are gone and a host's `HostPvp=1` is ignored). The design below stays
in the code for a later version, once core co-op is solid. It is kept here for reference.

With PVP on, you fight other players the way you fight thugs: strike at them, chain combos, and **counter** their
attacks when the counter icon flashes over them. Uncountered hits take health like a thug's hit (scaled by
"PVP damage"). A player brought down by another player is **defeated**: "A defeated B" shows in the chat, and B
gets back up with full health after 5 seconds (no game over, no checkpoint reload). Being taken down by enemies
still works the normal way.

How it works: the defender's game decides every hit on the defender, because only it knows if the defender
countered in time. When you strike at a player, your game tells theirs; their game makes your character attack them
with a real thug attack (counter icon, counter window), and either they counter it (you take the counter hit) or the
hit lands. Each player also has an invisible stand-in thug inside their character on your screen, so freeflow
combat aims at them and the counter icon shows over them.

## What syncs (0.4.1)
| Synced | How |
|---|---|
| Every player's position, facing and velocity | 20 times a second, smoothed on the other machines (100 ms interpolation, short extrapolation, teleports snap) |
| Movement animation | The other player's character walks to their interpolated position under the game's own animation system at their movement speed (walk, run, sprint) |
| Strikes, counters, hit reactions, takedowns, stance changes | Every new animation transition of a player (with its clip, target and timing) is sent and started on their character on the other machines 100 ms later |
| Which character each player is | Remote players appear as that character's player model when it is loaded in your world, otherwise as Batman |
| Story flags (missions, doors, events), chapter, 26 side-mission chapters, objectives | Host reads them 4 times a second and sends every change as one numbered step; guests apply steps in order. Joiners get a snapshot; every 5 s guests send a check value and the host repairs any difference |
| A guest's own progress (a mission it starts, a step it completes) | Sent to the host as a request; the host applies it and it comes back to everyone (flags only switch on, chapters only go up this way; anything a guest loses is restored by the host) |
| The host's game | Guests in the main menu join it as soon as the host is in one; a guest cannot start a game of its own while in a room |
| The host's map | Guests travel to it with the host's own start URL (without the opening movie once the host is past it) |
| In-level story changes (Catwoman's opening handing over to Bruce Wayne...) | Made by the host's game and sent to guests, who make the same change; a guest's own game asking for one is refused |
| Story cutscenes | The host reports the cutscene it plays; when it ends, a guest playing the same one ends it too (the guests' cutscenes start from the synced story, never forced) |
| Thugs attacking | The host's thugs pick the nearest player; an attack on a guest is sent to that guest and played there as a real thug attack (counter window, counter or hit) |
| NPCs within 60 m of any player | Host streams position, facing, speed, health, knocked-out, ragdoll and hidden 10 times a second; guests drive the same level NPC (or spawn one of the same class and character) with the game's movement animations |
| NPC animations | Every new animation transition of a shared NPC (attacks, hit reactions, knock-downs, idle actions) is sent once and replayed on the guest's copy after the same delay as its position; a guest that joins later gets each NPC's current stance |
| Knock-outs | A thug is down when the game marks it dead or knocked out (not only at 0 health); the guest's copy is knocked out once with a real strike and stays down |
| Player whereabouts | Markers on screen (name, distance, height, edge arrows) and a radar with every player around you |
| Pause menu | While yours is open, your character ignores input; others see "(in menu)" |
| Hits on NPCs | Guest hits become requests the host applies once; stale requests from before a reshuffle are ignored |
| Host settings | Host -> everyone, on change and on join |
| Pause | Refused in a co-op game; focus pause is switched off |
| Cutscene skip | Vote (or host only); skipped everywhere once all watching players voted |
| Names, ping, chat, event messages | Steam names by default; overlay and chat box; reliable messages |

**Not synced yet:** the moment-to-moment state of physics objects and gadgets, Riddler collectibles beyond their
story flags, gliding and grappling animations (the character follows the exact position there, in its last pose),
and exactly where a ragdolled body comes to rest (each game lets it fall by itself, close to where the host's lies).

**Known limits of 0.4.1** (please report what you see):
- Not yet tested with two real players (see MODLOG): the two-player parts were tested with one real game and a bot.
- The other player's character can lag behind them by up to a meter or two for a moment when they turn sharply or
  climb over things while running (animated characters walk the real floor). `PuppetMode=kinematic` in
  `BatCoop.ini` shows exact positions without movement animation, like 0.2.
- The cutscene vote was checked with the lab's stand-in for "watching a cutscene" and with real skip presses outside
  cutscenes; a vote skipping a real story cutscene in two real games has not been tested yet.
- Thugs go for a guest only in a fight the host's game has started, and only when the guest is within about 6 m of
  them and clearly nearer than the host. A thug fighting a guest does not swing at the host meanwhile.
- Cutscenes are never started on a guest by BatCoop (doing that from a script crashed the game): a guest's
  cutscene plays when its own synced story starts it, and BatCoop only ends it together with the host's.
- Refusing New Game/Continue in the menu and following the host's in-level story changes were tested with lab
  commands, not with real menu clicks or a real Catwoman-to-Batman hand-over in two games.

## How it works
- `scripts\Core`: game-independent code (wire format, session, interpolation, world/story sync, NPC sync, cutscene
  vote, animation calls, PVP (draft, off), chat feed, host settings). The same files are built into the link and the lab, where
  they are tested without the game.
- `scripts\Game`: the BmSDK side. Reads the local pawn every frame, spawns one collision-free puppet pawn per remote
  player and drives it, draws the overlay, chat box and host menu through a redirect of `RHUD.PostRender`, takes
  keys for them through the console's `InputKey`/`InputChar`.
- `link\BatCoopLink.exe`: a small helper the mod starts when you host or join. Arkham City itself is Steam app
  200260, and a process can only be one Steam app, so the Steam networking (lobbies, invites,
  ISteamNetworkingMessages relayed by Valve) runs in this separate process as Spacewar. The mod talks to it over
  `127.0.0.1:LinkPort`. It exits when the game exits. It also reads your Steam name.
- Star topology: the host's link numbers the players and forwards each guest's updates to the other guests.
- Story flags: Arkham City sets most of its story flags natively, out of reach of script hooks. The mod collects
  every flag name the loaded Kismet scripts mention (`RSeqVar_GlobalFlag`, `RSeqAct_SwitchFlags`,
  `RSeqAct_SetMapFlag`; about 240 in the open world) and the host polls them (under 2 ms).
- Animation: Arkham City animates characters through one pose player (`RAnimNode_Pose`); its calls are native, so the
  owner samples its own pose description and the puppet calls the same pose functions (see MODLOG).
- Design follows coop-anything's playbook (effect capture, actor ownership with epochs, intents, digest repair).

## Troubleshooting
- **Nothing happens on F5/F6**: read `BmGame\Mods\BatCoop\logs\batcoop.log` and `logs\link.log`.
- **"Steam is not running"**: start Steam before the game.
- **"No Steam friend is hosting BatCoop right now"**: your friend must press F5 first, and you must be Steam friends.
  Otherwise ask them for the lobby id (F10), copy it, and press F6.
- **"Version mismatch"**: everyone needs the same BatCoop version.
- **Firewall prompt for BatCoopLink**: needed only for direct (IP) play as host.
- **Shown as "Player"**: Steam was not running when the link started; set `Name=` in `BatCoop.ini`.
- **T does not open the chat**: the chat only opens in a co-op game; check `ChatKey=` in `BatCoop.ini`.
- **The game crashes a few seconds into Catwoman's level (new game)**: that was BatCoop 0.3.0/0.3.1; update everyone.
- **Enemies stay standing on a guest's screen after the host knocked them out**: that was 0.3.x; update everyone.
- **"Rendering thread exception" naming `Binaries\Win32\d3d9.dll`**: that is dgVoodoo2's `D3D9.dll` crashing. In
  testing it happened only with two game copies open on one PC, or right after the mod was hot-reloaded while the
  game ran; not in a normal game. If it happens to you, update dgVoodoo2, or move `Binaries\Win32\D3D9.dll` out to
  compare without it, and send the logs.
- **The game starts in a tiny window / at the lowest resolution**: when the game quits while it runs in a window,
  it saves the window's size as your resolution (`Documents\WB Games\Batman Arkham City GOTY\BmGame\Config\BmEngine.ini`,
  `[SystemSettings]` `ResX`/`ResY`/`Fullscreen`), so a shrunk window comes back at every start. Since 0.3.1 BatCoop
  notices a saved size below 640x360, resizes the window at once and puts back the launcher's resolution (or the
  desktop's, fullscreen) after the game quits. Since 0.4.1 it also watches the window while you play: if it shrinks
  below 640x360 (leaving fullscreen), it is resized to your resolution within a second. `DisplayGuard=0` in
  `BatCoop.ini` turns this off.
- **"Version mismatch"**: 0.4.1 only plays with 0.4.1 (protocol `batcoop-5`); everyone updates.

## Building from source and the testing lab
See `lab/README.md`. In short:

```
python -I lab/build.py all          # compile check against BmSDK, link helper, offline lab
python -I lab/run_offline.py        # offline lab: real link processes + bots + animcheck (43 checks + twins)
python -I lab/ingame.py             # in-game lab: launches the game, a bot joins, judged from logs and probes
python -I lab/ingame_sync.py        # in-game story/NPC/pause/vote lab against one real game copy
python -I lab/deploy.py install     # package to dist/BatCoop and install into the game
```

## Credits
- BmSDK-AC by Team-BmSDK (MIT): the script loader this mod runs on.
- Steamworks.NET by Riley Labrecque (MIT), `steam_api64.dll` from Valve's Steamworks SDK.
- coop-anything (cranebeaker, MIT): the Spacewar networking pattern, gameslot and animcheck used by the lab.
- universal-modder (rehan-remade) knowledge base: the Arkham City split-screen field note.
- Built with AI assistance (Claude Code).

Batman: Arkham City is a trademark of its owners. This mod ships no game files.
