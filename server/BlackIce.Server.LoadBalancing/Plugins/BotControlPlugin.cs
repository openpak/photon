using System.Collections.Generic;
using BlackIce.Server.Core;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing.Bots;

namespace BlackIce.Server.LoadBalancing.Plugins;

/// <summary>
/// Built-in plugin exposing runtime inspection and tuning of the playerbot fleet to the operator
/// console: count bots in a realm, spawn or despawn them in bulk, and flip the smart (world-aware)
/// behavior on or off. The bot fleet itself is always present (the host owns the <see cref="BotManager"/>);
/// this plugin only contributes the console verbs, so it carries no relay interceptors or hooks.
/// </summary>
public sealed class BotControlPlugin : IServerPlugin
{
    public string Name => "botcontrol";
    public string Description => "Operator console commands to inspect and tune the playerbot fleet: count, bulk spawn/despawn, and the smart-behavior toggle.";

    public void Configure(PluginBuilder builder)
    {
        var rooms = (RoomRegistry?)builder.Services.GetService(typeof(RoomRegistry));
        var bots = (BotManager?)builder.Services.GetService(typeof(BotManager));
        var admin = (AdminActionQueue?)builder.Services.GetService(typeof(AdminActionQueue));
        var botIds = (BotIdentityGenerator?)builder.Services.GetService(typeof(BotIdentityGenerator));
        // The bot fleet plumbing must be present for these commands to do anything; if the host didn't
        // register it, skip wiring the verbs rather than registering ones that would NRE on use.
        if (rooms is null || bots is null || admin is null || botIds is null) return;

        builder.AddCommands(new BotControlCommands(rooms, bots, admin, botIds));
    }
}

/// <summary>Console commands (Admin) to inspect and tune the playerbot fleet. Mutating verbs that relay
/// to clients are queued onto the Game listener thread via <see cref="AdminActionQueue"/> — only that
/// thread may touch per-peer transport — so they report "queued" and take effect on the next tick.</summary>
internal sealed class BotControlCommands
{
    private readonly RoomRegistry _rooms;
    private readonly BotManager _bots;
    private readonly AdminActionQueue _admin;
    private readonly BotIdentityGenerator _botIds;

    public BotControlCommands(RoomRegistry rooms, BotManager bots, AdminActionQueue admin, BotIdentityGenerator botIds)
    {
        _rooms = rooms;
        _bots = bots;
        _admin = admin;
        _botIds = botIds;
    }

    [ConsoleCommand("bots", Usage = "<realm>", MinParts = 2, MinLevel = PlayerLevel.Admin)]
    private string Bots(CommandLine line)
    {
        var typed = AfterArg(line, 0);   // realm name may contain spaces
        var realm = _rooms.ResolveName(typed);
        if (realm is null) return $"no such room: {typed}";
        int n = _bots.CountIn(realm);
        return $"{realm}: {n} bot(s)";
    }

    [ConsoleCommand("despawn", Usage = "<realm> [count|all]", MinParts = 2, MinLevel = PlayerLevel.Admin)]
    private string Despawn(CommandLine line)
    {
        // Optional trailing count|all: the realm name itself may contain spaces, so the count is only the
        // LAST token when it parses as a positive int (or the literal "all"); otherwise the whole tail is
        // the realm and we despawn all.
        var (typed, max, label) = SplitTrailingCount(AfterArg(line, 0));
        var realm = _rooms.ResolveName(typed);
        if (realm is null) return $"no such room: {typed}";
        _admin.Enqueue(() => _bots.Despawn(realm, max));
        return $"queued despawn of {label} bot(s) in \"{realm}\" (runs next tick)";
    }

    [ConsoleCommand("botspawn", Usage = "<realm> <count>", MinParts = 3, MinLevel = PlayerLevel.Admin)]
    private string BotSpawn(CommandLine line)
    {
        // <realm> ... <count>: the trailing token is the count; everything before it is the realm name.
        var tail = AfterArg(line, 0);
        int lastSpace = tail.LastIndexOf(' ');
        if (lastSpace < 0 || !int.TryParse(tail[(lastSpace + 1)..], out var count) || count < 1)
            return "usage: botspawn <realm> <count>   (count >= 1)";
        var realm = tail[..lastSpace].Trim();
        // Create the realm if needed (mirrors the existing "bot" command), then queue N deferred spawns; the
        // BotManager spawns each on the next listener tick (or holds smart bots until a player anchor exists).
        _rooms.GetOrCreate(realm);
        for (int i = 0; i < count; i++)
            _bots.RequestSpawn(_rooms.Session(realm), _botIds.Next());
        return $"queued {count} bot spawn(s) for \"{realm}\" (runs on next listener tick)";
    }

    [ConsoleCommand("botsmart", Usage = "<on|off>", MinParts = 2, MinLevel = PlayerLevel.Admin)]
    private string BotSmart(CommandLine line)
    {
        var arg = Arg(line, 1).ToLowerInvariant();
        bool on = arg switch { "on" or "true" or "1" => true, "off" or "false" or "0" => false, _ => _bots.Smart };
        if (arg is not ("on" or "true" or "1" or "off" or "false" or "0")) return "usage: botsmart <on|off>";
        _bots.Smart = on;
        return $"botsmart: {(on ? "on" : "off")}";
    }

    // --- helpers ---------------------------------------------------------------------------------

    private static string Arg(CommandLine line, int index) => index < line.Parts.Count ? line.Parts[index] : "";

    /// <summary>Everything after the token at <paramref name="index"/> (so a realm name can contain spaces).</summary>
    private static string AfterArg(CommandLine line, int index)
    {
        var s = line.Raw;
        int pos = 0;
        for (int i = 0; i <= index; i++)
        {
            pos = s.IndexOf(' ', pos);
            if (pos < 0) return "";
            pos++;
        }
        return s[pos..].Trim();
    }

    /// <summary>Splits "&lt;realm&gt; [count|all]": if the last token is "all" or a positive int it is the
    /// limit and the rest is the realm; otherwise the whole input is the realm and the limit is unbounded.
    /// Returns the realm text, the despawn cap, and a human label for the queued message.</summary>
    private static (string realm, int max, string label) SplitTrailingCount(string input)
    {
        int lastSpace = input.LastIndexOf(' ');
        if (lastSpace >= 0)
        {
            var last = input[(lastSpace + 1)..];
            if (string.Equals(last, "all", System.StringComparison.OrdinalIgnoreCase))
                return (input[..lastSpace].Trim(), int.MaxValue, "all");
            if (int.TryParse(last, out var n) && n > 0)
                return (input[..lastSpace].Trim(), n, n.ToString());
        }
        return (input, int.MaxValue, "all");
    }
}
