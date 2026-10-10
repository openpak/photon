using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using OpenPak.Photon;
using OpenPak.Photon.Transport;
using OpenPak.Server.Core;
using OpenPak.Server.Data;
using OpenPak.Server.LoadBalancing;
using OpenPak.Server.LoadBalancing.Auth;
using Xunit;

namespace OpenPak.Server.Tests;

/// <summary>
/// Logs never carry personal data: no user or Steam ids, tickets, tokens, nicknames, room names, chat text or
/// client addresses, and no packet or parameter dumps unless PHOTON_TRACE_BODIES is on. The test drives real
/// clients over UDP into the real listeners and role handlers at Trace level (the loudest), then searches
/// everything written to the log file and the console for the sentinel values.
/// </summary>
[Collection("Log")]
public class LogPrivacyTests
{
    private const string SteamId = "76561198777666555";
    private const string Ticket = "TICKETSENTINELzq81";
    private const string AuthToken = "AUTHTOKENSENTINELvv52";
    private const string Room = "ROOMSENTINELxk93";
    private const string Nick = "NICKSENTINELpm27";
    private const string Chat = "/CHATSENTINELhw64 secret words";
    private const string Pin = "PINSENTINEL4471";

    private sealed class FakeValidator : ISteamTicketValidator
    {
        public Task<SteamAuthResult> ValidateAsync(byte[] ticket, ulong assertedSteamId, CancellationToken ct) =>
            Task.FromResult(SteamAuthResult.Verified(assertedSteamId));
    }

    private sealed class Client : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly IPEndPoint _server;
        private int _seq;
        public int LocalPort => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        public Client(int serverPort) { _server = new IPEndPoint(IPAddress.Loopback, serverPort); }

        private void Send(params NCommand[] cmds)
        {
            var body = new List<byte>();
            foreach (var c in cmds) body.AddRange(c.ToBytes());
            var packet = new byte[PhotonHeader.Size + body.Count];
            new PhotonHeader(0, false, (byte)cmds.Length, Environment.TickCount, 4242).WriteTo(packet);
            body.CopyTo(packet, PhotonHeader.Size);
            _udp.Send(packet, packet.Length, _server);
        }

        public void Connect() => Send(new NCommand(NCommand.Connect, 255, NCommand.FlagReliable, 4, 1, new byte[44]));

        public void Message(byte type, byte[] body)
        {
            var payload = new byte[2 + body.Length];
            payload[0] = WireMessage.Magic; payload[1] = type;
            body.CopyTo(payload, 2);
            Send(new NCommand(NCommand.SendReliable, 0, NCommand.FlagReliable, 4, ++_seq, payload));
        }

        public void Op(byte code, Dictionary<byte, object> p) => Message(WireMessage.Operation, WireMessage.RequestBody(code, p));

