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

## Next
See the end of the file for the running status.
