using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.Data;

namespace BlackIce.Server.LoadBalancing.Plugins;

/// <summary>
/// Live (room, actor) moderation sets. Unlike a game RPC, these are enforced at the <b>relay</b> level —
/// the server simply stops forwarding the offending actor's events — so they are reliable server-side
/// enforcement a client cannot ignore. <c>Frozen</c> actors have their position stream (PUN event 201)
/// dropped (they stop moving for everyone); <c>muted</c> actors have their RPC events (PUN event 200)
/// dropped. Thread-safe (ConcurrentDictionary): the console mutates these from the admin thread while the
/// relay reads them from the listener thread. The empty-byte value is just a presence marker.
/// </summary>
public sealed class ModerationState
{
    private readonly ConcurrentDictionary<(string Room, int Actor), byte> _muted = new();
    private readonly ConcurrentDictionary<(string Room, int Actor), byte> _frozen = new();

    public void Mute(string room, int actor) => _muted[(room, actor)] = 0;
    public void Unmute(string room, int actor) => _muted.TryRemove((room, actor), out _);
    public void Freeze(string room, int actor) => _frozen[(room, actor)] = 0;
    public void Unfreeze(string room, int actor) => _frozen.TryRemove((room, actor), out _);

    public bool IsMuted(string room, int actor) => _muted.ContainsKey((room, actor));
    public bool IsFrozen(string room, int actor) => _frozen.ContainsKey((room, actor));

    /// <summary>Actors currently muted in the room (snapshot).</summary>
    public IReadOnlyList<int> MutedIn(string room) =>
        _muted.Keys.Where(k => k.Room == room).Select(k => k.Actor).OrderBy(a => a).ToList();

    /// <summary>Actors currently frozen in the room (snapshot).</summary>
    public IReadOnlyList<int> FrozenIn(string room) =>
        _frozen.Keys.Where(k => k.Room == room).Select(k => k.Actor).OrderBy(a => a).ToList();
}

/// <summary>
/// Relay interceptor enforcing <see cref="ModerationState"/>: drops a frozen actor's position stream (201)
/// and a muted actor's RPCs (200), forwarding everything else unchanged. Runs on the single listener
/// thread per room; the shared state it reads is concurrent-safe.
/// </summary>
internal sealed class ModerationInterceptor : IEventInterceptor
{
    private readonly ModerationState _state;
    public ModerationInterceptor(ModerationState state) => _state = state;

    public RelayVerdict Intercept(EventContext ctx)
    {
        // Freeze = drop the position stream so the actor stops moving for everyone in the room.
        if (ctx.Event.Code == PhotonCodes.PunEvent.SendSerialize && _state.IsFrozen(ctx.RoomName, ctx.SenderActor))
            return RelayVerdict.Drop();

        // Mute = drop the actor's RPCs (PUN event 200). NOTE: this silences ALL of the actor's [PunRPC]
        // invocations broadly (chat, actions, anything PUN-RPC-driven), not chat alone, because every
        // PUN RPC shares event code 200 and the method id lives inside the encoded payload.
        // GAP: the precise chat-only RPC method code is undocumented — until it's identified, mute is the
        // broad RPC silence. Revisit once the chat RPC method id is captured (docs/protocol/).
        if (ctx.Event.Code == PhotonCodes.PunEvent.Rpc && _state.IsMuted(ctx.RoomName, ctx.SenderActor))
            return RelayVerdict.Drop();

        return RelayVerdict.Forward(ctx.Event);
    }
}

/// <summary>
/// Built-in plugin providing relay-level moderation: <c>mute</c>/<c>unmute</c> (silence an actor's RPCs)
/// and <c>freeze</c>/<c>unfreeze</c> (stop an actor's movement), plus <c>modlist</c> to inspect a realm.
/// One shared <see cref="ModerationState"/> backs both the interceptor (enforcement) and the commands
/// (control).
/// </summary>
public sealed class ModerationPlugin : IServerPlugin
{
    public string Name => "moderation";
    public string Description => "Relay-level moderation: mute (drop an actor's RPCs) and freeze (drop an actor's position stream), enforced server-side.";

