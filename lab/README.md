# BatCoop testing lab

Three layers, cheapest first. Each prints `PASS`/`FAIL` lines and a final `RESULT:` line.

| Layer | Command | Needs | What it proves |
|---|---|---|---|
| Compile check | `python -I lab/build.py check` | BmSDK installed in the game | The mod compiles exactly as BmSDK compiles it at game start (C# 14, x86, BmSDK's global usings and references). One compile error would disable the whole mod in game. |
| Offline lab | `python -I lab/run_offline.py` | nothing but this repo | Real `BatCoopLink.exe` processes with bot players running the mod's own `CoopSession`: wire format, interpolation under jitter and loss, 3 players, guest-to-guest forwarding, chat, ping, room full, version mismatch, bad address, guest leaving, host leaving, and 15 s of movement through a 90±35 ms lag proxy judged by coop-anything animcheck. |
| In-game lab | `python -I lab/ingame.py [--keep] [--seconds 15]` | Steam running, the game installed with BmSDK and BatCoop (`lab/deploy.py install`) | One real Arkham City copy enters the open world by itself, hosts, a bot joins and circles it, the game's player circles too; checks the puppet spawns, moves and is removed, the bot receives the game's player, the game's own screenshot, and animcheck on both machines' probe streams. |
| In-game sync lab | `python -I lab/ingame_sync.py [--keep] [--skip-travel]` | as above | World, story, mission, NPC, pause and cutscene-vote sync with one real game copy: run A the game hosts and a bot guest joins; run B a bot host replays NPCs recorded in run A and the game joins as the guest (applies host story, binds the host's NPCs to its own level NPCs, forwards hits, votes, refuses pause, follows the host's map). |
| Mutation twins | part of `run_offline.py` | nothing | `BATCOOP_MUT=guest_local_write|no_epoch|skip_twice|no_digest` each turns one protection off; its test must fail. |

Interactive work: `python -I lab/launch.py start host` keeps a copy running, `python -I lab/bridge.py 37900 <command>`
talks to it, `python -I lab/launch.py stop` closes it by pid. BmSDK hot-reloads the mod when `lab/deploy.py install`
changes a script, so most fixes need no restart.

## How the in-game lab drives the game without input
- Settings go in as `BATCOOP_*` environment variables (they override `BatCoop.ini` keys of the same name), so the
  player's ini is never edited: `LabPort` opens the lab bridge, `LabEnterWorld=1` enters the open world 3 s after
  the main menu, `ProbePath`/`ProbeMachine` write an animcheck probe stream.
- The world is entered the way BmSDK's own DemoScript does it: `SaveGameManager.SetActiveStorySlotID(StorySlot_Temp)`
  then `start batentry?...`. Real save slots are not touched.
- The lab bridge (`scripts/Game/LabBridge.cs`) is a line protocol on `127.0.0.1:LabPort`, one JSON answer per line:
  `ping`, `state`, `enterworld`, `host steam|direct`, `join steam|direct <target>`, `leave`, `chat <text>`,
  `exec <console command>`, `tp x y z`, `walk <radius> <seconds>`, `puppets player|none [mirror]`,
  `probe <path> <machine>|off`, and for sync: `flags`, `getflag`, `setflag`, `chapter`, `world`, `npcs`, `npcdump`,
  `hit`, `pause`, `paused`, `unpause`, `watch on|off|auto`, `skip`, `vote`, `hooks`. It is closed unless `LabPort` is set.
- The game copy is started with a coop-anything `gameslot` lease and closed only by the PIDs this run started.
  If Arkham City is already running, the lab refuses to start.

## Bot player
`dotnet lab/Offline/bin/Release/net10.0/Offline.dll bot --link-exe <BatCoopLink.exe> --join host:port --center x,y,z`
joins any game copy (direct transport), circles, writes `--probe` and prints a `BOT_SUMMARY {json}` line.

## Probe ids
A player's own body is `0x100 + player id`; the puppet that shows player N on another machine is `0x200 + N`.
So `--avatar host=game:0x100>bot:0x200` compares the host's real movement with what the bot rendered.

## What the lab cannot cover
- Two Steam accounts: one PC runs one Steam account, so Steam lobby join and Steam P2P between two players need
  two PCs. `lab/steam_smoke.py` covers what one account can: Steam starts as Spacewar and a tagged lobby is created.
- Two game copies on one PC: the Steam wrapper refuses a second running copy (memory is no longer the limit, and the
  game's own mutex can be released); a bot plays the other side in each role instead.
- Real input: a real punch, a real skip key press and a real story cutscene were not driven (no input automation);
  the lab calls the same functions the hooks call.

## 0.3 additions
| Tool | What it checks |
|---|---|
| `python -I lab/pvp_check.py [--seconds 50] [--counter-every 2]` | PVP with one real game as the defender and a bot attacking it (`sh lab/botfriend.sh ... --stand-after 1 --pvp-attack-every 6`): the hidden proxy stays inside the bot's puppet (counter icon position), forced thug attacks land, perfect-timing counters (0.25 s after the icon) counter them, a defeat comes back up. |
| `python -I lab/track_check.py` | How far the animated puppet is from its interpolated target (xy and height) while a bot circles. |
| `sh lab/botfriend.sh <s> <radius> <speed> <log> [x,y,z] [bot args]` | Hosts on the game copy (bridge 37900) and starts a bot that joins, circles or stands, attacks in PVP and chats. |
| `python -I lab/shot.py 37900 out.png` | The game's own screenshot as PNG. |
| `python -I lab/display_keep.py show\|save\|restore` | The player's resolution in `BmEngine.ini`. Lab copies run in a window and the game saves its window size on quit, so `launch.py` and `ingame.py` save it before a run and restore it after. |
| `powershell -File lab/AudioMeter.ps1 -ProcessId <pid>` | Peak level of the game's Windows audio session (an unfocused game window reports silence, so this only helps with a focused window). |

New bridge commands: `insp <expr> [depth] [filter]`, `methods <expr> [filter]`, `call <expr> <Method> [args]`,
`set <expr> <Prop> <value>`, `find <Type> [n] [name]` (reflection on live game objects; roots `pawn pc wi gri pd engine
console puppet npc last obj:<path>|Prop.Sub`), `anim`, `animloop on|off` (our own transitions also replayed on the first
puppet), `hold on|off`, `view`, `pvp`, `pvpshow on|off`, `settings [name value]`, `combat watch|counter [delay]|log [clear]`,
`aithug [character]`, `chatlog`, `ui`, `handles [filter]`, `release <name end>`.
Never pass `null` for a class to native combat functions through `call` (it crashed the game).
