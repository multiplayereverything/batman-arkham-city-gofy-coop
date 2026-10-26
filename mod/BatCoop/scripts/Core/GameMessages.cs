#nullable enable
// Game payloads carried inside link Send/Recv frames. The link never looks inside them.
using System.Numerics;

namespace BatCoop.Core;

public enum GameMsg : byte
{
    PlayerState = 1, // unreliable, ~20 Hz
    PlayerInfo = 2,  // reliable, on join and on change
    Chat = 3,        // reliable
    Ping = 4,        // reliable, f64 sender time
    Pong = 5,        // reliable, f64 echoed time
    WorldInfo = 6,   // reliable, host -> everyone, on change and every few seconds
    World = 7,       // story and world state (WorldSync)
    Npc = 8,         // host-owned NPCs (NpcSync)
    Vote = 9,        // cutscene vote skip (CutsceneVote)
}

[System.Flags]
public enum PlayerFlags : byte
{
    None = 0,
    InWorld = 1,   // has a pawn in a game world (not in the menu or loading)
    Crouched = 2,
    Dead = 4,
    Hidden = 8,    // pawn hidden by the game (cutscene, vehicle, ...)
    Gliding = 16,
}

/// <summary>Characters a player can be. The value travels on the wire.</summary>
public enum CharacterId : byte
{
    Unknown = 0,
    Batman = 1,
    Catwoman = 2,
    Robin = 3,
    Nightwing = 4,
    BruceWayne = 5,
    BatmanWithSword = 6,
}

public struct PlayerState
{
    public uint Seq;
    public double Time;      // sender's clock, seconds
    public Vector3 Pos;      // Unreal units, Z up
    public float Yaw;        // degrees
    public float Pitch;      // degrees
    public Vector3 Vel;      // Unreal units per second
    public byte Physics;     // Actor.EPhysics of the sender's pawn
    public PlayerFlags Flags;
    public ushort Health;
    public ushort HealthMax;

    public readonly bool InWorld => (Flags & PlayerFlags.InWorld) != 0;

    public readonly byte[] Encode() => new WireWriter()
        .U8((byte)GameMsg.PlayerState)
        .U32(Seq).F32((float)Time).F32(Pos.X).F32(Pos.Y).F32(Pos.Z)
        .F32(Yaw).F32(Pitch).F32(Vel.X).F32(Vel.Y).F32(Vel.Z)
        .U8(Physics).U8((byte)Flags).U16(Health).U16(HealthMax)
        .ToArray();

    public static PlayerState Decode(ref WireReader r) => new()
    {
        Seq = r.U32(),
        Time = r.F32(),
        Pos = new Vector3(r.F32(), r.F32(), r.F32()),
        Yaw = r.F32(),
        Pitch = r.F32(),
        Vel = new Vector3(r.F32(), r.F32(), r.F32()),
        Physics = r.U8(),
        Flags = (PlayerFlags)r.U8(),
        Health = r.U16(),
        HealthMax = r.U16(),
    };
}

public sealed record PlayerInfo(string Name, CharacterId Character, string PawnClass, string World)
{
    public byte[] Encode() => new WireWriter()
        .U8((byte)GameMsg.PlayerInfo).Str(Name).U8((byte)Character).Str(PawnClass).Str(World).ToArray();

    public static PlayerInfo Decode(ref WireReader r) => new(r.Str(), (CharacterId)r.U8(), r.Str(), r.Str());
}

public sealed record WorldInfo(string World, string HostName)
{
    public byte[] Encode() => new WireWriter().U8((byte)GameMsg.WorldInfo).Str(World).Str(HostName).ToArray();
    public static WorldInfo Decode(ref WireReader r) => new(r.Str(), r.Str());
}

public static class GameMessages
{
    public static byte[] Chat(string text) => new WireWriter().U8((byte)GameMsg.Chat).Str(text).ToArray();

    // Ping time is f32 seconds since the session clock started; enough for round trips.
    public static byte[] Ping(double t) => new WireWriter().U8((byte)GameMsg.Ping).F32((float)t).ToArray();
    public static byte[] Pong(float t) => new WireWriter().U8((byte)GameMsg.Pong).F32(t).ToArray();
}
