using System.Text.RegularExpressions;

namespace Saddlebag.Core.Nexus;

/// <summary>Which Nexus mod (and ideally which file) a library mod came from. Saved in mod.json.</summary>
public sealed class NexusLink
{
    public string Game { get; set; } = NexusNames.Rdr2Domain;
    public long ModId { get; set; }
    /// <summary>The archive's file name as downloaded, e.g. "Trainer-1234-1-2-1700000000.zip".</summary>
    public string? FileName { get; set; }
    /// <summary>Upload time from the archive name; identifies the exact Nexus file.</summary>
    public long? UploadedTimestamp { get; set; }
    /// <summary>True when the mod id was guessed from the file name rather than given as a URL.</summary>
    public bool Guessed { get; set; }

    public string FilesPage => $"https://www.nexusmods.com/{Game}/mods/{ModId}?tab=files";
}

public sealed record ArchiveNameInfo(long? ModId, string? Version, long Timestamp);

public static partial class NexusNames
{
    public const string Rdr2Domain = "reddeadredemption2";

    [GeneratedRegex(@"nexusmods\.com/(?<game>[a-z0-9]+)/mods/(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ModUrl();

    // Nexus names downloads "<name>-<mod id>-<version, dots as dashes>-<upload unix time>.<ext>".
    [GeneratedRegex(@"^(?<rest>.+)-(?<ts>1\d{9})$")]
    private static partial Regex ArchiveTail();

    public static (string Game, long ModId)? ParseUrl(string? url)
    {
        if (url is null) return null;
        var m = ModUrl().Match(url);
        return m.Success && long.TryParse(m.Groups["id"].Value, out var id) ? (m.Groups["game"].Value.ToLowerInvariant(), id) : null;
    }

    /// <summary>
    /// Reads the mod id, version and upload time from a Nexus download name. The timestamp is reliable; the mod id is a
    /// best guess (names can contain numbers too), so the update check confirms it against the real file list.
    /// </summary>
    public static ArchiveNameInfo? ParseArchiveName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
        var m = ArchiveTail().Match(stem);
        if (!m.Success) return null;
        var ts = long.Parse(m.Groups["ts"].Value);
        var parts = m.Groups["rest"].Value.Split('-');
        // The first all-digit part after at least one name part is the mod id; the digits after it are the version.
        for (var i = 1; i < parts.Length; i++)
        {
            if (!parts[i].All(char.IsAsciiDigit) || parts[i].Length == 0) continue;
            var version = string.Join('.', parts[(i + 1)..].Where(p => p.Length > 0));
            return new ArchiveNameInfo(long.Parse(parts[i]), version.Length > 0 ? version : null, ts);
        }
        return new ArchiveNameInfo(null, null, ts);
    }

    /// <summary>A Nexus mod page URL, or a bare RDR2 mod id.</summary>
    public static (string Game, long ModId) ParseTarget(string urlOrId) =>
        ParseUrl(urlOrId) is { } u ? u
        : long.TryParse(urlOrId.Trim(), out var id) && id > 0 ? (Rdr2Domain, id)
        : throw new InvalidOperationException($"'{urlOrId}' isn't a Nexus mod URL or id.");

    public static NexusLink? FromImport(string? url, string? archiveFileName)
    {
        var info = archiveFileName is null ? null : ParseArchiveName(archiveFileName);
        if (ParseUrl(url) is { } u)
            return new NexusLink { Game = u.Game, ModId = u.ModId, FileName = info is null ? null : archiveFileName, UploadedTimestamp = info?.Timestamp };
        if (info?.ModId is { } guessed)
            return new NexusLink { ModId = guessed, FileName = archiveFileName, UploadedTimestamp = info.Timestamp, Guessed = true };
        return null;
    }
}

/// <summary>Compares mod version strings the way authors write them: "1.2.10" &gt; "1.2.9", "v2" = "2.0".</summary>
public static class ModVersion
{
    private static List<string> Parts(string v) =>
        v.Trim().TrimStart('v', 'V').Split(['.', '-', '_', ' '], StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>Negative if a is older, positive if newer, 0 if the same. Null if either is missing.</summary>
    public static int? Compare(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return null;
        var pa = Parts(a);
        var pb = Parts(b);
        for (var i = 0; i < Math.Max(pa.Count, pb.Count); i++)
        {
            var x = i < pa.Count ? pa[i] : "0";
            var y = i < pb.Count ? pb[i] : "0";
            int c;
            if (long.TryParse(x, out var nx) && long.TryParse(y, out var ny)) c = nx.CompareTo(ny);
            else
            {
                // "1.2b" vs "1.2": compare the leading number, then the suffix.
                var lx = new string(x.TakeWhile(char.IsAsciiDigit).ToArray());
                var ly = new string(y.TakeWhile(char.IsAsciiDigit).ToArray());
                c = long.TryParse(lx, out var a1) && long.TryParse(ly, out var b1) && a1 != b1
                    ? a1.CompareTo(b1)
                    : string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            }
            if (c != 0) return Math.Sign(c);
        }
        return 0;
    }
}
