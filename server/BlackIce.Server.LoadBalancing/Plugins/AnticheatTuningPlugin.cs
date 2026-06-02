using System.Globalization;
using BlackIce.Server.Core;
using BlackIce.Server.Data;

namespace BlackIce.Server.LoadBalancing.Plugins;

/// <summary>
/// Built-in plugin exposing the live anti-cheat tunables (<see cref="AnticheatOptions"/>) to the admin
/// console, so thresholds can be inspected and adjusted — and enforcement toggled — at runtime without a
/// restart. It contributes only console commands; it adds no interceptors of its own (the validators that
/// read these knobs live in the anti-cheat plugin). It resolves the same <see cref="AnticheatOptions"/>
/// singleton the validators read, so a set takes effect on the next event the relay validates.
/// </summary>
public sealed class AnticheatTuningPlugin : IServerPlugin
{
    public string Name => "anticheat-tuning";
    public string Description => "Admin console to GET/SET the live anti-cheat tunables and toggle enforcement at runtime. Mutates the shared AnticheatOptions the validators read.";

    public void Configure(PluginBuilder builder)
    {
        // Resolve the SAME options instance the validators read so a set is visible to them immediately;
        // fall back to a fresh POCO only when no anti-cheat is configured (commands then tune a harmless local copy).
        var opt = (AnticheatOptions?)builder.Services.GetService(typeof(AnticheatOptions)) ?? new AnticheatOptions();
        builder.AddCommands(new AnticheatTuningCommands(opt));
    }
}

/// <summary>
/// Console commands to inspect and tune the anti-cheat knobs live (Admin). These mutate the shared
/// <see cref="AnticheatOptions"/> POCO directly and run inline: a tuning write may race a listener thread
/// reading a field mid-validation, but each field is an atomic word-sized read/write and a momentarily
/// stale threshold for one event is harmless for tuning knobs — no lock is warranted. Every set is
/// validated via <see cref="AnticheatOptions.Validate"/> and reverted if it would break the validators.
/// </summary>
internal sealed class AnticheatTuningCommands
{
    private readonly AnticheatOptions _opt;
    public AnticheatTuningCommands(AnticheatOptions opt) => _opt = opt;

    [ConsoleCommand("anticheat", Usage = "[get <field>|set <field> <value>|enforce <on|off>]", MinLevel = PlayerLevel.Admin)]
    private string Anticheat(CommandLine line)
    {
        var verb = Arg(line, 1).ToLowerInvariant();
        switch (verb)
        {
            case "":
                return Dump();
            case "get":
                {
                    var field = Arg(line, 2);
                    if (field.Length == 0) return "usage: anticheat get <field>";
                    var value = GetField(field);
                    return value is null ? $"no such field: {field}" : $"{field} = {value}";
                }
            case "set":
                {
                    var field = Arg(line, 2);
                    var raw = AfterArg(line, 2);
                    if (field.Length == 0 || raw.Length == 0) return "usage: anticheat set <field> <value>";
                    return SetField(field, raw);
                }
            case "enforce":
                {
                    var state = Arg(line, 2).ToLowerInvariant();
                    if (state is "on" or "true") { _opt.Enforce = true; return "set Enforce = True"; }
                    if (state is "off" or "false") { _opt.Enforce = false; return "set Enforce = False"; }
                    return "usage: anticheat enforce <on|off>";
                }
            default:
                return "usage: anticheat [get <field>|set <field> <value>|enforce <on|off>]";
        }
    }

    /// <summary>Every tunable rendered as "name=value" lines (the bare `anticheat` dump).</summary>
    private string Dump() => string.Join('\n', new[]
    {
        $"Enforce={_opt.Enforce}",
        $"MaxSpeedUnitsPerSecond={Fmt(_opt.MaxSpeedUnitsPerSecond)}",
        $"MaxTeleportDistance={Fmt(_opt.MaxTeleportDistance)}",
        $"MaxDamagePerHit={Fmt(_opt.MaxDamagePerHit)}",
        $"MaxDamagePerWindow={Fmt(_opt.MaxDamagePerWindow)}",
        $"MaxEventsPerWindow={_opt.MaxEventsPerWindow}",
        $"MaxHitsPerWindow={_opt.MaxHitsPerWindow}",
        $"MaxHeadshotsPerWindow={_opt.MaxHeadshotsPerWindow}",
        $"RateWindowSeconds={Fmt(_opt.RateWindowSeconds)}",
        $"HeadshotFlagOffset={(_opt.HeadshotFlagOffset is int o ? o.ToString(CultureInfo.InvariantCulture) : "none")}",
        $"HeadshotFlagMask={_opt.HeadshotFlagMask}",
        $"AdminExemptLevel={_opt.AdminExemptLevel}",
    });

    /// <summary>Maps a case-insensitive field name to its current value as a string, or null if unknown.</summary>
    private string? GetField(string field) => field.ToLowerInvariant() switch
    {
        "enforce" => _opt.Enforce.ToString(),
        "maxspeedunitspersecond" => Fmt(_opt.MaxSpeedUnitsPerSecond),
        "maxteleportdistance" => Fmt(_opt.MaxTeleportDistance),
        "maxdamageperhit" => Fmt(_opt.MaxDamagePerHit),
        "maxdamageperwindow" => Fmt(_opt.MaxDamagePerWindow),
        "maxeventsperwindow" => _opt.MaxEventsPerWindow.ToString(CultureInfo.InvariantCulture),
        "maxhitsperwindow" => _opt.MaxHitsPerWindow.ToString(CultureInfo.InvariantCulture),
        "maxheadshotsperwindow" => _opt.MaxHeadshotsPerWindow.ToString(CultureInfo.InvariantCulture),
        "ratewindowseconds" => Fmt(_opt.RateWindowSeconds),
        "headshotflagoffset" => _opt.HeadshotFlagOffset is int o ? o.ToString(CultureInfo.InvariantCulture) : "none",
        "headshotflagmask" => _opt.HeadshotFlagMask.ToString(CultureInfo.InvariantCulture),
        "adminexemptlevel" => _opt.AdminExemptLevel.ToString(),
        _ => null,
    };

