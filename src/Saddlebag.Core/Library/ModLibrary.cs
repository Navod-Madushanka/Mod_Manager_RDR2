using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpCompress.Archives;
using Saddlebag.Core.Routing;
using Saddlebag.Core.State;
using Saddlebag.Core.Util;

namespace Saddlebag.Core.Library;

public sealed record ImportResult(ModRecord Mod, RoutePlan Plan, bool AlreadyImported);

/// <summary>What mod.json holds: metadata, your overrides, and the routing decisions for transparency.</summary>
public sealed class ModManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public string? SourceUrl { get; set; }
    public string? LinkedPath { get; set; }
    public ModOverrides Overrides { get; set; } = new();
    public string? PackageType { get; set; }
    public List<RoutedFile> Decisions { get; set; } = [];
}

/// <summary>
/// The library is where mods live: library/&lt;mod-id&gt;/files holds the extracted mod in its original layout.
/// The game folder only ever receives links or copies of these files.
/// </summary>
public sealed class ModLibrary(StateStore store, string home, Router router)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly string[] ArchiveExts = [".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".xz", ".bz2"];

    public string LibraryDir => Path.Combine(home, "library");
    public string ModDir(string id) => Path.Combine(LibraryDir, id);
    public string FilesRoot(ModRecord m) => m.LinkedPath ?? Path.Combine(ModDir(m.Id), "files");

    public ModRecord Require(string idOrName)
    {
        var mods = store.GetMods();
        return mods.FirstOrDefault(m => m.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase))
               ?? mods.SingleOrDefault(m => m.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"No mod with id or name '{idOrName}'. Run `saddlebag list`.");
    }

    public ImportResult Import(string source, string? name = null, string? version = null, string? url = null)
    {
        source = Path.GetFullPath(source);
        string sha;
        bool isDir = Directory.Exists(source);
        if (isDir) sha = FolderFingerprint(source);
        else if (File.Exists(source)) sha = FileOps.Sha256(source);
        else throw new FileNotFoundException($"'{source}' is not a file or folder.");

        if (store.FindModByArchive(sha) is { } existing)
            return new ImportResult(existing, Reroute(existing.Id), AlreadyImported: true);

        name ??= ArchiveBaseName(source);
        var id = $"{Slug(name)}-{sha[..8]}";
        var dir = ModDir(id);
        var staging = Path.Combine(dir, "files.partial");
        var files = Path.Combine(dir, "files");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        if (Directory.Exists(files)) Directory.Delete(files, true);
        Directory.CreateDirectory(staging);

        if (isDir) CopyTree(source, staging);
        else if (ArchiveExts.Any(e => source.EndsWith(e, StringComparison.OrdinalIgnoreCase))) Extract(source, staging);
        else File.Copy(source, Path.Combine(staging, Path.GetFileName(source))); // a single loose file, e.g. an .asi

        Directory.Move(staging, files);
        var mod = new ModRecord(id, name, version, url, sha, DateTime.UtcNow, null);
        store.UpsertMod(mod);
        return new ImportResult(mod, Reroute(id), AlreadyImported: false);
    }

    /// <summary>Registers a folder you build into (e.g. Visual Studio output) as a mod. Nothing is copied into the library.</summary>
    public ImportResult Link(string folder, string name)
    {
        folder = Path.GetFullPath(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        var id = "dev-" + Slug(name);
        var existing = store.GetMod(id);
        var mod = new ModRecord(id, name, "dev", null, null, existing?.InstalledAt ?? DateTime.UtcNow, folder);
        Directory.CreateDirectory(ModDir(id));
        store.UpsertMod(mod);
        return new ImportResult(mod, Reroute(id), existing is not null);
    }

    /// <summary>
    /// Turns files already sitting in the game folder into a tracked mod: they move into the library (same volume, so
    /// it's a rename) and are routed 1:1 back to where they were. Nothing is deleted; on failure every file moves back.
    /// </summary>
    public ImportResult Adopt(string gameDir, IReadOnlyList<string> relFiles, string name)
    {
        if (relFiles.Count == 0) throw new InvalidOperationException("Nothing to adopt.");
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', relFiles.Order(StringComparer.OrdinalIgnoreCase)))));
        var id = $"adopted-{Slug(name)}-{fingerprint[..8]}";
        if (store.GetMod(id) is not null) throw new InvalidOperationException($"These files were already adopted as {id}.");
        var dir = ModDir(id);
        var staging = Path.Combine(dir, "files.partial");
        Directory.CreateDirectory(staging);

        var moved = new List<(string From, string To)>();
        try
        {
            foreach (var rel in relFiles)
            {
                var from = RelPath.ToFull(gameDir, rel);
                var to = RelPath.ToFull(staging, rel);
                FileOps.EnsureParent(to);
                File.Move(from, to);
                moved.Add((from, to));
            }
        }
        catch
        {
            foreach (var (from, to) in moved) { FileOps.EnsureParent(from); File.Move(to, from); }
            Directory.Delete(dir, true);
            throw;
        }
        Directory.Move(staging, Path.Combine(dir, "files"));

        var mod = new ModRecord(id, name, null, "adopted from the game folder", null, DateTime.UtcNow, null);
        store.UpsertMod(mod);
        var manifest = new ModManifest { Id = id, Name = name };
        foreach (var rel in relFiles) manifest.Overrides.Files[rel] = rel;
        SaveManifest(manifest);
        return new ImportResult(mod, Reroute(id), AlreadyImported: false);
    }

    public void Remove(string id)
    {
        if (store.GetDeployed().Any(d => d.ModId == id))
            throw new InvalidOperationException("This mod still has files in the game folder. Disable it and run `deploy` (or `purge`) first.");
        store.DeleteMod(id);
        if (Directory.Exists(ModDir(id))) Directory.Delete(ModDir(id), true);
    }

    public ModManifest LoadManifest(string id)
    {
        var p = Path.Combine(ModDir(id), "mod.json");
        if (!File.Exists(p)) return new ModManifest { Id = id };
        var m = JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(p), Json) ?? new ModManifest { Id = id };
        // Restore case-insensitive keys after deserialization.
        m.Overrides.Files = new Dictionary<string, string?>(m.Overrides.Files, StringComparer.OrdinalIgnoreCase);
        return m;
    }

    private void SaveManifest(ModManifest m)
    {
        Directory.CreateDirectory(ModDir(m.Id));
        var p = Path.Combine(ModDir(m.Id), "mod.json");
        File.WriteAllText(p + ".tmp", JsonSerializer.Serialize(m, Json));
        File.Move(p + ".tmp", p, overwrite: true);
    }

    public RoutePlan SetOverride(string id, string source, string? dest)
    {
        var m = LoadManifest(id);
        m.Overrides.Files[RelPath.Normalize(source)] = dest is null ? null : RelPath.Normalize(dest);
        SaveManifest(m);
        return Reroute(id);
    }

    public RoutePlan ClearOverride(string id, string source)
    {
        var m = LoadManifest(id);
        m.Overrides.Files.Remove(RelPath.Normalize(source));
        SaveManifest(m);
        return Reroute(id);
    }

    public RoutePlan SetVariant(string id, string? variant)
    {
        var m = LoadManifest(id);
        m.Overrides.Variant = variant is null ? null : RelPath.Normalize(variant);
        SaveManifest(m);
        return Reroute(id);
    }

    /// <summary>Runs the router again (after an override, or to pick up a fresh dev build) and stores the decisions.</summary>
    public RoutePlan Reroute(string id)
    {
        var mod = store.GetMod(id) ?? throw new InvalidOperationException($"Unknown mod '{id}'.");
        var manifest = LoadManifest(id);
        var root = FilesRoot(mod);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Files for '{mod.Name}' are missing: {root}");
        var plan = router.Route(root, mod.Name, manifest.Overrides);

        var rows = plan.Files.Select(f =>
        {
            var full = RelPath.ToFull(root, f.Source);
            var size = new FileInfo(full).Length;
            var sha = f.Deploys ? FileOps.Sha256(full) : "";
            return new ModFileRecord(id, f.Source, f.Dest, sha, size, f.Kind.ToString(), f.RuleId, f.Confidence.ToString());
        }).ToList();
        store.ReplaceModFiles(id, rows);

        manifest.Id = id;
        manifest.Name = mod.Name;
        manifest.Version = mod.Version;
        manifest.SourceUrl = mod.SourceUrl;
        manifest.LinkedPath = mod.LinkedPath;
        manifest.PackageType = plan.PackageType;
        manifest.Decisions = plan.Files.ToList();
        SaveManifest(manifest);
        return plan;
    }

    // ---------- helpers ----------

    internal static void Extract(string archivePath, string dest)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        // Solid archives (most 7z and some RAR) must be read front to back; zip entries can be opened one by one.
        if (archive.IsSolid || archive.Type == SharpCompress.Common.ArchiveType.SevenZip)
        {
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory || reader.Entry.Key is null) continue;
                using var es = reader.OpenEntryStream();
                WriteEntry(dest, reader.Entry.Key, es);
            }
        }
        else
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory || entry.Key is null) continue;
                using var es = entry.OpenEntryStream();
                WriteEntry(dest, entry.Key, es);
            }
        }
    }

    private static void WriteEntry(string dest, string key, Stream content)
    {
        // Zip-slip guard: an entry like "../../RDR2.exe" must never land outside the staging folder.
        if (!RelPath.IsSafe(key)) throw new InvalidDataException($"Archive entry '{key}' points outside the mod folder.");
        var full = RelPath.ToFull(dest, RelPath.Normalize(key));
        FileOps.EnsureParent(full);
        using var fs = File.Create(full);
        content.CopyTo(fs);
    }

    private static void CopyTree(string src, string dest)
    {
        foreach (var f in Directory.EnumerateFiles(src, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            var target = Path.Combine(dest, Path.GetRelativePath(src, f));
            FileOps.EnsureParent(target);
            File.Copy(f, target);
        }
    }

    private static string FolderFingerprint(string dir)
    {
        var sb = new StringBuilder();
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            sb.Append(RelPath.From(dir, f)).Append('\0').Append(FileOps.Sha256(f)).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string ArchiveBaseName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) return name[..^7];
        return Directory.Exists(path) ? name : Path.GetFileNameWithoutExtension(name);
    }

    public static string Slug(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        var slug = string.Join('-', sb.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "mod" : slug[..Math.Min(slug.Length, 40)];
    }
}
