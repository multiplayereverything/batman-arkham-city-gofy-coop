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

## Next
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