    public void Configure(PluginBuilder builder)
    {
        // ONE shared state instance: the interceptor reads it, the commands mutate it. The interceptor
        // factory captures this same instance so every room's interceptor sees the same mute/freeze sets.
        var state = new ModerationState();
        var rooms = (RoomRegistry?)builder.Services.GetService(typeof(RoomRegistry)) ?? new RoomRegistry();

        builder
            .AddInterceptor(() => new ModerationInterceptor(state))
            .AddCommands(new ModerationCommands(rooms, state));
    }
}

/// <summary>Console commands (Mod level) to mute/freeze actors at the relay and inspect a realm.</summary>
internal sealed class ModerationCommands
{
    private readonly RoomRegistry _rooms;
    private readonly ModerationState _state;

    public ModerationCommands(RoomRegistry rooms, ModerationState state)
    {
        _rooms = rooms;
        _state = state;
    }

    [ConsoleCommand("mute", Usage = "<realm> <actor>", MinParts = 3, MinLevel = PlayerLevel.Mod)]
    private string Mute(CommandLine line)
    {
        if (Resolve(line, out var realm, out var actor) is { } err) return err;
        _state.Mute(realm, actor);
        return $"muted {realm}#{actor} (their RPCs are dropped at the relay)";
    }

    [ConsoleCommand("unmute", Usage = "<realm> <actor>", MinParts = 3, MinLevel = PlayerLevel.Mod)]
    private string Unmute(CommandLine line)
    {
        if (Resolve(line, out var realm, out var actor) is { } err) return err;
        _state.Unmute(realm, actor);
        return $"unmuted {realm}#{actor}";
    }

    [ConsoleCommand("freeze", Usage = "<realm> <actor>", MinParts = 3, MinLevel = PlayerLevel.Mod)]
    private string Freeze(CommandLine line)
    {
        if (Resolve(line, out var realm, out var actor) is { } err) return err;
        _state.Freeze(realm, actor);
        return $"froze {realm}#{actor} (their position stream is dropped at the relay)";
    }

    [ConsoleCommand("unfreeze", Usage = "<realm> <actor>", MinParts = 3, MinLevel = PlayerLevel.Mod)]
    private string Unfreeze(CommandLine line)
    {
        if (Resolve(line, out var realm, out var actor) is { } err) return err;
        _state.Unfreeze(realm, actor);
        return $"unfroze {realm}#{actor}";
    }

    [ConsoleCommand("modlist", Usage = "<realm>", MinParts = 2, MinLevel = PlayerLevel.Mod)]
    private string ModList(CommandLine line)
    {
        var typed = AfterArg(line, 0);   // realm name may contain spaces
        var realm = _rooms.ResolveName(typed);
        if (realm is null) return $"no such room: {typed}";
        var muted = _state.MutedIn(realm);
        var frozen = _state.FrozenIn(realm);
        if (muted.Count == 0 && frozen.Count == 0) return $"\"{realm}\": no muted or frozen actors";
        return $"\"{realm}\": muted=[{string.Join(",", muted)}] frozen=[{string.Join(",", frozen)}]";
    }

    // --- helpers ---------------------------------------------------------------------------------

    /// <summary>Parses "&lt;realm&gt; &lt;actor&gt;" from a 3-part line: resolves the realm and the trailing
    /// integer actor. Returns null on success (out params set) or an error string to print.</summary>
    private string? Resolve(CommandLine line, out string realm, out int actor)
    {
        realm = "";
        actor = 0;
        // The actor is the last token; the realm is everything between the command word and it (may have spaces).
        if (!int.TryParse(line.Parts[^1], out actor)) return "actor must be a number.";
        var typed = BeforeLastArg(line);
        var resolved = _rooms.ResolveName(typed);
        if (resolved is null) return $"no such room: {typed}";
        realm = resolved;
        return null;
    }

    /// <summary>Everything after the token at <paramref name="index"/> (so trailing text can contain spaces).</summary>
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

    /// <summary>The realm portion of "&lt;cmd&gt; &lt;realm…&gt; &lt;actor&gt;": everything after the command
    /// word up to (not including) the final actor token, so a realm name with spaces survives.</summary>
    private static string BeforeLastArg(CommandLine line)
    {
        var rest = AfterArg(line, 0);                 // drop the command word
        int lastSpace = rest.LastIndexOf(' ');
        return lastSpace < 0 ? rest : rest[..lastSpace].Trim();
    }
}
