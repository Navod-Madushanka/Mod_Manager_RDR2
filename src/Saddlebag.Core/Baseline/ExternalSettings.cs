using System.Text.Json;
using Saddlebag.Core.State;
using Saddlebag.Core.Util;

namespace Saddlebag.Core.Baseline;

/// <summary>A settings file outside the game folder, as captured with the baseline.</summary>
/// <param name="Display">Shown to you, e.g. "Documents/Rockstar Games/Red Dead Redemption 2/Settings/system.xml".</param>
/// <param name="RelToDocuments">Path under the Documents folder.</param>
public sealed record ExternalRecord(string Display, string RelToDocuments, long Size, long MTime, string Sha256);

/// <summary>
/// The game's own settings files in Documents (graphics, VR). A VR mod can rewrite system.xml, so the baseline keeps a
/// copy and verify says when it changed. A change doesn't turn Online red: you also change these by adjusting settings in
/// game, and they don't modify the game itself. Restore puts the baseline copy back, saving the current one first.
/// </summary>
public sealed class ExternalSettings(StateStore store, string home)
{
    public const string DocumentsOverrideKey = "documents_dir";
    private const string RecordsKey = "external_baseline";

    /// <summary>Both places players report RDR2's system.xml; whichever exist are captured.</summary>
    public static readonly string[] Candidates =
    [
        "Rockstar Games/Red Dead Redemption 2/Settings/system.xml",
        "Rockstar Games/Red Dead Redemption 2/system.xml",
    ];

    public string CopiesDir => Path.Combine(home, "baseline-external");
    public string RescuedDir => Path.Combine(home, "rescued");

    /// <summary>Windows' real Documents folder (follows OneDrive redirection). Overridable for tests.</summary>
    public string DocumentsDir =>
        store.GetSetting(DocumentsOverrideKey) is { Length: > 0 } d ? d : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    private static string Display(string rel) => "Documents/" + rel;
    private string Full(string rel) => RelPath.ToFull(DocumentsDir, rel);
    private string Copy(string rel) => RelPath.ToFull(CopiesDir, rel);

    public List<ExternalRecord> Records() =>
        store.GetSetting(RecordsKey) is { Length: > 0 } json ? JsonSerializer.Deserialize<List<ExternalRecord>>(json) ?? [] : [];

    /// <summary>Snapshots whichever candidates exist. Replaces the previous snapshot.</summary>
    public List<ExternalRecord> Capture()
    {
        if (Directory.Exists(CopiesDir)) Directory.Delete(CopiesDir, true);
        var records = new List<ExternalRecord>();
        if (string.IsNullOrEmpty(DocumentsDir)) { Save(records); return records; }
        foreach (var rel in Candidates)
        {
            var full = Full(rel);
            if (!File.Exists(full)) continue;
            var fi = new FileInfo(full);
            var copy = Copy(rel);
            FileOps.EnsureParent(copy);
            File.Copy(full, copy, overwrite: true);
            records.Add(new ExternalRecord(Display(rel), rel, fi.Length, fi.LastWriteTimeUtc.Ticks, FileOps.Sha256(copy)));
        }
        Save(records);
        return records;
    }

    private void Save(List<ExternalRecord> records) => store.SetSetting(RecordsKey, JsonSerializer.Serialize(records));

    /// <summary>Settings files that differ from the baseline copy, or are gone.</summary>
    public List<Finding> Check(bool deep = false)
    {
        var findings = new List<Finding>();
        foreach (var r in Records())
        {
            var full = Full(r.RelToDocuments);
            if (!File.Exists(full))
            {
                findings.Add(new(FindingKind.SettingsChanged, r.Display, "deleted since the baseline; the game recreates it with defaults"));
                continue;
            }
            var fi = new FileInfo(full);
            var same = fi.Length == r.Size && (!deep && fi.LastWriteTimeUtc.Ticks == r.MTime || FileOps.Sha256(full) == r.Sha256);
            if (!same)
                findings.Add(new(FindingKind.SettingsChanged, r.Display,
                    "changed since the baseline (a VR or graphics mod, or settings changed in game). Doesn't affect Online; restore it if a mod changed it"));
        }
        return findings;
    }

    /// <summary>Puts the baseline copy back. The current file is saved under rescued/ first. Returns where it was saved, if anywhere.</summary>
    public string? Restore(string display)
    {
        var r = Records().FirstOrDefault(x => x.Display.Equals(display, StringComparison.OrdinalIgnoreCase) ||
                                              x.RelToDocuments.Equals(RelPath.Normalize(display), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"'{display}' isn't a settings file in the baseline.");
        var copy = Copy(r.RelToDocuments);
        if (!File.Exists(copy)) throw new InvalidOperationException("The baseline copy is missing. Re-capture the baseline.");
        var full = Full(r.RelToDocuments);
        string? saved = null;
        if (File.Exists(full))
        {
            saved = RelPath.ToFull(Path.Combine(RescuedDir, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")), "Documents/" + r.RelToDocuments);
            FileOps.EnsureParent(saved);
            File.Copy(full, saved, overwrite: true);
        }
        FileOps.EnsureParent(full);
        File.Copy(copy, full + ".saddlebag-tmp", overwrite: true);
        File.Move(full + ".saddlebag-tmp", full, overwrite: true);
        File.SetLastWriteTimeUtc(full, new DateTime(r.MTime, DateTimeKind.Utc)); // so the quick check sees it as unchanged
        return saved;
    }
}
