# MODLOG: Batman: Arkham City online co-op (BatCoop)

Journal. Facts first, then decisions, then what failed and why.

## Game identity
- Batman: Arkham City GOTY, Steam AppID 200260, install `<game dir>` = `F:\SteamLibrary\steamapps\common\Batman Arkham City GOTY`.
- `Binaries\Win32\BatmanAC.exe`: PE32 i386, TimeDateStamp 1375731959 (0x520000F7 = the Steam build BmSDK knows).
  Code is encrypted on disk (Steam wrapper), so static reading of the exe on disk shows garbage.
- Engine: Unreal Engine 3 (2011, Rocksteady fork). Default map `FrontEnd`. Open world packages `OW`, `OW_A1..A9`, `OW_R*`, `OW_E*`.
- No saves or config under `Documents\WB Games` yet: the game had not been run on this PC when work started.

## Loader
- BmSDK-AC v0.21.0 (Team-BmSDK, MIT), zip SHA-256 `f477d9daa2aca7fb3b85203a010269c8a15ad077e8926313669c54e04b805196`.
- Installed by copying the zip's `Binaries` and `BmGame` into `<game dir>`; nothing existed at those paths before
  (checked file by file). Every added file is listed in `install/bmsdk-installed-files.txt`. Uninstall = delete those files.