        public void Dispose() => _udp.Dispose();
    }

    // Console text from the listener threads, which may still be writing when the test reads it.
    private sealed class LockedWriter : TextWriter
    {
        private readonly System.Text.StringBuilder _text = new();
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(char value) { lock (_text) _text.Append(value); }
        public override void Write(string? value) { lock (_text) _text.Append(value); }
        public override string ToString() { lock (_text) return _text.ToString(); }
    }

    private static string Capture(Action run)
    {
        var path = Path.Combine(Path.GetTempPath(), $"openpak-privacy-{Guid.NewGuid():N}.log");
        var prevLevel = Log.Level;
        var prevOut = Console.Out; var prevErr = Console.Error;
        var console = new LockedWriter();
        try
        {
            Log.Level = LogLevel.Trace;
            Log.ToFile(path);
            Console.SetOut(console); Console.SetError(console);
            run();
            Log.Flush();
            return File.ReadAllText(path) + "\n" + console.ToString();
        }
        finally
        {
            Console.SetOut(prevOut); Console.SetError(prevErr);
            Log.Level = prevLevel;
            var safe = Path.Combine(Path.GetTempPath(), $"openpak-privacy-{Guid.NewGuid():N}.log");
            Log.ToFile(safe);
            try { File.Delete(path); } catch (IOException) { }
            try { File.Delete(safe); } catch (IOException) { }
        }
    }

    /// <summary>Runs a Name Server and a Game server, sends a public Steam-ticket auth to the first and an
    /// anonymous auth, room join, player properties and chat command to the second; returns the whole log
    /// and the clients' own ports.</summary>
    private static (string Log, int NamePort, int GamePort) DriveServers()
    {
        int namePort = 0, gamePort = 0;
        var log = Capture(() => (namePort, gamePort) = DriveCore());
        return (log, namePort, gamePort);
    }

    private static (int NamePort, int GamePort) DriveCore()
    {
        using var db = new TestDb();
        db.Context.Realms.Add(new Realm { Name = "co-op", IsEnabled = true });
        db.Context.SaveChanges();
        var motd = new MotdService(db.Context);

        var nameHandler = new NameServerHandler("127.0.0.1:5055", "privacy-test-secret-not-default", TestAccounts.Create(),
            new FakeValidator(), allowAnonymousLan: false);
        var gameHandler = new GameServerHandler("privacy-test-secret-not-default", new RoomRegistry(), allowAnonymousLan: true,
            realms: new RealmService(db.Context), motd: motd);
        var nameListener = new UdpListener("NameServer", 0, nameHandler);
        var gameListener = new UdpListener("GameServer", 0, gameHandler);
        using var cts = new CancellationTokenSource();
        var runs = new[] { Task.Run(() => nameListener.RunAsync(cts.Token)), Task.Run(() => gameListener.RunAsync(cts.Token)) };
        try
        {
            using var name = new Client(nameListener.LocalPort);
            name.Connect();
            name.Message(WireMessage.Init, new byte[41]);
            name.Op(PhotonCodes.Op.Authenticate, new()
            {
                { PhotonCodes.Param.UserId, SteamId },
                { PhotonCodes.Param.SteamTicket, System.Text.Encoding.ASCII.GetBytes(Ticket) },
                { PhotonCodes.Param.ApplicationId, "app-id-for-test" },
                { (byte)216, $"token={AuthToken}&pin={Pin}" },
            });

            using var game = new Client(gameListener.LocalPort);
            game.Connect();
            game.Message(WireMessage.Init, new byte[41]);
            game.Op(PhotonCodes.Op.Authenticate, new()
            {
                { PhotonCodes.Param.UserId, SteamId },
                { PhotonCodes.Param.Secret, AuthToken },
                { (byte)216, $"token={AuthToken}" },
            });
            game.Op(PhotonCodes.Op.JoinGame, new()
            {
                { PhotonCodes.Param.RoomName, Room },
                { PhotonCodes.Param.PlayerProperties, new Dictionary<object, object> { { (byte)255, Nick } } },
                { PhotonCodes.Param.UserId, SteamId },
            });
            game.Op(PhotonCodes.Op.RaiseEvent, new()
            {
                { PhotonCodes.Param.Code, (byte)200 },
                { PhotonCodes.Param.Data, new Dictionary<object, object>
                    { { (byte)3, "ReceiveChatMessage" }, { (byte)4, new object[] { Chat } } } },
            });
            // Garbage the listener must drop and log without echoing: the sentinels, as raw bytes.
            game.Message(WireMessage.Operation, System.Text.Encoding.ASCII.GetBytes(Nick + Room));

            Thread.Sleep(1500);   // datagrams are in order on loopback; give the listener threads time to drain
            return (name.LocalPort, game.LocalPort);
        }
        finally
        {
            cts.Cancel();
            try { Task.WaitAll(runs, TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        }
    }

    private static string HexOf(string s) => string.Join(' ', System.Text.Encoding.UTF8.GetBytes(s).Select(b => b.ToString("X2")));

    private static IEnumerable<string> Sentinels(int namePort, int gamePort) => new[]
    {
        SteamId, Ticket, AuthToken, Room, Nick, "CHATSENTINEL", "secret words", Pin,
        HexOf(Ticket), HexOf(Room), HexOf(Nick),
        "127.0.0.1", $":{namePort}", $":{gamePort}",
    };

    [Fact]
    public void Real_traffic_leaves_no_personal_data_in_the_log_even_at_trace_level()
    {
        Redact.TraceBodies = false;
        var (log, namePort, gamePort) = DriveServers();

        // The test only means something if the traffic really got through the code paths that log.
        Assert.Contains("validating Steam ticket for ~", log);
        Assert.Contains("EnterRoom ~", log);
        Assert.Contains("chat command intercepted", log);
        Assert.Contains("new peer ~", log);

        foreach (var s in Sentinels(namePort, gamePort))
            Assert.False(log.Contains(s, StringComparison.OrdinalIgnoreCase), $"the log carries a personal value ({s.Length} chars, starts '{s[..Math.Min(4, s.Length)]}')");
    }

    [Fact]
    public void Trace_bodies_switch_is_what_turns_the_dumps_on()
    {
        // Control: the same traffic with the switch on does show the sentinels, so the test above is able to fail.
        Redact.TraceBodies = true;
        try
        {
            var (log, _, _) = DriveServers();
            Assert.Contains(Room, log);
        }
        finally { Redact.TraceBodies = false; }
    }

    [Fact]
    public void Tags_are_stable_within_a_run_and_hide_the_value()
    {
        Assert.Equal(Redact.Tag(SteamId), Redact.Tag(SteamId));
        Assert.NotEqual(Redact.Tag(SteamId), Redact.Tag(SteamId + "1"));
        Assert.Matches("^~[0-9a-f]{6}$", Redact.Tag(SteamId));
        Assert.Equal(Redact.Room("[app/1.0] " + Room), Redact.Room(Room));
        Assert.DoesNotContain("203.0.113.7", Redact.Scrub("connect to 203.0.113.7:5055 failed"));
        Assert.DoesNotContain("query", Redact.Url("https://example.test/regions?key=query#frag"));
    }
}
