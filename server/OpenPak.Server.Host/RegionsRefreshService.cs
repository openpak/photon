using Microsoft.Extensions.Hosting;
using OpenPak.Server.Core;
using OpenPak.Server.LoadBalancing;

namespace OpenPak.Server.Host;

/// <summary>Re-reads the routing table from the account core every 30 s. Nodes announce once a minute,
/// so a node that appears or leaves rotation is seen here within about a minute and a half.</summary>
public sealed class RegionsRefreshService : BackgroundService
{
    private readonly NodeDirectory _nodes;
    private readonly ServerConfig _config;

    public RegionsRefreshService(NodeDirectory nodes, ServerConfig config)
    {
        _nodes = nodes;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_nodes.Enabled || _config.Federation.IsNode) return;
        Log.Info("Federation", "routing table from the account core");
        while (!stoppingToken.IsCancellationRequested)
        {
            await _nodes.RefreshAsync(stoppingToken);
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
