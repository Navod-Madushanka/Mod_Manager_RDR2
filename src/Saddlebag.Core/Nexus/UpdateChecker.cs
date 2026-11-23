using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Saddlebag.Core.Library;
using Saddlebag.Core.State;

namespace Saddlebag.Core.Nexus;

public enum UpdateStatus { UpToDate, UpdateAvailable, OldFile, Unknown, NotLinked, Unavailable, Error }

public sealed record UpdateResult(
    string ModId, string ModName, UpdateStatus Status,
    string? InstalledVersion, string? InstalledFile,
    string? LatestVersion, string? LatestFile, DateTime? LatestUploaded,
    string? Url, string Detail, bool MatchedByFile);

/// <summary>
/// Checks linked mods against Nexus. When it can find the exact file you installed (by its download name or upload
/// time), it follows Nexus' file_updates chain to the file that replaced it: exact, and independent of how authors
/// label versions. Otherwise it falls back to comparing version strings. Read-only: nothing is downloaded or changed.
/// </summary>
public sealed class UpdateChecker(StateStore store, ModLibrary library, NexusClient client)
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions Json = new();

    private sealed record CacheEntry(DateTime FetchedUtc, NexusModInfo Mod, NexusFiles Files);

    public async Task<List<UpdateResult>> CheckAsync(IEnumerable<string>? modIds = null, bool refresh = false, CancellationToken ct = default)
    {
        var mods = (modIds?.ToList() is { Count: > 0 } ids ? ids.Select(library.Require) : store.GetMods()).ToList();
        var results = new List<UpdateResult>();
        string? stopReason = null; // rate limit or no connection: don't keep hammering

        foreach (var mod in mods)
        {
            var link = library.LoadManifest(mod.Id).Nexus;
            if (link is null)
            {
                results.Add(new(mod.Id, mod.Name, UpdateStatus.NotLinked, mod.Version, null, null, null, null, null,
                    mod.IsLinked ? "your own build" : "no Nexus link: `saddlebag nexus link <mod> <url>`", false));
                continue;
            }
            if (stopReason is not null)
            {
                results.Add(Fail(mod, link, $"skipped: {stopReason}"));
                continue;
            }
            try
            {
                var (info, files) = await FetchAsync(link, refresh, ct);
                results.Add(Evaluate(mod.Id, mod.Name, mod.Version, link, info, files));
            }
            catch (NexusRateLimitException e) { stopReason = "rate limit reached"; results.Add(Fail(mod, link, e.Message)); }
            catch (NexusAuthException) { throw; } // a bad key fails every mod; say it once
            catch (NexusException e) { results.Add(Fail(mod, link, e.Message)); }
            catch (HttpRequestException e) { stopReason = "Nexus unreachable"; results.Add(Fail(mod, link, $"couldn't reach Nexus: {e.Message.TrimEnd('"', '.')}")); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { results.Add(Fail(mod, link, "Nexus didn't answer in time")); }
        }
        return results;
    }

    private static UpdateResult Fail(State.ModRecord mod, NexusLink link, string why) =>
        new(mod.Id, mod.Name, UpdateStatus.Error, mod.Version, link.FileName, null, null, null, link.FilesPage, why, false);

    private async Task<(NexusModInfo, NexusFiles)> FetchAsync(NexusLink link, bool refresh, CancellationToken ct)
    {
        var key = $"nexus_cache:{link.Game}/{link.ModId}";
        if (!refresh && store.GetSetting(key) is { Length: > 0 } json)
        {
            var cached = JsonSerializer.Deserialize<CacheEntry>(json, Json);
            if (cached is not null && DateTime.UtcNow - cached.FetchedUtc < CacheTtl) return (cached.Mod, cached.Files);
        }
        var info = await client.GetModAsync(link.Game, link.ModId, ct);
        var files = await client.GetFilesAsync(link.Game, link.ModId, ct);
        store.SetSetting(key, JsonSerializer.Serialize(new CacheEntry(DateTime.UtcNow, info, files), Json));
        return (info, files);
    }

    /// <summary>The decision, as a pure function of what Nexus returned.</summary>
    public static UpdateResult Evaluate(string modId, string modName, string? installedVersion, NexusLink link, NexusModInfo info, NexusFiles files)
    {
        var url = link.FilesPage;
        if (!info.Available || info.Status is not (null or "published"))
            return new(modId, modName, UpdateStatus.Unavailable, installedVersion, link.FileName, null, null, null, url,
                $"the Nexus page is {info.Status ?? "unavailable"}", false);

        var byId = files.Files.ToDictionary(f => f.FileId);
        var installed = files.Files.FirstOrDefault(f => link.FileName is not null && string.Equals(f.FileName, link.FileName, StringComparison.OrdinalIgnoreCase))
                        ?? files.Files.FirstOrDefault(f => link.UploadedTimestamp is { } ts && f.UploadedTimestamp == ts);
        var newestMain = files.Files.Where(f => f.IsMain).MaxBy(f => f.UploadedTimestamp);
        static DateTime When(long ts) => DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;

        if (installed is not null)
        {
            // Follow "this file was replaced by that one" to the end of the chain.
            var next = (files.FileUpdates ?? []).GroupBy(u => u.OldFileId).ToDictionary(g => g.Key, g => g.MaxBy(u => u.UploadedTimestamp)!);
            var current = installed;
            var seen = new HashSet<long> { current.FileId };
            while (next.TryGetValue(current.FileId, out var u) && seen.Add(u.NewFileId))
            {
                if (byId.TryGetValue(u.NewFileId, out var nf)) current = nf;
                else
                {
                    current = new NexusFile(u.NewFileId, 0, null, u.NewFileName, null, u.NewFileName, u.UploadedTimestamp, false);
                    break;
                }
            }
            var installedLabel = installed.FileName ?? installed.Name;
            if (current.FileId != installed.FileId)
                return new(modId, modName, UpdateStatus.UpdateAvailable, installed.Version ?? installedVersion, installedLabel,
                    current.Version, current.FileName ?? current.Name, When(current.UploadedTimestamp), url,
                    "the author marked a newer file as the update to yours", true);
            if (installed.IsRetired && newestMain is not null && newestMain.FileId != installed.FileId)
                return new(modId, modName, UpdateStatus.OldFile, installed.Version ?? installedVersion, installedLabel,
                    newestMain.Version, newestMain.FileName ?? newestMain.Name, When(newestMain.UploadedTimestamp), url,
                    "your file is now listed as an old version; check the newest main file", true);
            return new(modId, modName, UpdateStatus.UpToDate, installed.Version ?? installedVersion, installedLabel,
                installed.Version, installedLabel, When(installed.UploadedTimestamp), url, "no newer file replaces yours", true);
        }

        // No exact file: compare versions, mod-level first.
        var latest = info.Version ?? newestMain?.Version;
        var cmp = ModVersion.Compare(installedVersion, latest);
        var guessNote = link.Guessed ? " (mod id guessed from the file name and no file matched it; confirm with `nexus link`)" : "";
        return cmp switch
        {
            < 0 => new(modId, modName, UpdateStatus.UpdateAvailable, installedVersion, link.FileName, latest, newestMain?.FileName,
                newestMain is null ? null : When(newestMain.UploadedTimestamp), url, "Nexus lists a newer version" + guessNote, false),
            >= 0 => new(modId, modName, UpdateStatus.UpToDate, installedVersion, link.FileName, latest, newestMain?.FileName,
                newestMain is null ? null : When(newestMain.UploadedTimestamp), url, "your version is the latest listed" + guessNote, false),
            null => new(modId, modName, UpdateStatus.Unknown, installedVersion, link.FileName, latest, newestMain?.FileName,
                newestMain is null ? null : When(newestMain.UploadedTimestamp), url,
                "can't tell which version you have: re-import the original Nexus download, or set it with `nexus link --version`" + guessNote, false),
        };
    }
}

/// <summary>
/// Where the API key lives: $SADDLEBAG_NEXUS_KEY wins; otherwise a file in the Saddlebag folder encrypted for your
/// Windows account (DPAPI). Elsewhere only the environment variable works: the key is never stored in plain text.
/// </summary>
public sealed class NexusKeyStore(string home)
{
    public const string EnvVar = "SADDLEBAG_NEXUS_KEY";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Saddlebag.NexusKey.v1");
    public string KeyFile => Path.Combine(home, "nexus.key");

    public static bool CanStore => OperatingSystem.IsWindows();

    public (string? Key, string Source) Load()
    {
        var env = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(env)) return (env.Trim(), $"${EnvVar}");
        if (OperatingSystem.IsWindows() && File.Exists(KeyFile))
        {
            try { return (Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(KeyFile), Entropy, DataProtectionScope.CurrentUser)), "saved key"); }
            catch (CryptographicException) { return (null, "saved key can't be decrypted (made by another Windows account?)"); }
        }
        return (null, "none");
    }

    public void Save(string key)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException($"Saving the key needs Windows (it's encrypted for your account). Set ${EnvVar} instead.");
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(KeyFile + ".tmp", bytes);
        File.Move(KeyFile + ".tmp", KeyFile, overwrite: true);
    }

    public bool Clear()
    {
        if (!File.Exists(KeyFile)) return false;
        File.Delete(KeyFile);
        return true;
    }
}
