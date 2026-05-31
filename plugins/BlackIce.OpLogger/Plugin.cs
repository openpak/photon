using System;
using System.IO;
using BepInEx;
using HarmonyLib;

namespace BlackIce.OpLogger;

/// <summary>
/// Records decoded Photon operations, responses, and events from the live client to a
/// JSONL file. Because it hooks the client *after* decryption, it captures semantic data
/// the wire cannot reveal. Phase 0 reconnaissance only.
/// </summary>
[BepInPlugin("blackice.oplogger", "BlackIce Op Logger", "0.1.0")]
public sealed class Plugin : BaseUnityPlugin
{
    internal static StreamWriter Log = null!;

    private void Awake()
    {
        var path = Path.Combine(Paths.BepInExRootPath, "oplog.jsonl");
        Log = new StreamWriter(path, append: true) { AutoFlush = true };
        Write("session", new { note = "op-logger started", utc = DateTime.UtcNow.ToString("o") });
        new Harmony("blackice.oplogger").PatchAll();
        Logger.LogInfo($"BlackIce op-logger active -> {path}");
    }

    /// <summary>Append one structured record as a single JSON line.</summary>
    internal static void Write(string kind, object payload)
    {
        var json = SimpleJson.Serialize(new { t = DateTime.UtcNow.ToString("o"), kind, payload });
        lock (Log) Log.WriteLine(json);
    }
}
