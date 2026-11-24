using System.Text.Json.Serialization;
using Saddlebag.Core.Util;

namespace Saddlebag.Core.Routing;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FileKind { Framework, Asi, AsiSupport, DotNetScript, ScriptSupport, LmlPackage, LmlStream, LmlReplace, Root, Data, Docs, Unknown, Skipped }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Confidence { High, Medium, Low }

/// <summary>One file's routing decision. Dest is null for files that are not deployed (docs, skipped) or still need an answer.</summary>
public sealed record RoutedFile(string Source, string? Dest, FileKind Kind, string RuleId, Confidence Confidence, string Reason)
{
    public bool NeedsDecision => Dest is null && Kind is FileKind.Data or FileKind.Unknown;
    public bool Deploys => Dest is not null;
}

public sealed record RoutePlan(string PackageRoot, string PackageType, IReadOnlyList<RoutedFile> Files, IReadOnlyList<string> Variants)
{
    public bool NeedsVariant => Variants.Count > 0;
    public IEnumerable<RoutedFile> Unresolved => Files.Where(f => f.NeedsDecision);
    public bool IsComplete => !NeedsVariant && !Unresolved.Any();
}

/// <summary>Per-mod answers that beat every rule. Saved in mod.json so they survive re-imports.</summary>
public sealed class ModOverrides
{
    /// <summary>Source path → destination path, or null to skip the file.</summary>
    public Dictionary<string, string?> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Variant { get; set; }
}

/// <summary>
/// Detects the package type first, then routes each file. Rule ids match the build plan's routing table,
/// so the preview can always say which rule placed a file.
/// </summary>
public sealed class Router(RuleSet? rules = null)
{
    private readonly RuleSet _r = rules ?? RuleSet.Default;
    public RuleSet Rules => _r;

