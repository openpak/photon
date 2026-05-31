namespace BlackIce.Photon;

/// <summary>
/// Single source of truth for the Photon LoadBalancing + PUN numeric constants the server reads
/// and writes on the wire. Names mirror Photon's own OperationCode / EventCode / ParameterCode and
/// PUN's PunEvent / custom-type registrations. The diagnostic <em>names</em> for these codes live in
/// <see cref="PhotonNames"/>; their <em>values</em> live here so each code is defined exactly once
/// instead of being re-declared (e.g. <c>PData = 245</c>) across every handler.
/// </summary>
public static class PhotonCodes
{
    /// <summary>Photon LoadBalancing OperationCode — the <c>op</c> of an OperationRequest/Response.</summary>
    public static class Op
    {
        public const byte Authenticate = 230;
        public const byte JoinLobby = 229;
        public const byte LeaveLobby = 228;
        public const byte CreateGame = 227;
        public const byte JoinGame = 226;
        public const byte JoinRandomGame = 225;
        public const byte Leave = 254;
        public const byte RaiseEvent = 253;
        public const byte SetProperties = 252;
        public const byte GetProperties = 251;
        public const byte ChangeGroups = 248;
    }

    /// <summary>Photon LoadBalancing EventCode — room/lobby lifecycle events.</summary>
    public static class Event
    {
        public const byte Join = 255;               // an actor entered the room
        public const byte Leave = 254;              // an actor left the room
        public const byte PropertiesChanged = 253;  // game- or actor-property change
        public const byte GameList = 230;           // lobby room directory

        /// <summary>Our custom server-&gt;client text channel (rendered by the BlackIce.Motd plugin). Not a stock Photon event.</summary>
        public const byte ServerMessage = 199;
    }

    /// <summary>PUN-level event codes carried inside an OpRaiseEvent (the event code lives in <see cref="Param.Code"/>).</summary>
    public static class PunEvent
    {
        public const byte Rpc = 200;            // [PunRPC] invocation
        public const byte SendSerialize = 201;  // OnPhotonSerializeView / position stream (unreliable)
        public const byte Instantiation = 202;  // networked object spawn (cached for late joiners)
        public const byte Destroy = 204;        // networked object despawn
    }

    /// <summary>Photon LoadBalancing ParameterCode — keys in an operation/event parameter table.</summary>
    public static class Param
    {
        public const byte RoomName = 255;            // a.k.a. GameId
        public const byte ActorNr = 254;
        public const byte TargetActorNr = 253;
        public const byte ActorList = 252;
        public const byte Properties = 251;
        public const byte Broadcast = 250;
        public const byte PlayerProperties = 249;
        public const byte GameProperties = 248;
        public const byte Cache = 247;               // EventCaching enum
        public const byte ReceiverGroup = 246;
        public const byte Data = 245;                // CustomEventContent (the event payload hashtable)
        public const byte Code = 244;                // the event code an OpRaiseEvent carries
        public const byte CleanupCacheOnLeave = 241;
        public const byte Address = 230;             // server address handed back on auth/create
        public const byte UserId = 225;
        public const byte ApplicationId = 224;
        public const byte GameList = 222;            // { roomName -> properties } map
        public const byte Secret = 221;              // auth token
        public const byte AppVersion = 220;
        public const byte Region = 210;
    }

    /// <summary>Well-known byte keys inside a room's GameProperties hashtable (lobby browser slots read these).</summary>
    public static class RoomProperty
    {
        public const byte MaxPlayers = 255;
        public const byte IsVisible = 254;
        public const byte IsOpen = 253;
        public const byte PlayerCount = 252;
        public const byte MasterClientId = 248;
    }

    /// <summary>PUN custom-type codes — the 1-byte tag after GpBinary's <c>Custom</c> marker.</summary>
    public static class CustomType
    {
        public const byte Quaternion = 81;    // 4 big-endian floats
        public const byte Vector3 = 86;       // 3 big-endian floats
        public const byte Vector2 = 87;       // 2 big-endian floats
        public const byte Color = 67;         // 4 big-endian floats (r,g,b,a)
        public const byte DamagePacket = 68;  // game-specific; first 4 bytes = big-endian float damage
    }

    /// <summary>Byte keys inside a PUN RPC (200) payload hashtable.</summary>
    public static class RpcKey
    {
        public const byte ViewId = 0;
        public const byte MethodName = 3;
        public const byte Args = 4;
        public const byte MethodShortcut = 5;   // byte index into the project RpcList, sent in lieu of MethodName
    }

    /// <summary>Byte keys inside a PUN Instantiation (202) payload hashtable.</summary>
    public static class InstantiationKey
    {
        public const byte PrefabName = 0;
        public const byte Position = 1;         // Vector3 (custom type 86): the spawn's world position
        public const byte ServerTime = 6;       // PUN casts this to int unconditionally — must be present
        public const byte ViewId = 7;
    }
}
