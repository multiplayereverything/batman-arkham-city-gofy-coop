#nullable enable
// Lab only: watches the combat moves of villains near the player every frame, and can press counter the moment
// a villain's attack shows its counter icon (what a player with perfect timing does). Off unless the lab asks.
using System.Numerics;
using BmSDK.BmGame;

namespace BatCoop.Game;

sealed class LabCombat
{
    public bool Watch, AutoCounter;
    /// <summary>How long after the icon shows the lab presses counter (a person needs a moment).</summary>
    public double CounterDelay = 0.25;
    readonly Dictionary<long, double> _iconSince = [];
    public readonly List<string> Log = [];
    public long CountersPressed, CounterIcons;
    readonly Dictionary<long, string> _lastMove = [];

    public void Tick(double now)
    {
        if (!Watch && !AutoCounter) return;
        var me = LocalWorld.TryGetPawn();
        if (me == null) return;
        foreach (var v in BmSDK.Framework.Game.FindObjects<RPawnVillain>())
        {
            if (!v.IsValid || v.bDeleteMe || v.Name.ToString()!.StartsWith("Default__") || Vector3.Distance(v.Location, me.Location) > 1500) continue;
            var move = v.CombatMove;
            string name = move != null && move.IsValid ? move.Class.Name.ToString() ?? "" : "";
            bool icon = move is RCombatMove_VillainAttack a && a.IsValid && (a.bCounterIconOn || a.bCounterPromptOn);
            long key = v.Ptr.ToInt64();
            var sig = name + (icon ? "+icon" : "");
            if (!_lastMove.TryGetValue(key, out var was) || was != sig)
            {
                _lastMove[key] = sig;
                Add($"{now:0.000} {v.Name}{(PvpBridge.IsProxy(v) ? " (PVP proxy)" : "")} move={(name.Length > 0 ? name : "-")}{(icon ? " COUNTER ICON" : "")} hp={v.Health} batman_hp={me.Health} dist={Vector3.Distance(v.Location, me.Location):0}");
                if (icon) CounterIcons++;
            }
            if (icon && !_iconSince.ContainsKey(key)) _iconSince[key] = now;
            if (!icon) _iconSince.Remove(key);
            if (icon && AutoCounter && now - _iconSince[key] >= CounterDelay)
            {
                bool ok = BmSDK.Framework.Game.GetPlayerController() is RPlayerControllerCombat pc && pc.TriggerCounter(true);
                CountersPressed++;
                Add($"{now:0.000} counter pressed on {v.Name}: {ok}");
                AutoCounter = false; // one press per arm, like a player
            }
        }
    }

    void Add(string s)
    {
        Log.Add(s);
        if (Log.Count > 200) Log.RemoveRange(0, 100);
    }
}
