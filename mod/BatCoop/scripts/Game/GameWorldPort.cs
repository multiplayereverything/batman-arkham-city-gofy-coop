#nullable enable
// Arkham City's shared story state for WorldSync: global flags (RFlagManager), object flags seen through the
// SetObjectFlag hook, chapters and side-mission chapters, objectives (RPersistentData) and the current map.
using BatCoop.Core;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace BatCoop.Game;

public sealed class GameWorldPort : IWorldPort
{
    readonly Action<string> _log;
    /// <summary>Object flags this machine has seen set or cleared (the engine has no list to read them back from).</summary>
    public readonly Dictionary<string, bool> ObjectFlags = [];
    readonly Dictionary<string, bool> _objectFlagsPending = [];
    string _hostUrl = "";
    public string? TravelTo;          // set when the host is in another map; the script travels on the next tick
    public long Applied;

    public GameWorldPort(Action<string> log) => _log = log;

    static RFlagManager? FlagManager()
    {
        try { return BmSDK.Framework.Game.GetPlayerController().GetFlagManager(); } catch { return null; }
    }

    static RPersistentData? Persistent()
    {
        try { return BmSDK.Framework.Game.GetPersistentData(); } catch { return null; }
    }

    public static string MapName()
    {
        try { return BmSDK.Framework.Game.GetWorldInfo().GetMapName(false).ToString() ?? ""; } catch { return ""; }
    }

    public static string LocalUrl()
    {
        try { return BmSDK.Framework.Game.GetWorldInfo().GetLocalURL().ToString() ?? ""; } catch { return ""; }
    }

    /// <summary>Global flags seen through the SetGlobalFlag hook since this world loaded (true = set, false = cleared).
    /// The engine's own "list set flags" call crashed the game, so flags are read as: the save's list plus these.</summary>
    public readonly Dictionary<string, bool> GlobalFlags = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The flags stored with the loaded save (RPersistentData.FlagNames).</summary>
    public static List<string> SavedFlagNames()
    {
        var list = new List<string>();
        var pd = Persistent();
        if (pd == null) return list;
        var arr = pd.FlagNames;
        if (arr == null) return list;
        for (int i = 0; i < arr.Count; i++) list.Add(arr[i].ToString() ?? "");
        return list;
    }

    /// <summary>Every global flag name the loaded Kismet scripts mention. The game sets most flags natively, out of
    /// reach of script hooks, so the host polls these names with GetGlobalFlag (which is safe to call).</summary>
    public readonly HashSet<string> Catalog = new(StringComparer.OrdinalIgnoreCase);
    public double LastReadMs;

    /// <summary>Add flag names from loaded Kismet objects (call on world load and now and then while areas stream in).</summary>
    public int ScanCatalog()
    {
        int before = Catalog.Count;
        void Add(string? n) { if (!string.IsNullOrWhiteSpace(n) && n != "None") Catalog.Add(n.Trim()); }
        foreach (var v in BmSDK.Framework.Game.FindObjects<RSeqVar_GlobalFlag>())
        {
            if (!v.IsValid || v.Name.ToString()!.StartsWith("Default__")) continue;
            Add(v.FlagNameAsString.ToString());
            Add(v.FlagName.ToString());
        }
        foreach (var a in BmSDK.Framework.Game.FindObjects<RSeqAct_SwitchFlags>())
        {
            if (!a.IsValid || a.Name.ToString()!.StartsWith("Default__")) continue;
            var list = a.FlagList;
            if (list == null) continue;
            for (int i = 0; i < list.Count; i++) Add(list[i].FlagName.ToString());
        }
        foreach (var a in BmSDK.Framework.Game.FindObjects<BmSDK.BmScript.RSeqAct_SetMapFlag>())
        {
            if (!a.IsValid || a.Name.ToString()!.StartsWith("Default__")) continue;
            Add(a.GlobalFlagName.ToString());
        }
        foreach (var n in SavedFlagNames()) Add(n);
        return Catalog.Count - before;
    }

