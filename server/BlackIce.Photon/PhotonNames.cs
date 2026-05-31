using System.Collections;
using System.Globalization;
using System.Text;

namespace BlackIce.Photon;

/// <summary>
/// Human-readable names for Photon LoadBalancing opcodes, event codes, parameter keys, and
/// eNet command/message types, plus value formatters for diagnostic logging. Names follow the
/// Photon LoadBalancing constants (OperationCode / EventCode / ParameterCode) we've mapped in
/// docs/protocol. Unknown codes render as e.g. "Op(123)" so logs never lose information.
/// </summary>
public static class PhotonNames
{
    public static string Op(byte code) => code switch
    {
        255 => "Op255",
        254 => "OpLeave",
        253 => "OpRaiseEvent",
        252 => "OpSetProperties",
        251 => "OpGetProperties",
        250 => "OpChangeGroups",
        249 => "OpExchangeKeysForEncryption",
        248 => "OpGetRegions",
        230 => "OpAuthenticate",
        229 => "OpJoinLobby",
        228 => "OpLeaveLobby",
        227 => "OpCreateGame",
        226 => "OpJoinGame",
        225 => "OpJoinRandomGame",
        224 => "OpCancelJoinRandom",
        223 => "OpFindFriends",
        222 => "OpGetLobbyStats",
        221 => "OpGetGameList",
        219 => "OpWebRpc",
        218 => "OpServerSettings",
        217 => "OpJoinRandomOrCreate",
        _ => $"Op({code})",
    };

    public static string Event(byte code) => code switch
    {
        255 => "EvGameListJoin",        // join / actor entered room
        254 => "EvLeave",
        253 => "EvPropertiesChanged",
        230 => "EvAuthOrGameList",       // auth event / lobby game-list (context-dependent)
        229 => "EvAppStats",
        228 => "EvQueueState",
        226 => "EvErrorInfo",
        224 => "EvGameListUpdate",
        223 => "EvGameList",
        210 => "EvCacheSliceChanged",
        202 => "EvPunInstantiation",     // PUN: networked object instantiation
        200 => "EvPunRpc",               // PUN: RPC
        199 => "EvServerMessage",        // OUR custom server->client text channel
        _ => $"Ev({code})",
    };

    public static string Param(byte key) => key switch
    {
        255 => "RoomName/GameId",
        254 => "ActorNr",
        253 => "TargetActorNr",
        252 => "ActorList/CacheOp",
        251 => "Properties",
        250 => "Broadcast",
        249 => "PlayerProperties",
        248 => "GameProperties",
        247 => "Cache/EventCaching",
        246 => "ReceiverGroup",
        245 => "CustomEventContent/Data",
        244 => "Code/EventCode",
        241 => "CleanupCacheOnLeave",
        236 => "PluginName",
        235 => "PluginVersion",
        230 => "Address",
        225 => "UserId",
        224 => "ApplicationId",
        222 => "GameList",
        221 => "Token/Secret",
        220 => "AppVersion",
        210 => "Region",
        _ => $"P({key})",
    };

    /// <summary>eNet command type name (transport layer).</summary>
    public static string Command(byte type) => type switch
    {
        1 => "Acknowledge",
        2 => "Connect",
        3 => "VerifyConnect",
        4 => "Disconnect",
        5 => "Ping",
        6 => "SendReliable",
        7 => "SendUnreliable",
        8 => "SendFragment",
        _ => $"Cmd({type})",
    };

    /// <summary>EgMessageType name (the [0xF3][type] framing).</summary>
    public static string MessageType(byte type) => type switch
    {
        0 => "Init",
        1 => "InitResponse",
        2 => "Operation",
        3 => "OperationResponse",
        4 => "Event",
        6 => "InternalOperationRequest",
        7 => "InternalOperationResponse",
        _ => $"MsgType({type})",
    };

    /// <summary>Renders a parameter table with named keys and value types, e.g. "ActorNr(254)=1, GameProperties(248)={...}".</summary>
    public static string Params(IDictionary<byte, object> p)
    {
        if (p.Count == 0) return "{}";
        var sb = new StringBuilder();
        foreach (var kv in p)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(Param(kv.Key)).Append('(').Append(kv.Key).Append(")=").Append(Value(kv.Value));
        }
        return sb.ToString();
    }

    /// <summary>Type-tagged, recursive value formatter. Byte arrays become hex; nested tables/arrays expand.</summary>
    public static string Value(object? v)
    {
        switch (v)
        {
            case null: return "null";
            case string s: return $"\"{s}\"";
            case byte[] bytes: return $"byte[{bytes.Length}]:{Hex(bytes, 64)}";
            case bool b: return b ? "true" : "false";
            case IDictionary dict:
                {
                    var sb = new StringBuilder("{");
                    bool first = true;
                    foreach (DictionaryEntry e in dict)
                    {
                        if (!first) sb.Append(", ");
                        first = false;
                        sb.Append(Value(e.Key)).Append('=').Append(Value(e.Value));
                    }
                    return sb.Append('}').ToString();
                }
            case IEnumerable seq when v is not string:
                {
                    var sb = new StringBuilder("[");
                    bool first = true;
                    foreach (var item in seq)
                    {
                        if (!first) sb.Append(", ");
                        first = false;
                        sb.Append(Value(item));
                    }
                    return sb.Append(']').ToString();
                }
            case IFormattable f:
                return $"{f.ToString(null, CultureInfo.InvariantCulture)}({v.GetType().Name})";
            default:
                return $"{v}({v.GetType().Name})";
        }
    }

    /// <summary>Hex dump, space-separated bytes, truncated with a "+N more" marker past <paramref name="max"/>.</summary>
    public static string Hex(ReadOnlySpan<byte> bytes, int max = 256)
    {
        int shown = Math.Min(bytes.Length, max);
        var sb = new StringBuilder(shown * 3 + 16);
        for (int i = 0; i < shown; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(bytes[i].ToString("X2"));
        }
        if (bytes.Length > max) sb.Append($" …(+{bytes.Length - max}B)");
        return sb.ToString();
    }
}
