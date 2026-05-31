namespace BlackIce.Recon.Catalog;

/// <summary>A networked remote-procedure-call handler discovered via the [PunRPC] attribute.</summary>
public sealed record RpcEntry(string DeclaringType, string Method, string[] Parameters, bool ReferencesMasterClient);

/// <summary>A named protocol constant (Photon OperationCode/EventCode/ParameterCode/StatusCode member).</summary>
public sealed record ConstantEntry(string Group, string Name, object Value);

/// <summary>A type that performs continuous state replication via OnPhotonSerializeView.</summary>
public sealed record SerializeViewEntry(string DeclaringType, string[] StreamCallOrder);

/// <summary>A prefab name passed to PhotonNetwork.Instantiate / InstantiateRoomObject.</summary>
public sealed record InstantiateEntry(string DeclaringType, string Method, string PrefabName);
