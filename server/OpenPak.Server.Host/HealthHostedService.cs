using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using OpenPak.Server.Core;
using OpenPak.Server.LoadBalancing;

namespace OpenPak.Server.Host;

/// <summary>
/// A tiny HTTP endpoint so something outside this process can tell a running
/// server from a silent one.
///
/// The game listeners are UDP, and a UDP port cannot be probed: nothing
/// distinguishes a bound socket from a dead one to a caller. A status page
/// therefore had no honest way to report this server and had to say so. One
/// TCP port fixes that, and while it is open it may as well answer the other
/// question people ask — who is actually playing — from the room registry
/// rather than from an estimate made elsewhere.
///
/// Deliberately HttpListener and not ASP.NET: two routes, no middleware, no
/// dependency, and nothing here is worth a web framework.
/// </summary>
public sealed class HealthHostedService : BackgroundService
{
    private readonly RoomRegistry _rooms;
    private readonly int _port;
    private readonly NodeDirectory _nodes;
    private readonly Dictionary<string, string> _nodeKeys;
    private readonly AccountRoomPublisher? _publisher;
    private readonly AccountEraser _eraser;

    public HealthHostedService(RoomRegistry rooms, ServerConfig config, NodeDirectory nodes, ErasureHub hub)
    {
        var internalKey = Environment.GetEnvironmentVariable("OPENPAK_INTERNAL_KEY") ?? "";
        var coreKey = Environment.GetEnvironmentVariable("ACCOUNT_INTERNAL_KEY") is { Length: > 0 } ck ? ck : internalKey;
        var coreAddr = Environment.GetEnvironmentVariable("ACCOUNT_GRPC_ADDR") is { Length: > 0 } ca ? ca : "openpak-account:20001";
        _eraser = new AccountEraser(coreKey.Length > 0 ? new CoreLinks(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }, coreAddr, coreKey) : null,
                                    hub, Environment.GetEnvironmentVariable("ACCOUNT_ERASE_KEY"));
        _rooms = rooms;
        _nodes = nodes;
        _nodeKeys = config.Federation.NodeKeys ?? new();
        string? env(params string[] names) => names.Select(Environment.GetEnvironmentVariable).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        _publisher = config.Federation.IsNode ? null
            : new AccountRoomPublisher(new HttpClient(), env("OPENPAK_BAAS_URL"), env("OPENPAK_INTERNAL_KEY"), env("OPENPAK_TITLE_ID"),
                                        PhotonTitles.Parse(env("OPENPAK_PHOTON_TITLES")));
        // Port 0 disables it. The default matches the container's published
        // health port; a LAN server that wants nothing listening sets 0.
        _port = config.Server.HealthPort;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_port <= 0)
        {
            Log.Info("HEALTH", "health endpoint disabled (port 0)");
            return;
        }
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://*:{_port}/");
        try
        {
            listener.Start();
        }
        catch (Exception e)
        {
            // A server that cannot open its health port must still serve games:
            // this is diagnostics, not the product.
            Log.Error("HEALTH", $"cannot listen on {_port}: {Redact.Scrub(e.Message)}");
            return;
        }
        Log.Info("HEALTH", $"listening on {_port} (/health, /players)");
        stoppingToken.Register(() => listener.Stop());

        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                return; // shutting down
            }
            catch (Exception e)
            {
                Log.Warn("HEALTH", $"accept failed: {Redact.Scrub(e.Message)}");
                continue;
            }
            try
            {
                Respond(ctx);
            }
            catch (Exception e)
            {
                Log.Warn("HEALTH", $"request failed: {Redact.Scrub(e.Message)}");
            }
        }
    }

    private void Respond(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        string json;
        int status;
        if (ctx.Request.HttpMethod == "POST" && path == "/internal/node-room")
            json = AcceptRoomReport(ctx.Request, out status);
        else if (ctx.Request.HttpMethod == "DELETE" && path.StartsWith("/internal/accounts/", StringComparison.Ordinal) && path.EndsWith("/data", StringComparison.Ordinal))
            (status, json) = _eraser.Handle(path["/internal/accounts/".Length..^"/data".Length], ctx.Request.Headers["Authorization"]);
        else
            json = Payload(path, _rooms, out status);
        var body = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.OutputStream.Write(body, 0, body.Length);
        ctx.Response.Close();
        LogRequest(ctx.Request.HttpMethod, path, status, started);
    }

    /// <summary>One line per request: method, path, status, duration. Never the query, headers or body.
    /// The probe routes (/health, /healthz) are skipped.</summary>
    public static void LogRequest(string method, string path, int status, long startedTimestamp)
    {
        if (path is "/health" or "/healthz") return;
        static string Clean(string s) => new(s.Take(64).Select(c => c < 0x20 || c == 0x7f || c == ' ' ? '?' : c).ToArray());
        Log.Info("HEALTH", $"http {Clean(method)} {Clean(path)} -> {status} {(long)System.Diagnostics.Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds}ms");
    }

    /// <summary>
    /// A region node reporting one of its rooms, with its own report key (<c>Federation.NodeKeys</c>): a
    /// report is accepted only for the node whose key it carries. Applied to the same registry the
    /// Master's lobby reads, so a room in Sydney is listed and joinable from anywhere.
    /// </summary>
    private string AcceptRoomReport(HttpListenerRequest req, out int status)
    {
        var presented = req.Headers["X-Node-Key"] ?? "";
        if (presented.Length == 0 || req.ContentLength64 > 64 * 1024)
        {
            status = 401;
            return JsonSerializer.Serialize(new { error = "node key" });
        }
        RoomReport? report;
        try
        {
            using var body = new StreamReader(req.InputStream, Encoding.UTF8);
            report = JsonSerializer.Deserialize<RoomReport>(body.ReadToEnd());
        }
        catch (JsonException)
        {
            report = null;
        }
        if (report is null || string.IsNullOrEmpty(report.Room) || string.IsNullOrEmpty(report.Node) || report.Room.Length > 64)
        {
            status = 400;
            return JsonSerializer.Serialize(new { error = "bad report" });
        }
        if (!NodeKeyMatches(_nodeKeys, report.Node, presented))
        {
            status = 401;
            return JsonSerializer.Serialize(new { error = "node key" });
        }
        report.ApplyTo(_rooms, _nodes);
        if (report.Identity is { Length: > 0 } identity && _publisher is not null)
            _ = _publisher.PublishAsync(identity, report.Room, report.AppId);
        status = 200;
        return JsonSerializer.Serialize(new { ok = true });
    }

    /// <summary>True when <paramref name="presented"/> is the configured report key of <paramref name="node"/>.
    /// No key configured for a node means its reports are refused.</summary>
    public static bool NodeKeyMatches(IReadOnlyDictionary<string, string> keys, string node, string presented)
    {
        if (!keys.TryGetValue(node, out var want) || string.IsNullOrEmpty(want) || presented.Length != want.Length) return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(want));
    }

    /// <summary>
    /// The answer for a path, separated from the socket so it can be tested
    /// without one. Player numbers are counted from live room membership, so
    /// they cannot drift from what the server is actually relaying.
    /// </summary>
    public static string Payload(string path, RoomRegistry rooms, out int status)
    {
        status = 200;
        object payload;
        switch (path)
        {
            case "/health":
            case "/healthz":
                payload = new { status = "ok" };
                break;
            case "/players":
                var players = 0;
                var list = new List<object>();
                foreach (var room in rooms.All)
                {
                    int count;
                    lock (room.ActorNumbers) count = room.ActorNumbers.Count;
                    players += count;
                    list.Add(new { room = room.Name, players = count });
                }
                payload = new { players, rooms = list.Count, detail = list };
                break;
            default:
                status = 404;
                payload = new { error = "not found" };
                break;
        }
        return JsonSerializer.Serialize(payload);
    }
}
