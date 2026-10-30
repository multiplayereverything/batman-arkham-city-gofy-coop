#nullable enable
// Reads the local player from the game for the session. Everything here runs on the game thread.
using System.Diagnostics;
using BatCoop.Core;
using BmSDK.BmGame;
using BmSDK.BmScript;
using BmSDK.Engine;

namespace BatCoop.Game;

public sealed class LocalWorld : ICoopWorld
{
    static readonly Stopwatch Clock = Stopwatch.StartNew();

    public string PlayerName = "Batman";
    PlayerInfo _info = new("Batman", CharacterId.Unknown, "", "");

    public double Now => Clock.Elapsed.TotalSeconds;
    public PlayerInfo LocalInfo => _info;
    public string WorldName { get; private set; } = "";

    /// <summary>The local pawn, or null in menus, loading screens and between levels.</summary>
    public static RPawnPlayer? TryGetPawn()
    {
        try
        {
            var p = BmSDK.Framework.Game.GetPlayerPawn();
            return p is { IsValid: true } && !p.bDeleteMe ? p : null;
        }
        catch
        {
            return null;
        }
    }

    public static CharacterId CharacterOf(Pawn p) => p switch
    {
        RPawnPlayerBatmanWithSword => CharacterId.BatmanWithSword,
        RPawnPlayerCatwoman => CharacterId.Catwoman,
        RPawnPlayerRobin => CharacterId.Robin,
        RPawnPlayerNightwing => CharacterId.Nightwing,
        RPawnPlayerBruceWayne => CharacterId.BruceWayne,
        RPawnPlayerBm => CharacterId.Batman,
        _ => CharacterId.Unknown,
    };

    /// <summary>Call once per tick before the session reads anything.</summary>
    public void Refresh()
    {
        var pawn = TryGetPawn();
        string world = "";
        try { world = BmSDK.Framework.Game.GetWorldInfo().GetMapName(false).ToString() ?? ""; } catch { }
        WorldName = world;
        var ch = pawn != null ? CharacterOf(pawn) : CharacterId.Unknown;
        var cls = pawn?.Class.Name.ToString() ?? "";
        if (_info.Name != PlayerName || _info.Character != ch || _info.PawnClass != cls || _info.World != world)
            _info = new PlayerInfo(PlayerName, ch, cls, world);
    }

    public bool TryReadLocal(out PlayerState s)
    {
        s = default;
        var pawn = TryGetPawn();
        if (pawn == null) return false;

        var rot = pawn.Rotation;
        var flags = PlayerFlags.InWorld;
        if (pawn.bIsCrouched) flags |= PlayerFlags.Crouched;
        if (pawn.Health <= 0) flags |= PlayerFlags.Dead;
        if (pawn.bHidden) flags |= PlayerFlags.Hidden;
        if (pawn.bGliding) flags |= PlayerFlags.Gliding;

        s.Pos = pawn.Location;
        s.Yaw = SnapshotBuffer.Normalize(rot.Yaw);
        s.Pitch = SnapshotBuffer.Normalize(rot.Pitch);
        s.Vel = pawn.Velocity;
        s.Physics = (byte)pawn.Physics;
        s.Flags = flags;
        s.Health = (ushort)Math.Clamp(pawn.Health, 0, ushort.MaxValue);
        s.HealthMax = (ushort)Math.Clamp(pawn.HealthMax, 0, ushort.MaxValue);
        s.MoveSpeed = (byte)pawn.MovementSpeed;
        return true;
    }
}
