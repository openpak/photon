using System;
using System.IO;
using BepInEx;
using HarmonyLib;
using Photon.Pun;

namespace BlackIce.OpLogger;

/// <summary>
/// Records decoded Photon operations, responses, and events from the live client to a
/// JSONL file. Because it hooks the client *after* decryption, it captures semantic data
/// the wire cannot reveal — including the raw bytes of custom types (DamagePacket etc.,
/// hex-dumped) and PUN's ordered RpcList (to resolve shortcut-indexed RPCs by name).
/// Reconnaissance / live protocol-verification tool.
/// </summary>
[BepInPlugin("blackice.oplogger", "BlackIce Op Logger", "0.2.0")]
public sealed class Plugin : BaseUnityPlugin
{
    internal static StreamWriter Log = null!;

    private void Awake()
    {
        var path = Path.Combine(Paths.BepInExRootPath, "oplog.jsonl");
        Log = new StreamWriter(path, append: true) { AutoFlush = true };
        Write("session", new { note = "op-logger started", utc = DateTime.UtcNow.ToString("o") });
        DumpRpcList();
        new Harmony("blackice.oplogger").PatchAll();
        Logger.LogInfo($"BlackIce op-logger active -> {path}");
    }

    /// <summary>Records PUN's ordered RPC method list once. A client may send an RPC by its byte index into
    /// this list (the "method shortcut", Photon RPC key 5) instead of by name — so this table is what maps a
    /// captured shortcut index back to a method name (e.g. the death/respawn RPCs). Best-effort: the settings
    /// asset may not be populated in every build, so failures are logged and ignored.</summary>
    private void DumpRpcList()
    {
        try
        {
            var rpcs = PhotonNetwork.PhotonServerSettings?.RpcList;
            if (rpcs == null) { Write("rpclist", new { note = "RpcList unavailable" }); return; }
            var indexed = new string[rpcs.Count];
            for (int i = 0; i < rpcs.Count; i++) indexed[i] = rpcs[i];
            Write("rpclist", new { count = indexed.Length, methods = indexed });
            Logger.LogInfo($"BlackIce op-logger captured RpcList ({indexed.Length} methods)");
        }
        catch (Exception ex)
        {
            Write("rpclist", new { error = ex.GetType().Name + ": " + ex.Message });
        }
    }

    /// <summary>Append one structured record as a single JSON line.</summary>
    internal static void Write(string kind, object payload)
    {
        var json = SimpleJson.Serialize(new { t = DateTime.UtcNow.ToString("o"), kind, payload });
        lock (Log) Log.WriteLine(json);
    }
}
