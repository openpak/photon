using Microsoft.Extensions.Hosting;
using OpenPak.Server.Core;
using OpenPak.Server.LoadBalancing;

namespace OpenPak.Server.Host;

/// <summary>Re-reads the region list once a minute. Nodes announce on the same cadence, so a node
/// that appears or vanishes is seen here within two.</summary>
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
        if (string.IsNullOrWhiteSpace(_config.Federation.RegionsUrl) || _config.Federation.IsNode) return;
        Log.Info("Federation", $"regions from {_config.Federation.RegionsUrl}");
        while (!stoppingToken.IsCancellationRequested)
        {
            await _nodes.RefreshAsync(stoppingToken);
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
