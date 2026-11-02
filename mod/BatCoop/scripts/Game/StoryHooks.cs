#nullable enable
// The host owns the story's places:
// - a guest in a co-op room cannot start a game from the main menu (New Game, Continue, Catwoman episodes, story
//   DLC, challenge maps): it joins the host's game by itself as soon as the host has one;
// - in-map story changes (RGameRI.CommitMapChange: Catwoman's opening handing over to Bruce Wayne, and the like)
//   happen on the host; a guest's own game asking for one is refused and the guest makes the host's instead.
//   0.4.0 let each game move on by itself, and a guest ended up as Batman inside a host's Catwoman level.
using BatCoop.Core;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.BmScript;
using BmSDK.Engine;
using Items = BmSDK.BmScript.RGFxMovieUI_MainMenu.MainMenuItems;

namespace BatCoop.Game;

static class StoryHooks
{
    static BatCoopScript? S => BatCoopScript.Instance;

    static void Count(string hook, string detail)
    {
        SyncHooks.Calls[hook] = SyncHooks.Calls.GetValueOrDefault(hook) + 1;
        if (SyncHooks.Calls[hook] <= 40) S?.HookLog($"{hook}: {detail}");
    }

    /// <summary>Main-menu entries that open a game world of their own.</summary>
    static readonly HashSet<Items> StartsAGame =
    [
        Items.MMI_NewGame, Items.MMI_Continue, Items.MMI_NewGamePlus, Items.MMI_ContinuePlus, Items.MMI_Riddler,
        Items.MMI_CatwomanEPs, Items.MMI_StoryDLC1New, Items.MMI_StoryDLC1Continue, Items.MMI_StoryDLCMenu, Items.MMI_VSlice,
    ];

    /// <summary>True for a guest in a co-op room: only the host opens worlds.</summary>
    static bool Guest => S?.Session is { InRoom: true, IsHost: false };

    /// <summary>True when the main-menu entry would open a world of our own while we are a guest.</summary>
    internal static bool BlocksMenuItem(int id) => Guest && StartsAGame.Contains((Items)id);

    [Redirect(typeof(RGFxMovieUI_MainMenu), nameof(RGFxMovieUI_MainMenu.OnClicked))]
    static void MainMenuClicked(RGFxMovieUI_MainMenu self, int inID)
    {
        Count("MainMenu.OnClicked", ((Items)inID).ToString());
        if (BlocksMenuItem(inID))
        {
            S!.RefuseGuestStart();
            return;
        }
        self.OnClicked(inID);
    }

    [Redirect(typeof(RGameInfo), nameof(RGameInfo.Game_StartGameAt))]
    static void StartGameAt(RGameInfo self, FString at)
    {
        Count("Game_StartGameAt", at.ToString() ?? "");
        if (Guest && S!.FollowingAllowed == false) { S.RefuseGuestStart(); return; }
        self.Game_StartGameAt(at);
    }

    [Redirect(typeof(RGameInfo), nameof(RGameInfo.Game_Continue))]
    static void GameContinue(RGameInfo self)
    {
        Count("Game_Continue", "");
        if (Guest && S!.FollowingAllowed == false) { S.RefuseGuestStart(); return; }
        self.Game_Continue();
    }

    [Redirect(typeof(RGFxMovieUI), nameof(RGFxMovieUI.Game_Replay1stCatwoman))]
    static void Replay1stCatwoman(RGFxMovieUI self, FString character)
    {
        Count("Game_Replay1stCatwoman", character.ToString() ?? "");
        if (Guest) { S!.RefuseGuestStart(); return; }
        self.Game_Replay1stCatwoman(character);
    }

    // ---------- in-map story changes ----------

    [Redirect(typeof(RGameRI), nameof(RGameRI.PrepareMapChange))]
    static bool PrepareMapChange(RGameRI self, TArray<FName> levels, Actor originator)
    {
        var names = Names(levels);
        Count("PrepareMapChange", names);
        if (Guest && S!.RefuseOwnMapChange(names, "prepare")) return false;
        return self.PrepareMapChange(levels, originator);
    }

    [Redirect(typeof(RGameRI), nameof(RGameRI.CommitMapChange))]
    static void CommitMapChange(RGameRI self, TArray<FName> levels, FName entrance, bool enteredFromFront)
    {
        var names = Names(levels);
        Count("CommitMapChange", $"{names} at {entrance} front={enteredFromFront}");
        var s = S;
        if (Guest && s!.RefuseOwnMapChange(names, "commit")) return;
        self.CommitMapChange(levels, entrance, enteredFromFront);
        if (s?.Session is { InRoom: true, IsHost: true } || s?.Session is not { InRoom: true }) s?.NoteHostStage(names, entrance.ToString() ?? "");
    }

    [Redirect(typeof(RGameRI), nameof(RGameRI.StartLevel))]
    static void StartLevel(RGameRI self, FName level)
    {
        Count("StartLevel", level.ToString() ?? "");
        self.StartLevel(level);
    }

    [Redirect(typeof(RGameInfo), nameof(RGameInfo.PreCommitMapChange))]
    static void PreCommitMapChange(RGameInfo self, FString previous, FString next)
    {
        Count("PreCommitMapChange", $"{previous} -> {next}");
        self.PreCommitMapChange(previous, next);
    }

    [Redirect(typeof(PlayerController), nameof(PlayerController.PreClientTravel))]
    static void PreClientTravel(PlayerController self, FString url, Actor.ETravelType type, bool seamless)
    {
        Count("PreClientTravel", $"{url} {type} seamless={seamless}");
        self.PreClientTravel(url, type, seamless);
    }

    [Redirect(typeof(RSeqAct_FullScreenPlayer), nameof(RSeqAct_FullScreenPlayer.PlayMovie))]
    static void PlayMovie(RSeqAct_FullScreenPlayer self)
    {
        Count("FullScreenPlayer.PlayMovie", self.TheMovieName.ToString() ?? "");
        self.PlayMovie();
    }

    internal static string Names(TArray<FName>? levels)
    {
        if (levels == null) return "";
        var list = new List<string>();
        for (int i = 0; i < levels.Count; i++) list.Add(levels[i].ToString() ?? "");
        return string.Join(",", list);
    }

    internal static TArray<FName> Levels(string names)
    {
        var parts = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var arr = new TArray<FName>(parts.Length);
        foreach (var p in parts) arr.Push(new FName(p));
        return arr;
    }
}
