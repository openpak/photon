using System.Collections;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.Data;

namespace BlackIce.Server.LoadBalancing;

/// <summary>
/// Intercepts in-room chat that is actually a server "/command" and turns it into a ServerMessage
/// event the client renders, instead of relaying it to other players. Extracted from
/// GameServerHandler so the chat surface (and its set of commands) can grow independently of room
/// auth/join/property handling.
/// </summary>
public sealed class ChatCommandHandler
{
    private const byte OpRaiseEvent = PhotonCodes.Op.RaiseEvent;
    private const byte PEventCode = PhotonCodes.Param.Code, PData = PhotonCodes.Param.Data;
    private const byte EvServerMessage = PhotonCodes.Event.ServerMessage;
    private const byte PunRpcEvent = PhotonCodes.PunEvent.Rpc;
    private const byte RpcMethodName = PhotonCodes.RpcKey.MethodName, RpcParams = PhotonCodes.RpcKey.Args,
                       RpcMethodShortcut = PhotonCodes.RpcKey.MethodShortcut;

    private readonly RealmService? _realms;
    private readonly MotdService? _motd;
    private readonly CommandRegistry? _commands;

    /// <param name="commands">The command set runnable from chat. Each command's <c>MinLevel</c> gates it
    /// against the caller's (verified) level, so a normal player can only run player-tier commands. Null →
    /// only the built-in <c>/motd</c> works (back-compat / tests).</param>
    public ChatCommandHandler(RealmService? realms, MotdService? motd, CommandRegistry? commands = null)
    {
        _realms = realms;
        _motd = motd;
        _commands = commands;
    }

    /// <summary>Builds a ServerMessage event: a server->client text line the BlackIce.Motd plugin renders.</summary>
    public static EventData ServerMessageEvent(string text) =>
        new(EvServerMessage, new() { { PData, text } });

    /// <summary>
    /// Inspects an inbound RaiseEvent (op 253). If it is a chat RPC whose text is a "/command",
    /// returns the ServerMessage to send back and the caller must NOT relay it. Returns null for
    /// non-commands (normal chat / other events) so future relay logic handles them.
    ///
    /// PUN serializes the RPC method either as the string name (key 3) or, when the method is in
    /// the project's RpcList, as a byte shortcut index (key 5). The shortcut index lives in the
    /// game's PhotonServerSettings asset, not in code, so we cannot resolve it statically. We
    /// therefore only ever intercept "/"-prefixed text (which only player chat carries): a named
    /// "ReceiveChatMessage" RPC, or a shortcut RPC (no name, key 5 present) whose first arg is a
    /// "/command". This works regardless of the shortcut index and leaves all normal RPCs and
    /// normal chat untouched. (B5 live capture should confirm no other RPC sends "/"-leading text.)
    /// </summary>
    public EventData? TryHandle(string? roomName, OperationRequest req, PlayerLevel callerLevel = PlayerLevel.Player)
    {
        if (req.OperationCode != OpRaiseEvent) return null;
        // Event code comes off the wire from an untrusted peer; validate rather than coerce.
        // A real PUN client always sends it as a GpBinary byte, so anything else (wrong type
        // or out of byte range) is malformed and ignored — no exception on bad input.
        if (!req.Parameters.TryGetValue(PEventCode, out var ec) || ec is not byte ecByte || ecByte != PunRpcEvent) return null;
        if (!req.Parameters.TryGetValue(PData, out var d) || d is not IDictionary rpc) return null;

        var method = rpc.Contains(RpcMethodName) ? rpc[RpcMethodName] as string : null;
        var isShortcutRpc = rpc.Contains(RpcMethodShortcut);
        var args = rpc.Contains(RpcParams) ? rpc[RpcParams] as object[] : null;
        var text = (args is { Length: > 0 } ? args[0] as string : null)?.Trim();

        if (string.IsNullOrEmpty(text) || text![0] != '/') return null;          // only intercept /commands
        var isChat = method == "ReceiveChatMessage" || (method is null && isShortcutRpc);
        if (!isChat) return null;

        Log.Info("GameServer", $"chat command intercepted: \"{text}\" (room=\"{roomName}\", " +
                               $"method={(method ?? "<shortcut>")}, level={callerLevel})");

        var line = text[1..].Trim();                                  // strip the leading '/'
        var word = (line.Split(' ', 2)[0]).ToLowerInvariant();

        // /motd is realm-aware and lives outside the command registry; keep it as a built-in (player-tier).
        if (word == "motd")
        {
            var realm = roomName is not null ? _realms?.Get(roomName) : null;
            var resolved = _motd?.Resolve(realm);
            return ServerMessageEvent(string.IsNullOrWhiteSpace(resolved) ? "No MOTD set." : resolved!);
        }

        // /help and /commands list exactly what THIS caller's level may run.
        if (word is "help" or "commands")
            return ServerMessageEvent(_commands?.HelpFor(callerLevel) ?? "No commands available.");

        // Any other /command: run it through the registry at the caller's level. The registry enforces
        // per-command MinLevel (a too-low caller is refused) and arg/usage checks, returning the result text.
        if (_commands is not null && _commands.TryExecute(line, callerLevel, out var output))
            return ServerMessageEvent(string.IsNullOrWhiteSpace(output) ? "(done)" : output);

        return ServerMessageEvent($"Unknown command: {text}  (try /help)");
    }
}
