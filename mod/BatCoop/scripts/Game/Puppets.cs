#nullable enable
// One puppet pawn per remote player. Puppets never block anyone, never think and are destroyed when their player
// leaves, changes world or the script reloads.
//
// Animated puppets (the default) walk on the ground under the game's own pose player: each frame they are sent
// to the interpolated position with MoveTo at the owner's movement speed, so the game plays its walk, run and
// sprint cycles, turns and stops; the owner's transitions (strikes, counters, takedowns, stance changes) are
// replayed on top (AnimBridge). Whenever the owner is not walking (gliding, grappling, falling, in a vehicle or
// a cutscene) or the puppet is far off, it snaps to the position instead (kinematic), as all puppets did in 0.2.
using System.Numerics;
using BatCoop.Core;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.BmScript;
using BmSDK.Engine;
using EMovementSpeed = BmSDK.BmGame.RPoseConfig.EMovementSpeed;

namespace BatCoop.Game;

public enum PuppetKind { Player, None }
public enum PuppetMode { Animated, Kinematic }

public sealed class Puppet
{
    public required byte Id;
    public required RPawnPlayer Pawn;
    public required CharacterId Character;
    public RenderSample Last;
    public bool HasSample;
    public bool Kinematic = true;
    public double ActionUntil;
    public byte Speed = 255;
    public bool Moving;
    public double NextFace;
    public long Replayed, ReplayFailures, PullsRefused;
    public string LastReplayError = "";
}

public sealed class PuppetManager
{
    const byte PHYS_Walking = 1;
    /// <summary>Transitions wait this long, like positions (SnapshotBuffer's interpolation delay).</summary>
    public const double PoseDelay = 0.1;

    readonly Dictionary<byte, Puppet> _puppets = [];
    readonly Action<string> _log;
    readonly Dictionary<byte, double> _retryAt = [];