    /// <summary>
    /// Parses <paramref name="raw"/> by the field's type and assigns it; then validates and, if the new
    /// value would break the validators, reverts to the prior value and returns the error. This makes a bad
    /// set non-destructive: the live thresholds the relay reads can never be left in an invalid state.
    /// </summary>
    private string SetField(string field, string raw)
    {
        switch (field.ToLowerInvariant())
        {
            case "enforce":
                if (!TryParseBool(raw, out var b)) return $"invalid bool: {raw}";
                return Apply(field, raw, () => _opt.Enforce, v => _opt.Enforce = v, b);
            case "maxspeedunitspersecond":
                if (!TryFloat(raw, out var fs)) return $"invalid float: {raw}";
                return Apply(field, raw, () => _opt.MaxSpeedUnitsPerSecond, v => _opt.MaxSpeedUnitsPerSecond = v, fs);
            case "maxteleportdistance":
                if (!TryFloat(raw, out var ft)) return $"invalid float: {raw}";
                return Apply(field, raw, () => _opt.MaxTeleportDistance, v => _opt.MaxTeleportDistance = v, ft);
            case "maxdamageperhit":
                if (!TryFloat(raw, out var fh)) return $"invalid float: {raw}";
                return Apply(field, raw, () => _opt.MaxDamagePerHit, v => _opt.MaxDamagePerHit = v, fh);
            case "maxdamageperwindow":
                if (!TryFloat(raw, out var fw)) return $"invalid float: {raw}";
                return Apply(field, raw, () => _opt.MaxDamagePerWindow, v => _opt.MaxDamagePerWindow = v, fw);
            case "maxeventsperwindow":
                if (!TryInt(raw, out var ie)) return $"invalid int: {raw}";
                return Apply(field, raw, () => _opt.MaxEventsPerWindow, v => _opt.MaxEventsPerWindow = v, ie);
            case "maxhitsperwindow":
                if (!TryInt(raw, out var ih)) return $"invalid int: {raw}";
                return Apply(field, raw, () => _opt.MaxHitsPerWindow, v => _opt.MaxHitsPerWindow = v, ih);
            case "maxheadshotsperwindow":
                if (!TryInt(raw, out var ihs)) return $"invalid int: {raw}";
                return Apply(field, raw, () => _opt.MaxHeadshotsPerWindow, v => _opt.MaxHeadshotsPerWindow = v, ihs);
            case "ratewindowseconds":
                if (!TryDouble(raw, out var d)) return $"invalid double: {raw}";
                return Apply(field, raw, () => _opt.RateWindowSeconds, v => _opt.RateWindowSeconds = v, d);
            case "headshotflagoffset":
                if (raw.Equals("none", StringComparison.OrdinalIgnoreCase) || raw.Equals("null", StringComparison.OrdinalIgnoreCase))
                    return Apply(field, "none", () => _opt.HeadshotFlagOffset, v => _opt.HeadshotFlagOffset = v, (int?)null);
                if (!TryInt(raw, out var io)) return $"invalid int (or none): {raw}";
                return Apply(field, raw, () => _opt.HeadshotFlagOffset, v => _opt.HeadshotFlagOffset = v, (int?)io);
            case "headshotflagmask":
                if (!byte.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var by)) return $"invalid byte: {raw}";
                return Apply(field, raw, () => _opt.HeadshotFlagMask, v => _opt.HeadshotFlagMask = v, by);
            case "adminexemptlevel":
                if (!Enum.TryParse<PlayerLevel>(raw, ignoreCase: true, out var lvl)) return $"invalid PlayerLevel: {raw} (Player|Mod|Admin|Console)";
                return Apply(field, lvl.ToString(), () => _opt.AdminExemptLevel, v => _opt.AdminExemptLevel = v, lvl);
            default:
                return $"no such field: {field}";
        }
    }

    /// <summary>Assigns <paramref name="value"/>, re-validates, and reverts on error so a bad set can't break
    /// the validators. Returns "set &lt;field&gt; = &lt;value&gt;" on success or the first validation error.</summary>
    private string Apply<T>(string field, string shown, Func<T> read, Action<T> write, T value)
    {
        var prior = read();
        write(value);
        var errors = _opt.Validate();
        if (errors.Count > 0)
        {
            write(prior);   // revert: keep the live thresholds always valid
            return errors[0];
        }
        return $"set {field} = {shown}";
    }

    // --- parsing helpers (invariant culture so "1.5" parses regardless of host locale) -----------

    private static bool TryFloat(string s, out float v) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    private static bool TryDouble(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    private static bool TryInt(string s, out int v) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

    private static bool TryParseBool(string s, out bool v)
    {
        if (s is "on" or "true" or "1") { v = true; return true; }
        if (s is "off" or "false" or "0") { v = false; return true; }
        return bool.TryParse(s, out v);
    }

    private static string Fmt(float v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Fmt(double v) => v.ToString(CultureInfo.InvariantCulture);

    // --- arg helpers (mirrors ServerCommands) ----------------------------------------------------

    private static string Arg(CommandLine line, int index) => index < line.Parts.Count ? line.Parts[index] : "";

    /// <summary>Everything after the token at <paramref name="index"/> (so a value can contain spaces).</summary>
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
}