    public List<string> SetGlobalFlags()
    {
        var fm = FlagManager();
        var set = new List<string>();
        if (fm == null) return set;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var n in Catalog)
            if (fm.GetGlobalFlag(n)) set.Add(n);
        foreach (var (n, v) in GlobalFlags)
            if (v && !Catalog.Contains(n) && fm.GetGlobalFlag(n)) set.Add(n);
        LastReadMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        return set;
    }

    /// <summary>A new world was loaded: what we tracked belonged to the old one.</summary>
    public void OnWorldLoaded()
    {
        GlobalFlags.Clear(); ObjectFlags.Clear(); _objectFlagsPending.Clear();
        int added = ScanCatalog();
        _log($"flag catalog: {Catalog.Count} names ({added} new)");
    }

    public WorldState? Read()
    {
        if (LocalWorld.TryGetPawn() == null) return null; // menus, loading, between levels
        var fm = FlagManager();
        var pd = Persistent();
        if (fm == null || pd == null) return null;

        var s = new WorldState();
        foreach (var name in SetGlobalFlags())
            if (name.Length > 0) s.Set(WorldEffect.Flag("g:" + name, true));
        foreach (var (key, v) in ObjectFlags)
            if (v) s.Set(WorldEffect.Flag("o:" + key, true));

        s.Set(WorldEffect.Number("ch:main", pd.CurrentChapter));
        s.Set(WorldEffect.Number("ch:sub", pd.CurrentSubChapter));
        for (int i = 0; i < 26; i++)
        {
            s.Set(WorldEffect.Number($"ch:side:{i}", pd.CurrentSideChapter[i]));
            s.Set(WorldEffect.Number($"ch:sidesub:{i}", pd.CurrentSideSubChapter[i]));
        }
        s.Set(WorldEffect.Text("obj:primary", pd.PrimaryObjective.ToString() ?? ""));
        s.Set(WorldEffect.Text("obj:side", pd.SideObjective.ToString() ?? ""));
        s.Set(WorldEffect.Text("lvl:map", MapName()));
        // The URL is the host's to give: on a guest report the last one received, so it never reads as drift.
        s.Set(WorldEffect.Text("lvl:url", BatCoopScript.Instance?.IsHostNow == true ? LocalUrl() : _hostUrl));
        return s;
    }

    public void Apply(in WorldEffect e)
    {
        Applied++;
        var key = e.Key;
        if (key.StartsWith("g:"))
        {
            // A call from C# goes straight to the engine (redirects only see the game's own calls), so record it here.
            FlagManager()?.SetGlobalFlag(key[2..], e.Int != 0);
            GlobalFlags[key[2..]] = e.Int != 0;
            Catalog.Add(key[2..]);
        }
        else if (key.StartsWith("o:"))
        {
            ObjectFlags[key[2..]] = e.Int != 0;
            if (!ApplyObjectFlag(key[2..], e.Int != 0)) _objectFlagsPending[key[2..]] = e.Int != 0;
        }
        else if (key.StartsWith("ch:"))
        {
            var pd = Persistent();
            if (pd == null) return;
            byte v = (byte)Math.Clamp(e.Int, 0, 255);
            if (key == "ch:main") pd.CurrentChapter = v;
            else if (key == "ch:sub") pd.CurrentSubChapter = v;
            else if (key.StartsWith("ch:sidesub:")) pd.CurrentSideSubChapter[int.Parse(key[11..])] = v;
            else if (key.StartsWith("ch:side:")) pd.CurrentSideChapter[int.Parse(key[8..])] = v;
        }
        else if (key == "obj:primary") { var pd = Persistent(); if (pd != null) pd.PrimaryObjective = e.Str; }
        else if (key == "obj:side") { var pd = Persistent(); if (pd != null) pd.SideObjective = e.Str; }
        else if (key == "lvl:url") _hostUrl = e.Str;
        else if (key == "cmd:setflags") FlagManager()?.SetGlobalFlags(e.Str); // a guest's SetGlobalFlags call, run on the host
        else if (key == "lvl:map")
        {
            var here = MapName();
            if (e.Str.Length > 0 && here.Length > 0 && !string.Equals(here, e.Str, StringComparison.OrdinalIgnoreCase))
                TravelTo = e.Str;
        }
    }

    /// <summary>The host's URL for following it into its map (falls back to the bare map name).</summary>
    public string HostUrl(string map) => _hostUrl.Length > 0 ? _hostUrl : map;

    /// <summary>Object flags name an object by its path; it may only exist once its level has streamed in.</summary>
    static bool ApplyObjectFlag(string key, bool v)
    {
        int bar = key.LastIndexOf('|');
        if (bar <= 0) return true;
        var obj = BmSDK.Framework.Game.FindObject<Actor>(key[..bar]);
        if (obj == null) return false;
        FlagManager()?.SetObjectFlag(obj, key[(bar + 1)..], v);
        return true;
    }

    /// <summary>Retry object flags whose objects had not loaded yet (call about once a second).</summary>
    public void RetryPending()
    {
        foreach (var (k, v) in _objectFlagsPending.ToList())
            if (ApplyObjectFlag(k, v)) _objectFlagsPending.Remove(k);
    }

    public int PendingObjectFlags => _objectFlagsPending.Count;
}
