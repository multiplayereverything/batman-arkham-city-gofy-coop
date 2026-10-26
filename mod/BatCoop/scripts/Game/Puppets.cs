#nullable enable
// One puppet pawn per remote player, moved every frame from the snapshot buffer. Puppets never collide,
// never think and are destroyed when their player leaves, changes world or the script reloads.
using System.Numerics;
using BatCoop.Core;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.BmScript;
using BmSDK.Engine;

namespace BatCoop.Game;

public enum PuppetKind { Player, None }

public sealed class Puppet
{
    public required byte Id;
    public required Pawn Pawn;
    public required CharacterId Character;
    public RenderSample Last;
    public bool HasSample;
}

public sealed class PuppetManager
{
    readonly Dictionary<byte, Puppet> _puppets = [];
    readonly Action<string> _log;
    readonly Dictionary<byte, double> _retryAt = [];

    public PuppetKind Kind = PuppetKind.Player;
    /// <summary>Copy the remote pawn's EPhysics onto the puppet instead of PHYS_None (animation experiments).</summary>
    public bool MirrorPhysics;
    public string LastError = "";

    public PuppetManager(Action<string> log) => _log = log;

    public IReadOnlyDictionary<byte, Puppet> All => _puppets;

    public void Update(CoopSession session, string localWorld, double now)
    {
        foreach (var id in _puppets.Keys.ToList())
        {
            if (!session.Remotes.TryGetValue(id, out var rp) || !Wanted(rp, localWorld) || !_puppets[id].Pawn.IsValid)
                Destroy(id);
        }
        if (Kind == PuppetKind.None) return;

        foreach (var rp in session.Remotes.Values)
        {
            if (!Wanted(rp, localWorld) || !rp.Buffer.Sample(now, out var sample)) continue;
            if (!_puppets.TryGetValue(rp.Id, out var puppet))
            {
                if (_retryAt.TryGetValue(rp.Id, out var t) && now < t) continue;
                puppet = Spawn(rp, sample);
                if (puppet == null) { _retryAt[rp.Id] = now + 5; continue; }
                _puppets[rp.Id] = puppet;
            }
            else if (rp.Info != null && rp.Info.Character != puppet.Character && rp.Info.Character != CharacterId.Unknown)
            {
                Destroy(rp.Id); // character switch: respawn next frame with the right pawn class
                continue;
            }
            Apply(puppet, sample);
        }
    }

    static bool Wanted(RemotePlayer rp, string localWorld)
    {
        if (!rp.Buffer.HasData || !rp.Buffer.Newest.InWorld) return false;
        // Only show players that are in the same world (story map) as we are.
        return rp.Info == null || rp.Info.World.Length == 0 || localWorld.Length == 0
               || string.Equals(rp.Info.World, localWorld, StringComparison.OrdinalIgnoreCase);
    }

    Puppet? Spawn(RemotePlayer rp, RenderSample s)
    {
        var ch = rp.Info?.Character ?? CharacterId.Batman;
        var rot = new Rotator(0, s.Yaw, 0);
        Pawn? pawn = null;
        try
        {
            pawn = SpawnPlayerPawn(ch, s.Pos, rot, out var why) ?? SpawnPlayerPawn(CharacterId.Batman, s.Pos, rot, out why);
            if (pawn == null) { LastError = $"spawn failed: {why}"; _log(LastError); }
        }
        catch (Exception e)
        {
            LastError = $"spawn failed: {e.Message}";
            _log(LastError);
        }
        if (pawn == null) return null;

        Calm(pawn);
        _log($"puppet for player {rp.Id} '{rp.Name}': {pawn.Class.Name} at {s.Pos}");
        return new Puppet { Id = rp.Id, Pawn = pawn, Character = ch };
    }

    static Pawn? SpawnPlayerPawn(CharacterId ch, Vector3 pos, Rotator rot, out string why)
    {
        why = "";
        try
        {
            return ch switch
            {
                CharacterId.Catwoman => new RPawnPlayerCatwoman(pos, rot),
                CharacterId.Robin => new RPawnPlayerRobin(pos, rot),
                CharacterId.Nightwing => new RPawnPlayerNightwing(pos, rot),
                _ => new RPawnPlayerBm(pos, rot),
            };
        }
        catch (Exception e)
        {
            // Usually: the class or its package is not loaded in this world.
            why = $"{ch}: {e.GetType().Name}: {e.Message}";
            return null;
        }
    }

    void Calm(Pawn pawn)
    {
        pawn.SetCollision(false, false, true);
        pawn.bCollideWorld = false;
        pawn.bBlockActors = false;
        pawn.bCanBeDamaged = false;
        if (!MirrorPhysics) pawn.SetPhysics(Actor.EPhysics.PHYS_None, false);
    }

    void Apply(Puppet p, RenderSample s)
    {
        var pawn = p.Pawn;
        if (!pawn.IsValid) return;
        pawn.SetLocation(s.Pos);
        pawn.SetRotation(new Rotator(0, s.Yaw, 0));
        pawn.Velocity = s.Vel;
        bool hidden = (s.Latest.Flags & PlayerFlags.Hidden) != 0;
        if (pawn.bHidden != hidden) pawn.SetHidden(hidden);
        // The player pawn's own code switches itself back to falling; a puppet must not fall between updates.
        if (MirrorPhysics) { if ((byte)pawn.Physics != s.Latest.Physics) pawn.SetPhysics((Actor.EPhysics)s.Latest.Physics, false); }
        else if (pawn.Physics != Actor.EPhysics.PHYS_None) pawn.SetPhysics(Actor.EPhysics.PHYS_None, false);
        p.Last = s;
        p.HasSample = true;
    }

    public void Destroy(byte id)
    {
        if (!_puppets.Remove(id, out var p)) return;
        try { if (p.Pawn.IsValid) p.Pawn.Destroy(); } catch { }
        _log($"puppet {id} removed");
    }

    /// <summary>The world these puppets lived in is gone: drop the references without calling into them.</summary>
    public void Forget()
    {
        _puppets.Clear();
        _retryAt.Clear();
    }

    public void DestroyAll()
    {
        foreach (var id in _puppets.Keys.ToList()) Destroy(id);
        _retryAt.Clear();
    }
}