- Adds `Binaries\Win32\dinput8.dll` (ASI loader), `plugins\BmSDK.Host.asi`, `sdk\` (bundled .NET 10 x86 runtime + Roslyn).
- Mods live in `BmGame\Mods\<Name>\mod.toml` + `scripts\**\*.cs`; compiled by Roslyn (C# 14, net10 reference set) at game
  start, hot-reloaded per mod when files change. Log: `Binaries\Win32\BmSDK.log`.
- Script entry points: `Main`, `OnEnterMenu`, `OnEnterGame`, `OnTick`, `OnKeyDown(Keys)`, `OnLoad`, `OnUnload`.
- Main thread work from other threads: `EngineSynchronizationContext.Instance.Post/Send`.
- BmSDK's DemoScript enters the open world from the menu with a temp save slot:
  `SaveGameManager.SetActiveStorySlotID(StorySlot_Temp)` then console `start batentry?Players=Playable_Batman?Area=OW,...?Start=BeginVS`.
  The lab uses this to reach gameplay without menu input and without touching real saves.

## Prior art
- universal-modder KB: "Split-screen co-op fixes for Arkham City (BmSDK C# scripts)". Key facts used here: P1-only logic,
  `RGameRI.PlayerCharacters`, `SetupPlayerMesh` picks the first slot with a matching character, never swap meshes on a live pawn.
- coop-anything DIRECTORY: no Arkham City online co-op exists (Arkham Knight has Nightfall Together). Path 2: build from zero.

## Networking decision (2026-10-26)
- User asked first for the global mod relay, then changed to **Steam networking through Spacewar (AppID 480)**. The relay is not used.
- The game process already runs SteamAPI as 200260, so Spacewar cannot live inside it. Pattern: HXCoop (coop-anything
  `playbook/networking-steam-spacewar.md`): a helper process `BatCoopLink.exe` (x64, .NET 10) runs as AppID 480 and owns
  lobbies, invites and ISteamNetworkingMessages; the mod talks to it over 127.0.0.1 TCP.
- Steamworks.NET Standalone 2025.164.1, `Windows-x64/steam_api64.dll` SHA-256
  `eb17909a76668cf9ae0b92a618a34a50f6c73d3a6787cb4dd8ce36a8b10bfb75` (matches the playbook).
- Second transport `direct` (TCP host:port) for LAN and for the one-PC lab; one Steam account cannot be two players.

## Toolchain
- .NET SDK 10.0.403 extracted from Microsoft's official zip into `tools/dotnet` (git-ignored). The dotnet-install script
  was refused by the permission classifier (needs ExecutionPolicy Bypass), so the zip was used instead.
- `tools/apidump`: MetadataLoadContext dump of `BmSDK.dll` (`dotnet tools/apidump/out/apidump.dll <sdk dir> <Type>`).

## Launching the game for the lab (2026-10-26)
- `BatmanAC.exe -windowed -ResX=1280 -ResY=720` started outside Steam: the Steam wrapper relaunched through Steam,
  and Steam's default target is `BmLauncher.exe` (a window waiting for "Play"). BmSDK had already compiled the
  scripts in the short-lived first process, so a compile line in BmSDK.log does not mean the game is running.
- With Steam's own launch environment (`SteamAppId=200260`, `SteamGameId=200260`) the exe starts directly
  (Steam still checks ownership). The lab closes a BmLauncher it caused, by PID, if this ever falls back.
- `Script.Main` runs about 18 s after launch; the lab's `start batentry?...` puts Batman in map `OW` ~20 s later.
- Child processes inherit `SteamAppId=200260`; the mod removes it when starting BatCoopLink and the link sets 480.

## Steam (one account)
- `lab/steam_smoke.py`: link starts as AppID 480 in ~0.3 s, `CreateLobby(FriendsOnly)` returns a lobby in ~0.2 s,
  leave is clean. Two-account play (lobby join + P2P) is not testable on one PC.

## In-game findings (lab runs 1-4, 2026-10-26)
1. **The world pauses when the window loses focus** (`[Engine.Engine] bPauseOnLossOfFocus=TRUE` in BmEngine.ini) and
   BmSDK's `OnTick` stops with it: no sends, no puppet updates, the bridge stops answering. The mod can clear
   `_Engine.bPauseOnLossOfFocus` in memory (`RunInBackground=1`); the lab always does, players choose in BatCoop.ini.
2. **Never block the game thread on a Task.** `TcpClient.ConnectAsync(..).Wait(ms)` on the game thread never
   finished. A plain blocking `Connect(IPAddress.Loopback, port)` is immediate on loopback.
3. **Puppet = `new RPawnPlayerBm(pos, rot)`, unpossessed.** Spawns fine in `OW`, draws as a full Batman with the
   local player's suit, idle pose. `SetCollision(false,false,true)` + `bCollideWorld=false` keep it out of the way.
   Its own code sets `PHYS_Falling` again after `SetPhysics(PHYS_None)`, so the mod re-applies PHYS_None every frame.
4. A remote player whose `PlayerInfo.World` differs from ours is not drawn (by design). The first bot reported
   `batentry` while the game's map name is `OW`, so nothing spawned; the bot now adopts the host's world.
5. `exec shot` writes `Documents\WB Games\Batman Arkham City GOTY\BmGame\ScreenShots\ScreenShotNNNNN.bmp`.
6. `RHUD.PostRender` redirect + `Canvas.DrawText` works for the overlay; `Canvas.Project` works for name tags.
7. Map name of the open world from `WorldInfo.GetMapName(false)` is `OW` (not `batentry`).

## Next (0.1 notes)
See the end of the file for the running status.

## Status at 0.1.0 (2026-10-26)
- Compile check: 0 errors. Offline lab: 14/14 + animcheck PASS (p95 8 uu at 90+-35 ms lag).
- In-game lab run 5: 12/12 PASS. Real game hosts (direct), bot joins, puppet spawns as RPawnPlayerBm with
  PHYS_None, moves 500 uu wide path, is removed on leave; bot receives 1200 states from the game; animcheck both
  directions: 2993 samples, p95 position 1.6 uu, facing 0.37 deg, 0 glitches.
- Steam smoke (one account): Spacewar init + friends-only lobby + leave PASS. Two-account Steam play: untested.
- Not synced: remote animation (idle pose), enemies, story, collectibles, gadgets. Next: drive the puppet's
  animation from velocity/physics (RPawnPlayerAnim / AnimTree nodes), then host-authoritative enemy state.

## 0.2.0: world, story, missions, NPCs, no pause, cutscene vote (2026-10-26)
Design from coop-anything (skill + MCP: playbook effect-capture-story-sync, actor-ownership, how-coop-mods-work,
fake-world-testing; tools mutants/gameslot/animcheck). The MCP was registered mid-session, so it was driven over
stdio with the same server; its tools answered (list_docs, get_skill, read_playbook, search_directory, detect_engine).

Findings in the real game (all reproduced at least twice):
1. **`RFlagManager.FindSetFlagsLike(name, out TArray<FString>)` crashes the game silently** (no dump). Never call
   functions with out TArray parameters through BmSDK 0.21 (`SetFlagList` has the same shape and was not hooked).
2. **Story flags are set natively at load** (the start URL's `Flags=`), so no redirect sees them. Flag names come from
   loaded Kismet objects instead: `RSeqVar_GlobalFlag.FlagName/FlagNameAsString`, `RSeqAct_SwitchFlags.FlagList[].FlagName`,
   `BmScript.RSeqAct_SetMapFlag.GlobalFlagName`. 238 names in the open world; polling all with `GetGlobalFlag` takes ~1.8 ms.
3. `RPersistentData.FlagNames` is empty on a fresh temp-slot start (it is the save's list).
4. Redirects DO fire for calls made from C# (both TakeDamage hooks fired on a lab `TakeDamage`), and for script calls
   (`SetPause` from the console `pause`).
5. **No pause:** console `pause` stops the world tick and with it BmSDK's `OnTick`, so a guard in OnTick never runs.
   Redirect `PlayerController.SetPause` (refuse while in a room) works; the HUD `PostRender` redirect still runs while
   paused and is the fallback (and serves the lab bridge while paused).
6. Clearing `WorldInfo.Pauser` by hand leaves the engine's pauser list set; the next `pause` then toggles it off.
7. NPC keys from `GetPathName`, e.g. `OW_R2_Ch2.TheWorld:PersistentLevel:RPawnVillainOutcast_15`, are the same on
   every copy of the same level: a guest finds the host's NPC with `FindObject<RBMPawnAI>(key)`.
   `RCharacter.StaticCreatePawn` is the spawn call (not on RBMPawnAI). Class paths: `BmScript.RPawnVillainOutcast`.
8. `RPawnVillain` overrides `TakeDamage`: hook both `RPawn` and `RPawnVillain`.
9. **Following the host's map:** `open <url>` from inside a running game closed it; `start <url>` (BmSDK demo path)
   reloads fine outside a room. Inside a room it crashed until the mod stopped touching actors in the frame of the
   travel: the load happens inside the ConsoleCommand call, and the puppet update right after spawned into a dying
   world. Now: compare the WorldInfo pointer every frame and forget (never call into) old actors; skip the rest of the
   frame once travel starts; guest re-requests a snapshot after any world change.
10. Guest digest must hash only keys the host tracks (plus its own set flags), or a host that tracks fewer keys
    repairs the guest every 5 s for nothing.
11. Merge rules for a guest's own (native) progress: flags OR, chapters max; a guest losing state is repaired, never sent.

Two copies of the game do not fit in memory on this PC (one copy 1.4 GB, 1.5 GB free with it running), so the in-game
sync lab plays one real copy against a bot in each role (lab/ingame_sync.py): 17/17 PASS. Real two-game sessions: untested.

## 0.3.0: animation sync, PVP, chat, host menu, Steam names (2026-10-29)
Asked for: animation sync, pause keeps the world and audio running, Steam names instead of "Player", a host menu
with a PVP toggle (thug-like fights with counters and damage), a chat on T with mission/defeat/death lines and a
rebindable key, test everything, 0.3.0.

Findings in the real game (one copy unless noted):
1. **Two copies on one PC still do not run**, now for a different reason: memory is fine (15.7 GB, 7.4 GB free
   with one copy), and the game's single-instance mutex (`\Sessions\1\BaseNamedObjects\6c6a0267-cdb5-4cf2-ae78-7f29002f212d`)
   can be released from inside the first copy (lab `release`, own process only). The second copy still exits with
   code 0 before BmSDK loads: the Steam wrapper refuses a second running instance. Not pursued further (it would mean
   touching DRM). Two-player tests stay one real game + a bot; a real two-PC test is still owed.
2. **Steam name bug:** the link fell back to "Player" when `Name=` was empty and never used the persona name it read.
   Now the link starts Steam (as Spacewar) for the name on any transport and reports it back (`LinkToMod.Identity`).
3. **Pause:** in a room the pause menu opens and the world keeps running (TimeSeconds advances); `SetPause` is refused
   so the pause post-process and the pause path never start. No menu audio hook fires (`RGFxMovie.SetGlobalAudioState`,
   `SetAudioState`, `GameMovieSetPauseState`, `PostAudioEvent`, `StartFlashAudioEvent`, `SetAudioFade` all redirected
   and counted: 0 calls). Audio not measurable here: Windows reports the game's session silent even unpaused while
   the window is unfocused (lab/AudioMeter.ps1). **Unverified by ear.**
4. **Crash found and fixed:** `WorldInfo.Pauser = null` by hand (0.2's fallback in RoomTick/PostRender) leaves the
   engine's pauser list set; the next `SetPause(false)` (closing the pause menu) crashes the game (SEH in BmSDK's
   loader). Calling `ClosePauseMovie`/`SetPause(false)` from inside the HUD render also crashed. 0.3 never clears the
   pauser by hand; the lab bridge serves only reads while paused.
5. **Animation:** the player AnimTree is one `RAnimNode_Pose` driven by a pose description (`RAnimNode_Pose.FPoseDescription`:
   movement/weapon/idle stance, transition type/name/target/clip) and a move-to request. **All pose functions are
   native: redirects never fire on them, not even for calls from C#; calling them from C# works.** `Tweak_Anim_*`
   (editor override) does nothing in the shipping game.
   - Locomotion: a puppet with `PHYS_Walking`, actor collision on (the city's floors are static-mesh actors; without
     it the puppet falls through roofs), `SetMovementSpeed(MS_*)` and `MoveTo(pos)` plays the game's real run/walk cycle.
   - Actions: each strike/counter/hit reaction raises `Anim.PosePlayer.LastTransitionId`; the clip is
     `Pose.CustomTransition.CustomFullBodyAnim` (e.g. `Anim_Batman_Combat.Code.BM_Attack:AnimSequence_19`). The owner
     sends type, stances, name, clip path, target, yaw (degrees; the call wants 65536 units per turn), meeting point;
     the puppet calls `StartRelativeTransition*`/`ChangePose` with the same. Verified: the puppet plays the same
     clips in order ~0.1 s later (strike 174, hit reaction 16).
   - The puppet's pending `MoveTo` cut replayed strikes off after ~0.2 s: actions now stop the walk and hold for the
     clip's `SequenceLength`.
   - `SetLocation` on the walking puppet is refused when the spot touches an actor/geometry: refused corrections are
     retried with collision off for that move.
   - Tracking on open roof floor vs the interpolated target: xy p50 18 uu, p95 ~130 uu with spikes where the bot's
     flat made-up circle crosses raised geometry (|z| p95 96). `PuppetMode=kinematic` keeps 0.2's exact snapping.
6. **Combat (the basis of PVP):**
   - `RCharacter.StaticCreatePawn(pos, rot, null, RPawnVillainThug class, character class, ...)` makes a thug with its
     AI (`RBMAIController`, `RBMBehaviour_CombatAI`). `EnterCombat(true,true)`, then `bCanAttack = true` on the
     behaviour (the combat manager hands out attack tokens) and `AttackTargetPawnWithClass(RCombatMove_VillainCloseAttack)`
     attacks the player on demand: counter icon, real counter window, real damage (10 per hit at normal difficulty).
   - `RPlayerControllerCombat.TriggerCounter(true)` during the icon counters it (`RCombatMove_BatmanCounter`).
     `TryStrike(0)` strikes (freeflow picks the target natively; it does pick a hidden thug).
   - Damage both ways goes through the `TakeDamage` redirects (`RPawn`, `RPawnVillain`).
   - **A thug that is not drawn never lands its strike** (HiddenGame, shrunk to 0.001, owner-no-see all fail;
     `LastRenderTime` forced every frame does not help either): the attack shows its icon and gives up after ~0.6 s.
     So the proxy's hit is applied by BatCoop: the attack's own `RCombatMove_VillainCloseAttack.DamagePawn(me)` while it
     runs (after 1.15 s), else a thug-like `TakeDamage` when the game gives the attack up. Counter window ~0.6 s.
   - A countered thug stays knocked down even with full health; its next attacks show the icon but cannot be
     countered. Proxies are replaced with a fresh one after a counter, a ragdoll, or 1.5 s in a non-attack move.
   - `SetLocation` on the proxy is refused when it overlaps the puppet or us: proxies collide with no actor, so they
     stay pinned inside the puppet (max 1 uu over 290 samples, during attacks too), which keeps the counter icon over
     the other player (the user saw it off to one side before: the attack lunge carried the proxy away).
   - Calling the native `FindStrikeTarget` with a null damage class crashed the game. Never pass null classes to
     native combat functions.
   - Proxies are excluded from NPC sync (`NpcHostPort.IsLive`), never bark, never throw bricks, never attack on their
     own (`CanStartAttacking` redirected), and take no damage (strikes on them are the attacker's request only).
7. **Input:** BmSDK's `OnKeyDown` cannot consume keys. `_Console.InputKey`/`InputChar` redirects see every key before
   the player controller and can swallow it: the chat box and the host menu use them (W while typing does not walk;
   T is taken in a room). The game's own binding of T is `TriggerSecondaryGroupCombatMove` (`BmInput.ini`, with a
   `:META:COM_SECONDARY_GROUP_COMBAT` entry for the Controls menu). The Controls menu is a fixed Flash screen; BatCoop's
   keys are rebound in the host menu instead (saved to BatCoop.ini).
8. Mission lines come from the host's committed story diffs (`WorldSync.StepCommitted`): a new primary objective, a
   main chapter going up, a side-mission chapter going up; credited to the guest whose intent the host applied in
   the 3 s before, else the host. The first commit of a room is the baseline and makes no line.

Tests at 0.3.0:
- Offline: 41/41 (12 new: identity, host_settings, host_settings_late_join, chat_feed, event_feed, pvp_roundtrip,
  pvp_defeated_down, pvp_off, anim_calls, anim_refresh_stance_only, mission_feed, host_only_skip), animcheck PASS,
  mutation twins 4/4.
- In-game `ingame.py` 12/12 (kinematic puppets, see the note in the script) and `ingame_sync.py` 17/17: everything
  0.2 synced still syncs.
- In-game PVP (`lab/pvp_check.py`, game = defender, bot = attacker every 6 s, hidden proxies): PASS: 10 forced
  attacks, 6/6 uncountered hits landed (10 damage), 4/4 lab counters (0.25 s after the icon) countered, 0 misses,
  proxy pinned within 1 uu; a defeat showed "BotFriend defeated LabHost" on both sides and health came back.
  Attacker side: 6 strikes at the bot's proxy were sent as attacks; both counters the bot answered hit Batman.
- In-game UI through the console hooks: T opens the chat, the typed line reached the bot; F3 menu draws, Esc closes;
  chat key rebound to Y (saved, Y opens the chat) and back; a menu change turned PVP off.
- Not tested: two real players (two PCs), Steam P2P between two accounts, real keyboard/mouse input (the lab calls
  the same functions), PVP between two humans, audio by ear, gliding/grappling animation (not synced).

## 0.3.1: hotfix, the game kept opening at a tiny resolution (2026-10-29)
Asked for: stop the game resizing itself to the lowest resolution; fix other bugs found on the way; 0.3.1.

Findings in the real game:
1. The player's `Documents\WB Games\Batman Arkham City GOTY\BmGame\Config\BmEngine.ini` `[SystemSettings]` said
   `Fullscreen=False ResX=140 ResY=77`; `UserEngine.ini` (the launcher's choice) said 1600x900 fullscreen. The game uses
   BmEngine.ini, so every start opened a 140x77 window.
2. Mechanism, reproduced: **the game saves its window's client size as the resolution** (with `Fullscreen=False`) when
   the window is resized, and writes the settings back on a normal quit. A lab copy shrunk to 154x91 then `exec exit`
   left `Fullscreen=False ResX=154 ResY=91`. Minimizing (client 0x0) and a `-windowed -ResX` start do not change the
   saved values; `taskkill /F` writes nothing. The 140x77 most likely came from a lab copy (lab copies run windowed and
   some were closed with `exec exit`); nothing in the mod changes the resolution.
3. `setres` is not a command in the shipping game ("Command not recognized"), and **`scale set ResX 800` crashed the
   game**: never use `scale set` through `exec`.

Fix:
- `Core/DisplaySettings.cs` (shared with the link): read/write Fullscreen/ResX/ResY in `[SystemSettings]` only, keeping
  CRLF and every other line; the repair target is the launcher's resolution if usable (>= 640x360), else the
  desktop's in fullscreen.
- `Game/DisplayGuard.cs`: at load (skipped when the command line has `-windowed`/`-ResX`, i.e. lab copies, or with
  `DisplayGuard=0`), a saved size below 640x360 is rewritten in the file, the game window (when windowed) is resized
  to the repaired size (the game then takes it as its resolution), and `BatCoopLink.exe --fix-display <pid> <ini> WxH
  fullscreen|windowed` waits for the game to quit and writes the repaired resolution again (the game's own save on
  quit would otherwise put the windowed size back).
- Lab: `lab/display_keep.py`; `launch.py` and `ingame.py` save the player's three values before a run and restore
  them when no copy runs any more.
- The player's file was put back to 1600x900 fullscreen.

Tests at 0.3.1:
- Offline 42/42 (new `display_repair`), animcheck PASS, mutation twins 4/4.
- Real game, started like a player (no `-windowed`) with 154x91 windowed saved: the log said "the game started at
  154x91 windowed; restoring 1600x900 fullscreen", the window's client area became 1600x900, the helper ran, and after
  `exec exit` the file said `Fullscreen=True ResX=1600 ResY=900`.
- `ingame.py` 12/12; the player's resolution was unchanged after that windowed lab run.
- Not tested: a start that is already fullscreen at a tiny size (the window is left alone there; the toast asks for
  a restart and the fix is written after quitting).

## Handoff (start here in a new session)
- Repo: `multiplayereverything/batman-arkham-city-gofy-coop`, branch `main`, version 0.3.1, protocol `batcoop-3`.
- Game: `F:\SteamLibrary\steamapps\common\Batman Arkham City GOTY`, BmSDK 0.21, dgVoodoo2 installed.
- Build/deploy: `python -I lab/build.py all`, `python -I lab/deploy.py install` (hot reload; a reload ends the
  co-op room, so re-host). One test copy: `python -I lab/launch.py start host` / `stop`.
- Bot friend: `sh lab/botfriend.sh <s> <radius> <speed> <log> [x,y,z] [--stand-after 1] [--pvp-attack-every 6]`
  (stop bot processes before rebuilding the offline lab: they lock its files).
- Lab bridge research commands: `insp/methods/call/set/find` (reflection on live objects; `obj:<path>|Prop`),
  `anim`, `animloop`, `pvp`, `pvpshow`, `settings`, `combat watch|counter [delay]|log`, `aithug`, `chatlog`, `ui`,
  `view`, `hold`, `handles`/`release`.
- Lab copies run windowed; `launch.py`/`ingame.py` keep the player's resolution (`lab/display_keep.py show`).
  Never run `scale set` through `exec` (crash).
- Open items: real two-player test (user + friend); audio while paused by ear; glide/grapple animation; animated
  puppet spikes on sharp turns; takedown prompt near PVP proxies; longer counter window (make the proxy's attack
  survive while hidden, then let it land natively).