    private static bool HasExt(string rel, IEnumerable<string> exts) => exts.Any(e => rel.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    private bool IsDoc(string rel) => HasExt(rel, _r.DocExtensions) || RelPath.FileName(rel).StartsWith("readme", StringComparison.OrdinalIgnoreCase) && !rel.Contains('.');
    private bool IsFramework(string rel) => _r.Frameworks.Contains(RelPath.FileName(rel), RelPath.Comparer);

    /// <summary>A managed DLL that references the RDR2 .NET API (by the configured name prefixes) is a .NET script.</summary>
    private bool IsScript(DllInfo d) => d.Kind == DllKind.Managed &&
        d.AssemblyReferences.Any(a => _r.DotNetApiReferencePrefixes.Any(p => a.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
    private static bool IsInstallXml(string rel) => RelPath.FileName(rel).Equals("install.xml", StringComparison.OrdinalIgnoreCase);

    /// <param name="filesRoot">Folder holding the extracted mod in its original layout.</param>
    /// <param name="modName">Used to name the lml/ folder when the package root is the archive root.</param>
    public RoutePlan Route(string filesRoot, string modName, ModOverrides? overrides = null)
    {
        overrides ??= new ModOverrides();
        var all = Directory.EnumerateFiles(filesRoot, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .Select(f => RelPath.From(filesRoot, f))
            .OrderBy(p => p, RelPath.Comparer)
            .ToList();

        // Wrapper stripping: walk down single folders until something real sits at the level.
        var root = "";
        if (overrides.Variant is not null)
        {
            root = RelPath.Normalize(overrides.Variant);
            if (!all.Any(f => RelPath.IsUnder(f, root)))
                throw new InvalidOperationException($"Variant '{overrides.Variant}' is not a folder in this mod.");
        }
        root = StripWrappers(all, root);

        var inRoot = all.Where(f => RelPath.IsUnder(f, root)).ToList();
        var outside = all.Except(inRoot, RelPath.Comparer).ToList();
        var rel = inRoot.Select(f => RelPath.StripPrefix(f, root)).ToList();

        // Rule 9: several option folders and nothing to deploy at this level → ask.
        if (overrides.Variant is null)
        {
            var variants = DetectVariants(rel);
            if (variants.Count >= 2)
                return new RoutePlan(root, "variants", [], variants.Select(v => RelPath.Combine(root, v)).ToList());
        }

        List<RoutedFile> files;
        string type;
        var top = rel.Where(r => r.Contains('/')).Select(RelPath.FirstSegment).Distinct(RelPath.Comparer).ToList();

        if (rel.Any(r => !r.Contains('/') && IsInstallXml(r)))
        {
            type = "lml-package";
            var name = SafeFolderName(root.Length == 0 ? modName : RelPath.FileName(root));
            files = rel.Select(r => new RoutedFile(RelPath.Combine(root, r), $"lml/{name}/{r}", FileKind.LmlPackage, "2", Confidence.High,
                _r.Reason("lml-package"))).ToList();
        }
        else if (top.Any(t => _r.SignatureFolders.Contains(t, RelPath.Comparer)))
        {
            type = "pre-structured";
            files = rel.Select(r => RoutePreStructured(root, r)).ToList();
        }
        else
        {
            type = "loose";
            files = RouteLoose(filesRoot, root, rel);
        }

        // Files outside the chosen variant or package root are not deployed.
        files.AddRange(outside.Select(o => new RoutedFile(o, null, FileKind.Skipped, "root", Confidence.High,
            _r.Reason(overrides.Variant is null ? "outside-root" : "other-variant"))));

        // Overrides beat every rule.
        files = files.Select(f => overrides.Files.TryGetValue(f.Source, out var dest)
            ? dest is null
                ? f with { Dest = null, Kind = FileKind.Skipped, RuleId = "override", Confidence = Confidence.High, Reason = _r.Reason("override-skip") }
                : f with { Dest = RelPath.Normalize(dest), Kind = f.Kind is FileKind.Data or FileKind.Unknown or FileKind.Docs or FileKind.Skipped ? FileKind.Root : f.Kind,
                    RuleId = "override", Confidence = Confidence.High, Reason = _r.Reason("override") }
            : f).ToList();

        foreach (var f in files.Where(f => f.Dest is not null))
            if (!RelPath.IsSafe(f.Dest!)) throw new InvalidOperationException($"Unsafe destination '{f.Dest}' for '{f.Source}'.");

        return new RoutePlan(root, type, files, []);
    }

    private string StripWrappers(List<string> all, string root)
    {
        while (true)
        {
            var under = all.Where(f => RelPath.IsUnder(f, root)).Select(f => RelPath.StripPrefix(f, root)).ToList();
            var looseHere = under.Where(r => !r.Contains('/') && !IsDoc(r)).ToList();
            if (looseHere.Count > 0) return root;
            var dirs = under.Where(r => r.Contains('/')).Select(RelPath.FirstSegment).Distinct(RelPath.Comparer).ToList();
            if (dirs.Count != 1) return root;
            if (_r.StructuralFolders.Contains(dirs[0], RelPath.Comparer)) return root; // a real folder, not a wrapper
            root = RelPath.Combine(root, dirs[0]);
        }
    }

    private List<string> DetectVariants(List<string> rel)
    {
        if (rel.Any(r => !r.Contains('/') && !IsDoc(r))) return [];
        var dirs = rel.Where(r => r.Contains('/')).GroupBy(RelPath.FirstSegment, RelPath.Comparer);
        return dirs.Where(g => !_r.SignatureFolders.Contains(g.Key, RelPath.Comparer) &&
                               g.Any(r => IsInstallXml(r) || r.EndsWith(".asi", StringComparison.OrdinalIgnoreCase) ||
                                          r.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                                          RelPath.StripPrefix(r, g.Key).StartsWith("lml/", StringComparison.OrdinalIgnoreCase)))
                   .Select(g => g.Key).ToList();
    }

    private RoutedFile RoutePreStructured(string root, string r)
    {
        var src = RelPath.Combine(root, r);
        if (!r.Contains('/'))
        {
            if (IsFramework(r)) return new(src, r, FileKind.Framework, "3", Confidence.High, _r.Reason("framework"));
            if (IsDoc(r)) return new(src, null, FileKind.Docs, "10", Confidence.High, _r.Reason("docs"));
            if (r.EndsWith(".asi", StringComparison.OrdinalIgnoreCase)) return new(src, r, FileKind.Asi, "1", Confidence.High, _r.Reason("prestructured"));
        }
        return new(src, r, FileKind.Root, "1", Confidence.High, _r.Reason("prestructured"));
    }

    private List<RoutedFile> RouteLoose(string filesRoot, string root, List<string> rel)
    {
        string Full(string r) => RelPath.ToFull(filesRoot, RelPath.Combine(root, r));

        var asiDirs = rel.Where(r => r.EndsWith(".asi", StringComparison.OrdinalIgnoreCase) && !IsFramework(r))
            .Select(RelPath.Dir).Distinct(RelPath.Comparer).ToList();
        var dlls = rel.Where(r => r.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !IsFramework(r))
            .ToDictionary(r => r, r => PeInspector.Inspect(Full(r)), RelPath.Comparer);
        var scriptDirs = dlls.Where(d => IsScript(d.Value)).Select(d => RelPath.Dir(d.Key)).Distinct(RelPath.Comparer).ToList();

        var result = new List<RoutedFile>();
        foreach (var r in rel)
        {
            var src = RelPath.Combine(root, r);
            var name = RelPath.FileName(r);
            var segs = r.Split('/');
            var replaceIdx = Array.FindIndex(segs, s => s.Equals("replace", StringComparison.OrdinalIgnoreCase));

            if (replaceIdx >= 0 && replaceIdx < segs.Length - 1)
            {
                var gamePath = string.Join('/', segs[(replaceIdx + 1)..]);
                result.Add(new(src, $"lml/replace/{gamePath}", FileKind.LmlReplace, "7", Confidence.Medium, _r.Reason("replace")));
            }
            else if (IsFramework(r))
                result.Add(new(src, name, FileKind.Framework, "3", Confidence.High, _r.Reason("framework")));
            else if (IsDoc(r))
                result.Add(new(src, null, FileKind.Docs, "10", Confidence.High, _r.Reason("docs")));
            else if (HasExt(r, _r.StreamExtensions))
                result.Add(new(src, $"lml/stream/{name}", FileKind.LmlStream, "6", Confidence.Medium, _r.Reason("stream")));
            else if (r.EndsWith(".asi", StringComparison.OrdinalIgnoreCase))
                result.Add(new(src, name, FileKind.Asi, "4", Confidence.High, _r.Reason("asi")));
            else if (OwningDir(asiDirs, r) is { } ad && !(dlls.TryGetValue(r, out var di0) && IsScript(di0)))
                result.Add(new(src, RelPath.StripPrefix(r, ad), FileKind.AsiSupport, "4", Confidence.High, _r.Reason("asi-sibling")));
            else if (dlls.TryGetValue(r, out var di) && IsScript(di))
                result.Add(new(src, $"scripts/{name}", FileKind.DotNetScript, "5", Confidence.High, _r.Reason("dotnet-script")));
            else if (OwningDir(scriptDirs, r) is { } sd && !HasExt(r, [".ymt", ".meta"]))
                result.Add(new(src, "scripts/" + RelPath.StripPrefix(r, sd), FileKind.ScriptSupport, "5", Confidence.Medium, _r.Reason("dotnet-sibling")));
            else if (HasExt(r, _r.DataExtensions))
                result.Add(new(src, null, FileKind.Data, "8", Confidence.Low, _r.Reason("data")));
            else
                result.Add(new(src, null, FileKind.Unknown, "11", Confidence.Low,
                    dlls.TryGetValue(r, out var d2) ? $"{d2.Kind.ToString().ToLowerInvariant()} DLL with no known role" : _r.Reason("unknown")));
        }
        return result;
    }

    /// <summary>The deepest folder in <paramref name="dirs"/> that contains <paramref name="r"/>, or null.</summary>
    private static string? OwningDir(List<string> dirs, string r) =>
        dirs.Where(d => RelPath.IsUnder(r, d)).OrderByDescending(d => d.Length).FirstOrDefault();

    public static string SafeFolderName(string name)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var s = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        return s.Length == 0 ? "mod" : s;
    }
}
