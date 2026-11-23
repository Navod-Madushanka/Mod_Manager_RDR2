using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Saddlebag.Core.Nexus;

// Shapes follow Nexus' own client library (node-nexus-api, src/types.ts); only the fields Saddlebag reads.

public sealed record NexusModInfo(
    [property: JsonPropertyName("mod_id")] long ModId,
    [property: JsonPropertyName("domain_name")] string? DomainName,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("updated_timestamp")] long UpdatedTimestamp);

public sealed record NexusFile(
    [property: JsonPropertyName("file_id")] long FileId,
    [property: JsonPropertyName("category_id")] int CategoryId,
    [property: JsonPropertyName("category_name")] string? CategoryName,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("file_name")] string? FileName,
    [property: JsonPropertyName("uploaded_timestamp")] long UploadedTimestamp,
    [property: JsonPropertyName("is_primary")] bool IsPrimary)
{
    // category_id per Nexus' client: 1 main, 2 patch, 3 optional, 4 old, 6 deleted, 7 archived.
    // Names differ between Nexus APIs, so the id decides and the name is only a fallback.
    public bool IsMain => CategoryId == 1 || (CategoryId == 0 && string.Equals(CategoryName, "MAIN", StringComparison.OrdinalIgnoreCase));
    public bool IsRetired => CategoryId is 4 or 6 or 7 ||
        (CategoryId == 0 && CategoryName?.ToUpperInvariant() is "OLD_VERSION" or "ARCHIVED" or "DELETED" or "REMOVED");
}

public sealed record NexusFileUpdate(
    [property: JsonPropertyName("old_file_id")] long OldFileId,
    [property: JsonPropertyName("new_file_id")] long NewFileId,
    [property: JsonPropertyName("new_file_name")] string? NewFileName,
    [property: JsonPropertyName("uploaded_timestamp")] long UploadedTimestamp);

public sealed record NexusFiles(
    [property: JsonPropertyName("files")] List<NexusFile> Files,
    [property: JsonPropertyName("file_updates")] List<NexusFileUpdate>? FileUpdates);

public sealed record NexusUser(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("is_premium")] bool IsPremium);

public class NexusException(string message) : Exception(message);
public sealed class NexusRateLimitException() : NexusException("Nexus rate limit reached. Try again later; results are cached for an hour.");
public sealed class NexusAuthException() : NexusException("Nexus rejected the API key. Set a new one with `saddlebag nexus key --set`.");
public sealed class NexusNotFoundException(string what) : NexusException($"Nexus has no {what}.");

/// <summary>
/// Minimal client for the Nexus Mods v1 API: mod info, its file list, and key validation.
/// Endpoints and headers match Nexus' own client library: APIKEY plus Application-Name and Application-Version.
/// </summary>
public sealed class NexusClient
{
    public const string DefaultBaseUrl = "https://api.nexusmods.com/v1";
    private readonly HttpClient _http;
    private readonly string _base;

    /// <summary>Remaining request budget, from the last response's x-rl-* headers.</summary>
    public int? DailyRemaining { get; private set; }
    public int? HourlyRemaining { get; private set; }
    public int RequestsMade { get; private set; }

    public NexusClient(string apiKey, string appVersion, HttpMessageHandler? handler = null, string baseUrl = DefaultBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new NexusException("No Nexus API key. Set one with `saddlebag nexus key --set` or $SADDLEBAG_NEXUS_KEY.");
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.Add("APIKEY", apiKey.Trim());
        _http.DefaultRequestHeaders.Add("Application-Name", "Saddlebag");
        _http.DefaultRequestHeaders.Add("Application-Version", appVersion);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Saddlebag/{appVersion}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _base = baseUrl.TrimEnd('/');
    }

    public Task<NexusModInfo> GetModAsync(string game, long modId, CancellationToken ct = default) =>
        GetAsync<NexusModInfo>($"/games/{Uri.EscapeDataString(game)}/mods/{modId}", $"mod {modId} for {game}", ct);

    public Task<NexusFiles> GetFilesAsync(string game, long modId, CancellationToken ct = default) =>
        GetAsync<NexusFiles>($"/games/{Uri.EscapeDataString(game)}/mods/{modId}/files", $"file list for mod {modId}", ct);

    public Task<NexusUser> ValidateKeyAsync(CancellationToken ct = default) =>
        GetAsync<NexusUser>("/users/validate", "user for this key", ct);

    private async Task<T> GetAsync<T>(string path, string what, CancellationToken ct)
    {
        RequestsMade++;
        using var res = await _http.GetAsync(_base + path, ct);
        ReadLimits(res);
        switch (res.StatusCode)
        {
            case HttpStatusCode.TooManyRequests: throw new NexusRateLimitException();
            case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden: throw new NexusAuthException();
            case HttpStatusCode.NotFound: throw new NexusNotFoundException(what);
        }
        if (!res.IsSuccessStatusCode) throw new NexusException($"Nexus returned {(int)res.StatusCode} for the {what}.");
        try
        {
            return await res.Content.ReadFromJsonAsync<T>(ct) ?? throw new NexusException($"Nexus sent an empty {what}.");
        }
        catch (JsonException e) { throw new NexusException($"Couldn't read the {what} from Nexus: {e.Message}"); }
    }

    private void ReadLimits(HttpResponseMessage res)
    {
        static int? Header(HttpResponseMessage r, string name) =>
            r.Headers.TryGetValues(name, out var v) && int.TryParse(v.FirstOrDefault(), out var n) ? n : null;
        DailyRemaining = Header(res, "x-rl-daily-remaining") ?? DailyRemaining;
        HourlyRemaining = Header(res, "x-rl-hourly-remaining") ?? HourlyRemaining;
    }
}
