using BlackIce.Photon;
using BlackIce.Server.Data;

namespace BlackIce.Server.LoadBalancing.Plugins;

/// <summary>
/// Built-in plugin demonstrating server-authoritative gameplay <b>mutators</b> — global rule tweaks applied
/// purely by rewriting the damage RPCs as they relay, so the vanilla client just renders bigger/smaller (or
/// always-critical) hits with no mod required. It is <b>inert by default</b> (×1 damage, no forced crits),
/// so the server behaves vanilla until an admin tunes it live with the <c>mutator</c> command. Because it
/// returns <see cref="RelayVerdict.Rewrite"/> (not a terminal verdict), the anti-cheat/game-mode validators
/// still see and can veto the rewritten hit.
/// </summary>
public sealed class MutatorsPlugin : IServerPlugin
{
    public string Name => "mutators";
    public string Description => "Global gameplay mutators (server-side): damage multiplier + force-crit, applied by rewriting damage RPCs. Inert until tuned.";
    public int Order => -100;   // rewrite damage BEFORE the validators so anti-cheat/game-modes see the final value

    public void Configure(PluginBuilder builder)
    {
        var state = new MutatorState();
        builder
            .AddInterceptor(() => new MutatorInterceptor(state))
            .AddCommands(new MutatorCommands(state));
    }
}

/// <summary>Shared, live-tunable mutator settings. The 32-bit fields are read on the relay thread and
/// written on the console thread; aligned reads/writes of <see cref="float"/>/<see cref="bool"/> are atomic
/// in the CLR, so no lock is needed for these independent scalars.</summary>
internal sealed class MutatorState
{
    public float DamageMultiplier = 1f;
    public bool ForceCrit;

    /// <summary>True when the settings would actually change a hit (otherwise the interceptor no-ops).</summary>
    public bool Active => DamageMultiplier != 1f || ForceCrit;
}

/// <summary>Rewrites the damage value (and optionally forces the crit flag) of every damage-carrying RPC
/// while the mutators are active; a pure pass-through otherwise.</summary>
internal sealed class MutatorInterceptor : IEventInterceptor
{
    private readonly MutatorState _state;
    public MutatorInterceptor(MutatorState state) => _state = state;

    public RelayVerdict Intercept(EventContext ctx)
    {
        if (!_state.Active) return RelayVerdict.Forward(ctx.Event);
        if (PunRpcInfo.From(ctx.Event)?.DamageValue is null) return RelayVerdict.Forward(ctx.Event);

        float mult = _state.DamageMultiplier;
        bool crit = _state.ForceCrit;
        bool changed = DamageData.TryRewriteDamage(ctx.Event, d => d * mult, forceCrit: crit ? true : null);
        return changed ? RelayVerdict.Rewrite(ctx.Event) : RelayVerdict.Forward(ctx.Event);
    }
}

/// <summary>Console commands to inspect and tune the gameplay mutators live (Admin).</summary>
internal sealed class MutatorCommands
{
    private readonly MutatorState _state;
    public MutatorCommands(MutatorState state) => _state = state;

    [ConsoleCommand("mutators", MinLevel = PlayerLevel.Admin)]
    private string Show(CommandLine line) =>
        $"mutators: damage ×{_state.DamageMultiplier:0.##}, force-crit {(_state.ForceCrit ? "on" : "off")} " +
        $"({(_state.Active ? "ACTIVE" : "inert")})";

    [ConsoleCommand("mutator", Usage = "<damage <mult>|crits <on|off>|reset>", MinParts = 2, MinLevel = PlayerLevel.Admin)]
    private string Set(CommandLine line)
    {
        var verb = line.Parts[1].ToLowerInvariant();
        switch (verb)
        {
            case "damage":
                if (line.Parts.Count < 3 || !float.TryParse(line.Parts[2], out var m) || m < 0f || !float.IsFinite(m))
                    return "usage: mutator damage <mult>   (a finite multiplier ≥ 0, e.g. 2.0)";
                _state.DamageMultiplier = m;
                return $"mutator: damage ×{m:0.##}";
            case "crits":
                if (line.Parts.Count < 3 || line.Parts[2].ToLowerInvariant() is not ("on" or "off"))
                    return "usage: mutator crits <on|off>";
                _state.ForceCrit = line.Parts[2].Equals("on", System.StringComparison.OrdinalIgnoreCase);
                return $"mutator: force-crit {(_state.ForceCrit ? "on" : "off")}";
            case "reset":
                _state.DamageMultiplier = 1f;
                _state.ForceCrit = false;
                return "mutator: reset to vanilla (×1, no forced crits)";
            default:
                return "usage: mutator <damage <mult>|crits <on|off>|reset>";
        }
    }
}
