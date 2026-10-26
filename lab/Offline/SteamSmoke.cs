using BatCoop.Core;

namespace BatCoop.Lab;

/// <summary>What one Steam account can prove on one PC: the link starts Steam as Spacewar (480), creates a
/// friends-only lobby tagged with our protocol, reports its id, and leaves it again.</summary>
public static class SteamSmoke
{
    public static int Run(string linkExe, string outDir)
    {
        Directory.CreateDirectory(outDir);
        using var bot = new Bot("SteamSmoke", 38990, new FakeWorld("SteamSmoke", FakeWorld.Still(default)), linkExe, outDir);
        bot.Session.Host(TransportKind.Steam, 4, 0);
        var end = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < end && bot.Session.State is LinkState.Idle or LinkState.Connecting)
        {
            bot.Tick();
            Thread.Sleep(20);
        }
        bool ok = bot.Session.State == LinkState.Hosting && ulong.TryParse(bot.Session.Lobby, out var lobby) && lobby > 0;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} steam_lobby: state {bot.Session.State}, '{bot.Session.StatusText}', lobby {bot.Session.Lobby}");
        bot.Session.Leave();
        end = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < end && bot.Session.State != LinkState.Idle) { bot.Tick(); Thread.Sleep(20); }
        Console.WriteLine($"{(bot.Session.State == LinkState.Idle ? "PASS" : "FAIL")} steam_leave: state {bot.Session.State}");
        Console.WriteLine($"RESULT: {(ok && bot.Session.State == LinkState.Idle ? "PASS" : "FAIL")}");
        return ok ? 0 : 1;
    }
}