    public PuppetKind Kind = PuppetKind.Player;
    public PuppetMode Mode = PuppetMode.Animated;
    public string LastError = "";
    /// <summary>Lab: leave the puppets where they are (the lab drives them by hand).</summary>
    public bool LabHold;

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
            Apply(puppet, sample, now);
            ReplayPoses(puppet, rp, now);
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
        RPawnPlayer? pawn = null;
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
        _log($"puppet for player {rp.Id} '{rp.Name}': {pawn.Class.Name} at {s.Pos} ({Mode})");
        // Older transitions are stale for a new puppet; the owner resends its stance every few seconds.
        rp.Poses.Clear();
        return new Puppet { Id = rp.Id, Pawn = pawn, Character = ch };
    }

    static RPawnPlayer? SpawnPlayerPawn(CharacterId ch, Vector3 pos, Rotator rot, out string why)
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
        // Animated puppets stand on the ground: the city's floors are static-mesh actors, so the puppet must collide
        // with actors to stand on them, but it blocks nobody (players and NPCs walk through it).
        if (Mode == PuppetMode.Animated) pawn.SetCollision(true, false, false);
        else pawn.SetCollision(false, false, true);
        pawn.bCollideWorld = Mode == PuppetMode.Animated;
        pawn.bBlockActors = false;
        pawn.bCanBeDamaged = false;
        pawn.SetPhysics(Actor.EPhysics.PHYS_None, false);
    }

    void Apply(Puppet p, RenderSample s, double now)
    {
        var pawn = p.Pawn;
        if (!pawn.IsValid || LabHold) return;
        bool hidden = (s.Latest.Flags & PlayerFlags.Hidden) != 0;
        if (pawn.bHidden != hidden) pawn.SetHidden(hidden);
        p.Last = s;
        p.HasSample = true;

        var err = s.Pos - pawn.Location;
        bool ownerWalking = s.Latest.Physics == PHYS_Walking && !hidden && (s.Latest.Flags & PlayerFlags.Gliding) == 0;
        if (Mode == PuppetMode.Kinematic || !ownerWalking || err.Length() > 400 || Math.Abs(err.Z) > 150)
        {
            Snap(p, s);
            return;
        }
        if (p.Kinematic)
        {
            // Back on the ground: hand the puppet to the game's walking physics and pose player.
            pawn.SetPhysics(Actor.EPhysics.PHYS_Walking, false);
            p.Kinematic = false;
            p.Speed = 255;
            p.Moving = false;
        }

        // A replayed strike, counter or takedown moves the puppet itself; let it play.
        if (now < p.ActionUntil) return;

        var flatVel = s.Vel with { Z = 0 };
        float speed = flatVel.Length();
        if (speed < 30)
        {
            if (p.Moving) { pawn.StopMoveTo(); p.Moving = false; }
            if (now >= p.NextFace)
            {
                p.NextFace = now + 0.25;
                var dir = new Rotator(0, s.Yaw, 0).ToDirection() with { Z = 0 };
                pawn.FaceAt(pawn.Location + dir * 500);
            }
        }
        else
        {
            byte want = s.Latest.MoveSpeed < (byte)EMovementSpeed.MS_MAX ? s.Latest.MoveSpeed : SpeedFor(speed);
            if (want != p.Speed) { pawn.SetMovementSpeed((EMovementSpeed)want, 1); p.Speed = want; }
            // Aim a little ahead so the walk does not slow down at every update.
            pawn.MoveTo(s.Pos + flatVel * 0.15f);
            p.Moving = true;
        }
        // Keep the walking puppet on the owner's path. The walk cycle turns wider than a player can, so the
        // puppet is pulled onto the interpolated position every frame: a third of the error, all of it when large.
        var flatErr = err with { Z = 0 };
        float e = flatErr.Length();
        if (e > 20)
        {
            var to = pawn.Location + flatErr * (e > 150 ? 1f : 0.35f);
            if (!pawn.SetLocation(to))
            {
                // Refused because the spot touches something (the puppet collides with actors to stand on the
                // city's floors): move it without collision, then stand it on the floor again.
                p.PullsRefused++;
                pawn.SetCollision(false, false, false);
                pawn.SetLocation(to);
                pawn.SetCollision(true, false, false);
            }
        }
    }

    static byte SpeedFor(float uuPerSecond) => uuPerSecond switch
    {
        < 120 => (byte)EMovementSpeed.MS_WalkSlow,
        < 300 => (byte)EMovementSpeed.MS_Walk,
        < 650 => (byte)EMovementSpeed.MS_Run,
        _ => (byte)EMovementSpeed.MS_Sprint,
    };

    static void Snap(Puppet p, RenderSample s)
    {
        var pawn = p.Pawn;
        if (!p.Kinematic) { p.Kinematic = true; p.Moving = false; }
        // The player pawn's own code switches itself back to falling; a puppet must not fall between updates.
        if (pawn.Physics != Actor.EPhysics.PHYS_None) pawn.SetPhysics(Actor.EPhysics.PHYS_None, false);
        pawn.SetLocation(s.Pos);
        pawn.SetRotation(new Rotator(0, s.Yaw, 0));
        pawn.Velocity = s.Vel;
    }

    void ReplayPoses(Puppet p, RemotePlayer rp, double now)
    {
        while (rp.Poses.TryTake(now, PoseDelay, out var call))
        {
            if (!p.Pawn.IsValid || Mode == PuppetMode.Kinematic || LabHold) continue;
            try
            {
                bool action = call.Clip.Length > 0 || call.ClipName.Length > 0;
                // A clip is an action (a strike, a counter, a hit reaction, a takedown): the puppet's walk to its
                // position must not cut it off, so the walk stops and the action plays to its end.
                if (action && p.Moving) { p.Pawn.StopMoveTo(); p.Moving = false; }
                if (!AnimBridge.Replay(p.Pawn, call, out var why)) { p.ReplayFailures++; p.LastReplayError = why; }
                p.Replayed++;
                if (action) p.ActionUntil = now + Math.Clamp(AnimBridge.ClipSeconds(call) * (1 - call.StartTime), 0.4f, 3f);
            }
            catch (Exception e)
            {
                p.ReplayFailures++;
                p.LastReplayError = e.GetType().Name + ": " + e.Message;
            }
        }
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
