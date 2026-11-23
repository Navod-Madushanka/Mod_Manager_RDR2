using Saddlebag.Core.Util;

namespace Saddlebag.Core.Routing;

/// <summary>Per-mod choice to package a loose mod as an LML install.xml mod. Saved in mod.json.</summary>
public sealed class LmlPackOptions
{
    public bool Enabled { get; set; }
    /// <summary>Folder under lml/; defaults to the mod name.</summary>
    public string? Folder { get; set; }
    public string? Author { get; set; }
    /// <summary>Source file → game path such as "update:/x64/data/metadata/cameras.ymt".</summary>
    public Dictionary<string, string> GamePaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record PackResult(RoutePlan Plan, string InstallXml, IReadOnlyList<string> Notes);

/// <summary>
/// Turns a loose mod's routing into an LML package: stream assets move to lml/&lt;Folder&gt;/stream/, files you gave a
/// game path become &lt;FileReplacement&gt; entries, and a generated install.xml describes it, so LML's Mod Manager lists it.
/// </summary>
public static class LmlPackager
{
    /// <summary>Library path of the generated install.xml. It lives in the mod's library folder, never in your files.</summary>
    public const string GeneratedInstallXml = "@generated/install.xml";
    public const string RuleId = "lml-pack";

    public static PackResult Apply(RoutePlan plan, LmlPackOptions opt, string modName, string? version, string? link)
    {
        if (plan.NeedsVariant) throw new InvalidOperationException("Pick a variant before packaging this mod for LML.");
        if (plan.PackageType == "lml-package") throw new InvalidOperationException("This mod already ships its own install.xml.");

        var folder = Router.SafeFolderName(opt.Folder ?? modName);
        var pkg = $"lml/{folder}";
        var notes = new List<string>();

        foreach (var key in opt.GamePaths.Keys)
            if (!plan.Files.Any(f => f.Source.Equals(key, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"'{key}' isn't a file in this mod.");
        foreach (var (src, gp) in opt.GamePaths)
            if (!InstallXml.IsGamePath(gp))
                throw new InvalidOperationException($"'{gp}' (for {src}) isn't a game path. Use the mount form, e.g. update:/x64/data/metadata/cameras.ymt");

        var replacements = new List<FileReplacement>();
        var hasStream = false;
        var files = plan.Files.Select(f =>
        {
            if (opt.GamePaths.TryGetValue(f.Source, out var gamePath))
            {
                // update:/x64/data/a.ymt is stored in the package as replace/update/x64/data/a.ymt
                var mount = gamePath[..gamePath.IndexOf(':')];
                var inPkg = $"replace/{mount}/{RelPath.Normalize(gamePath[(gamePath.IndexOf(":/", StringComparison.Ordinal) + 2)..])}";
                if (!RelPath.IsSafe(inPkg)) throw new InvalidOperationException($"'{gamePath}' isn't a safe game path.");
                replacements.Add(new FileReplacement(gamePath, inPkg));
                return f with { Dest = $"{pkg}/{inPkg}", Kind = FileKind.LmlPackage, RuleId = RuleId, Confidence = Confidence.High,
                    Reason = $"declared in the generated install.xml as {gamePath}" };
            }
            if (f.Kind == FileKind.LmlStream && f.Dest is not null)
            {
                hasStream = true;
                return f with { Dest = $"{pkg}/stream/{RelPath.FileName(f.Dest)}", Kind = FileKind.LmlPackage, RuleId = RuleId,
                    Reason = "streaming asset, listed in the generated install.xml" };
            }
            return f;
        }).ToList();

        if (!hasStream && replacements.Count == 0)
            throw new InvalidOperationException("Nothing to package: the mod has no loose stream assets, and no files were given a game path (--map SRC=update:/...).");

        var dupes = files.Where(f => f.RuleId == RuleId && f.Dest is not null).GroupBy(f => f.Dest!, RelPath.Comparer).FirstOrDefault(g => g.Count() > 1);
        if (dupes is not null)
            throw new InvalidOperationException($"Two files would land at {dupes.Key}: {string.Join(", ", dupes.Select(d => d.Source))}. Skip one with `saddlebag route <mod> --skip <src>`.");

        var legacy = files.Count(f => f.Kind == FileKind.LmlReplace);
        if (legacy > 0)
            notes.Add($"{legacy} file(s) stay in lml/replace/ (LML's legacy folder): their game paths can't be worked out safely. Package them with --map SRC=update:/<path>.");

        var xml = InstallXml.Generate(modName, opt.Author, version, link is not null && link.StartsWith("http") ? link : null,
            "Packaged by Saddlebag", hasStream ? "stream" : null, replacements);
        files.Add(new RoutedFile(GeneratedInstallXml, $"{pkg}/install.xml", FileKind.LmlPackage, RuleId, Confidence.High,
            "generated so LML's Mod Manager lists this mod"));
        return new PackResult(plan with { PackageType = "lml-pack", Files = files }, xml, notes);
    }
}
