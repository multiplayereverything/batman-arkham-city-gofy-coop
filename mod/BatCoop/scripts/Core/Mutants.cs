#nullable enable
// Mutation twins (coop-anything tools/mutants pattern): BATCOOP_MUT=<name> turns one protection off so the lab can
// prove its test fails without it. Never set outside the lab.
using System;

namespace BatCoop.Core;

public static class Mutants
{
    static readonly string Active = Environment.GetEnvironmentVariable("BATCOOP_MUT") ?? "";

    /// <summary>guest_local_write: guests change shared state themselves instead of asking the host.
    /// no_epoch: NPC hits from an old epoch are accepted. skip_twice: a repeated skip message skips again.
    /// no_digest: guests never send digests, so drift is never repaired.</summary>
    public static bool On(string name) => Active.Length > 0 && string.Equals(Active, name, StringComparison.Ordinal);
}
