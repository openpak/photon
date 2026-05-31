using BepInEx;
using Photon.Pun;
using Photon.Realtime;            // IOnEventCallback
using ExitGames.Client.Photon;    // EventData
using UnityEngine;

namespace BlackIce.Motd;

/// <summary>
/// Renders the server's Message of the Day as a native chat line. The server publishes the text
/// as the room custom property "motd"; on join we show it via the game's own ChatGUI.
/// Event code 199 (ServerMessage) is also handled to display live server messages.
/// </summary>
[BepInPlugin("blackice.motd", "BlackIce MOTD", "0.1.0")]
public sealed class MotdPlugin : BaseUnityPlugin
{
    internal static BepInEx.Logging.ManualLogSource Log = null!;
    internal const string MotdPropertyKey = "motd";
    internal const string Sender = "ouroborOS";   // matches the game's own system-line sender

    private void Awake()
    {
        Log = Logger;
        var go = new GameObject("BlackIce.MotdReceiver");
        DontDestroyOnLoad(go);
        go.AddComponent<MotdReceiver>();
        Logger.LogInfo("BlackIce MOTD active");
    }
}

/// <summary>Listens for room join and event 199 (ServerMessage) and renders the text as an Info chat line.</summary>
internal sealed class MotdReceiver : MonoBehaviourPunCallbacks, IOnEventCallback
{
    private const byte ServerMessageCode = 199;

    public override void OnJoinedRoom()
    {
        var props = PhotonNetwork.CurrentRoom?.CustomProperties;
        if (props != null && props.TryGetValue(MotdPlugin.MotdPropertyKey, out var value) && value is string motd
            && !string.IsNullOrWhiteSpace(motd))
        {
            Show(motd);
        }
    }

    public void OnEvent(EventData photonEvent)
    {
        if (photonEvent.Code == ServerMessageCode && photonEvent.CustomData is string text && !string.IsNullOrWhiteSpace(text))
            Show(text);
    }

    internal static void Show(string motd)
    {
        var chat = ChatGUI.instance;
        if (chat == null) return;
        foreach (var line in motd.Replace("\r", "").Split('\n'))
            chat.ComposeMessage(ChatMessage.Type.Info, MotdPlugin.Sender, line);
    }
}
