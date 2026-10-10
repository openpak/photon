using System.Collections.Generic;
using System.Net.Http.Json;
using OpenPak.Server.Core;

namespace OpenPak.Server.LoadBalancing;

/// <summary>
/// Publishes the room a player just entered to the Switch adapter (nx-baas), which turns it
/// into that player's friend presence so a friend can join. The adapter resolves the identity
/// itself — it is the account authority — so this is one POST, not a resolve + publish pair.
/// </summary>
public sealed class AccountRoomPublisher
{
    private readonly HttpClient _http;
    private readonly string? _baseUrl;
    private readonly string? _internalKey;
    private readonly string? _titleId;
    private readonly IReadOnlyDictionary<string, string> _titlesByAppId;

    /// <param name="baseUrl">The adapter's internal game API (OPENPAK_BAAS_URL, e.g.
    /// http://nx-baas:20070). Publishing is skipped without it.</param>
    /// <param name="titleId">The title to file a room under when its Photon AppId is unknown or
    /// absent. Publishing is skipped when neither this nor the map yields one: a wrong title id
    /// would file rooms under another game.</param>
    /// <param name="titlesByAppId">Photon AppId → title id, from <see cref="PhotonTitles"/>. What
    /// makes one server able to host more than one game.</param>
    public AccountRoomPublisher(HttpClient http, string? baseUrl, string? internalKey = null, string? titleId = null,
                                IReadOnlyDictionary<string, string>? titlesByAppId = null)
    {
        _http = http;
        _baseUrl = baseUrl?.TrimEnd('/');
        _internalKey = internalKey;
        _titleId = titleId;
        _titlesByAppId = titlesByAppId ?? new Dictionary<string, string>();
    }

    /// <param name="identity">What the client's id_token yielded: "nex:&lt;token&gt;" or
    /// "bsdid:&lt;bs:did&gt;" (see <see cref="AccountIdentityRegistry.ExtractToken"/>). Passed
    /// through verbatim — only the adapter can verify it.</param>
    /// <param name="appId">The Photon AppId the room's peer authenticated with (param 224), which
    /// names the game. Null or unmapped falls back to the configured default title.</param>
    public async Task PublishAsync(string identity, string room, string? appId = null)
    {
        if (string.IsNullOrEmpty(_baseUrl)) return;
        var titleId = appId is not null && _titlesByAppId.TryGetValue(appId, out var mapped) ? mapped : _titleId;
        if (string.IsNullOrEmpty(titleId))
        {
            Log.Warn("Account", $"Room presence publishing is configured but AppId {appId ?? "(none)"} maps to no " +
                                "title (OPENPAK_PHOTON_TITLES / OPENPAK_TITLE_ID)");
            return;
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/internal/photon-room");
            if (!string.IsNullOrEmpty(_internalKey)) request.Headers.Add("X-Internal-Key", _internalKey);
            request.Content = JsonContent.Create(new { identity, room, app_id = titleId });
            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                Log.Warn("Account", $"Room presence publish failed: HTTP {(int)response.StatusCode}");
            else
                Log.Info("Account", $"Published {titleId} room {Redact.Room(room)}");
        }
        catch (Exception ex)
        {
            Log.Warn("Account", $"Room presence publish failed: {ex.GetType().Name}");
        }
    }
}
