using System.Numerics;
using BatCoop.Core;

namespace BatCoop.Lab;

/// <summary>Fake story store (coop-anything fakeworld pattern): flags, chapters, objectives and the current map,
/// with the same gate the game layer has: a guest's own script writes become intents.</summary>
public sealed class FakeStory : IWorldPort
{
    public readonly Dictionary<string, WorldEffect> Values = new();
    public CoopSession? Session;
    public int Applies;
    public int Travels;
    public bool InWorld = true;

    public WorldState? Read()
    {
        if (!InWorld) return null;
        var s = new WorldState();
        foreach (var v in Values.Values)
            if (v.Kind != WorldValueKind.Flag || v.Int != 0) s.Set(v);
        return s;
    }

    public void Apply(in WorldEffect e)
    {
        Applies++;
        if (e.Key == "lvl:map" && (!Values.TryGetValue(e.Key, out var old) || old.Str != e.Str)) Travels++;
        Values[e.Key] = e;
    }

    /// <summary>What a story script does: on the host it changes the world, on a guest it asks the host.</summary>
    public void ScriptSets(WorldEffect e)
    {
        if (Session is { InRoom: true, IsHost: false } s && !s.World.Applying && !Mutants.On("guest_local_write")) s.World.GuestIntent(e);
        else Values[e.Key] = e;
    }

    /// <summary>A change that bypasses every hook (a native write the mod cannot see): only the digest finds it.</summary>
    public void Corrupt(WorldEffect e) => Values[e.Key] = e;
}

/// <summary>Fake NPC population on the host: walkers on circles, some far away.</summary>
public sealed class FakeNpcs : INpcHostPort
{
    public sealed class Npc
    {
        public long Handle;
        public string Key = "";
        public Vector3 Center;
        public float Radius, Speed;
        public int Health = 100;
        public string PawnClass = "RPawnVillainThug", CharacterClass = "RCharacter_JokerThug1";
        public List<NpcHit> Hits = [];
    }

    public readonly List<Npc> All = [];
    public double Now => FakeWorld.Clock.Elapsed.TotalSeconds;

    public Npc Add(string key, Vector3 center, float radius = 300, float speed = 250)
    {
        var n = new Npc { Handle = 0x1000 + All.Count * 16, Key = key, Center = center, Radius = radius, Speed = speed };
        All.Add(n);
        return n;
    }

    public Vector3 PosOf(Npc n, double t)
    {
        double a = t * n.Speed / n.Radius;
        return n.Center + new Vector3((float)Math.Cos(a) * n.Radius, (float)Math.Sin(a) * n.Radius, 0);
    }

    public IEnumerable<NpcSample> Sample()
    {
        double t = Now;
        foreach (var n in All)
        {
            var st = new NpcState { Pos = PosOf(n, t), Yaw = 0, Health = (ushort)Math.Max(0, n.Health),
                                    Flags = n.Health <= 0 ? NpcFlags.Down : NpcFlags.None, Physics = 1 };
            yield return new NpcSample(n.Handle, n.Key, n.PawnClass, n.CharacterClass, st);
        }
    }

    public void ApplyHit(long handle, in NpcHit hit)
    {
        var n = All.First(x => x.Handle == handle);
        n.Hits.Add(hit);
        n.Health -= hit.Damage;
    }
}
