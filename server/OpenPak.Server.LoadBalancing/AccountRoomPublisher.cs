using System.Net.Http.Json;
using System.Text.Json;
using OpenPak.Server.Core;

namespace OpenPak.Server.LoadBalancing;

public sealed class AccountRoomPublisher
{
    private readonly HttpClient _http;
    private readonly string? _baseUrl;
    private readonly string? _internalKey;
    private readonly string? _titleId;

    /// <param name="titleId">The title whose rooms this server publishes (Outbound = 0100ED9024EB8000).
    /// Publishing is skipped without it: a wrong title id would file rooms under another game.</param>
    public AccountRoomPublisher(HttpClient http, string? baseUrl, string? internalKey = null, string? titleId = null)
    {
        _http = http;
        _baseUrl = baseUrl?.TrimEnd('/');
        _internalKey = internalKey;
        _titleId = titleId;
    }

    public async Task PublishAsync(string identity, string room)
    {
        if (string.IsNullOrEmpty(_baseUrl)) return;
        if (string.IsNullOrEmpty(_titleId))
        {
            Log.Warn("Account", "Room presence publishing is configured but no title id is set (OPENPAK_TITLE_ID)");
            return;
        }
        try
        {
            using var resolve = identity.StartsWith("nex:", StringComparison.Ordinal)
                ? new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/internal/pid-by-nex-token")
                {
                    Content = JsonContent.Create(new { token = identity[4..] })
                }
                : new HttpRequestMessage(HttpMethod.Get,
                    $"{_baseUrl}/internal/pid-by-bsdid?bsdid={Uri.EscapeDataString(identity.StartsWith("bsdid:", StringComparison.Ordinal) ? identity[6..] : identity)}");
            if (!string.IsNullOrEmpty(_internalKey)) resolve.Headers.Add("X-Internal-Key", _internalKey);
            using var resolved = await _http.SendAsync(resolve).ConfigureAwait(false);
            if (!resolved.IsSuccessStatusCode)
            {
                Log.Warn("Account", $"Account resolution failed: HTTP {(int)resolved.StatusCode}");
                return;
            }
            using var json = JsonDocument.Parse(await resolved.Content.ReadAsStreamAsync().ConfigureAwait(false));
            if (!json.RootElement.TryGetProperty("pid", out var pidElement) || !pidElement.TryGetUInt64(out var pid))
            {
                Log.Warn("Account", "Account resolution returned no PID");
                return;
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/internal/photon-room");
            if (!string.IsNullOrEmpty(_internalKey)) request.Headers.Add("X-Internal-Key", _internalKey);
            request.Content = JsonContent.Create(new { pid, room, app_id = _titleId });
            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                Log.Warn("Account", $"Room presence publish failed: HTTP {(int)response.StatusCode}");
            else
                Log.Info("Account", $"Published {_titleId} room {room} for PID {pid}");
        }
        catch (Exception ex)
        {
            Log.Warn("Account", $"Room presence publish failed: {ex.GetType().Name}");
        }
    }
}
