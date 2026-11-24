using System.Diagnostics;
using Saddlebag.Core.Routing;
using Saddlebag.Core.Util;

namespace Saddlebag.Core.Deploy;

public enum Severity { Info, Warn, Block }

/// <summary>One result from the pre-deploy checks. Id is stable so tests and the GUI can key on it.</summary>
public sealed record CheckFinding(string Id, Severity Severity, string Message, string? Hint = null);

/// <summary>A file a mod wants in the game folder: the input to every check.</summary>
public sealed record Candidate(string Dest, string ModId, string ModName, string LibraryPath, string Source, string Sha256, bool Linked, int Priority);

/// <summary>
/// The checks from the build plan's conflict table. They run on every plan, so install time and deploy time
/// see the same answers. Pure functions over the candidate list plus what's on disk.
/// </summary>
public static class DeployChecks
{
    public const string SameDest = "same-dest";
    public const string StreamName = "stream-name";
    public const string SharedSlot = "shared-slot";
    public const string TwoAsiLoaders = "two-asi-loaders";
    public const string MissingLml = "missing-dep-lml";
    public const string MissingAsiLoader = "missing-dep-asi-loader";
    public const string MissingScriptHook = "missing-dep-scripthook";
    public const string MissingDotNetHook = "missing-dep-dotnet-hook";
    public const string DotNetMismatch = "dotnet-version-mismatch";
    public const string GameUpdated = "game-updated";
    public const string ReplaceTarget = "replace-target";
    public const string BrokenInstallXml = "broken-install-xml";


    /// <summary>Reads a PE's file version. Swappable so tests don't need signed binaries with version resources.</summary>
    public static Func<string, string?> FileVersion { get; set; } = path =>
    {
        try { var v = FileVersionInfo.GetVersionInfo(path).FileVersion; return string.IsNullOrWhiteSpace(v) ? null : v.Trim(); }
        catch { return null; }
    };

    public static bool IsSharedSlot(string dest, RuleSet? rules = null) => !dest.Contains('/') && (rules ?? RuleSet.Default).Frameworks.Contains(dest, RelPath.Comparer);

    /// <summary>
    /// Picks the file that lands at each destination. Shared loader slots go to the newest file version (so a mod that
    /// bundles an old dinput8.dll can't downgrade yours); everything else goes to the highest priority.
    /// </summary>
    public static (Dictionary<string, Candidate> Winners, List<CheckFinding> Findings) Resolve(IEnumerable<Candidate> candidates, RuleSet? rules = null)
    {
        var winners = new Dictionary<string, Candidate>(RelPath.Comparer);
        var findings = new List<CheckFinding>();
        foreach (var group in candidates.GroupBy(c => c.Dest, RelPath.Comparer))
        {
            var list = group.OrderBy(c => c.Priority).ToList();
            var distinct = list.Select(c => c.Sha256).Distinct().Count();
            if (IsSharedSlot(group.Key, rules) && list.Count > 1)
            {
                var ranked = list.Select(c => (c, v: ParseVersion(FileVersion(c.Source)))).OrderBy(x => x.v).ThenBy(x => x.c.Priority).ToList();
                var win = ranked[^1].c;
                winners[group.Key] = win;
                var others = list.Where(c => c.ModId != win.ModId).Select(c => c.ModName).Distinct().ToList();
                if (distinct > 1)
                    findings.Add(new(SharedSlot, Severity.Info,
                        $"{group.Key} is shared: using {win.ModName}'s copy{VersionSuffix(win)}; bundled copies in {string.Join(", ", others)} are skipped."));
                continue;
            }
            winners[group.Key] = list[^1];
            if (distinct > 1)
                findings.Add(new(SameDest, Severity.Warn,
                    $"{group.Key}: {list[^1].ModName} wins over {string.Join(", ", list.Take(list.Count - 1).Select(c => c.ModName).Distinct())}.",
                    "Change who wins with `saddlebag order <mod> <position>`; the loser stays in the library."));
        }
        return (winners, findings);
    }

    private static string VersionSuffix(Candidate c) => FileVersion(c.Source) is { } v ? $" (v{v})" : "";

    private static Version ParseVersion(string? v)
    {
        if (v is null) return new Version(0, 0);
        var clean = new string(v.TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray()).Trim('.');
        return Version.TryParse(clean.Contains('.') ? clean : clean + ".0", out var parsed) ? parsed : new Version(0, 0);
    }

    /// <summary>Checks on the final layout: what will be in the game folder after this deploy.</summary>
    /// <param name="winners">Files mods will place.</param>
    /// <param name="untrackedOnDisk">Game-relative files present but not in the ledger and not in the baseline (e.g. a loader you installed by hand).</param>
    public static List<CheckFinding> Layout(IReadOnlyDictionary<string, Candidate> winners, ISet<string> untrackedOnDisk, bool gameUpdated, RuleSet? rules = null)
    {
        var asiLoaders = (rules ?? RuleSet.Default).AsiLoaders;
        var findings = new List<CheckFinding>();
        bool Has(string rootFile) => winners.ContainsKey(rootFile) || untrackedOnDisk.Contains(rootFile);
        string Who(Func<Candidate, bool> pred) => string.Join(", ", winners.Values.Where(pred).Select(c => c.ModName).Distinct());

        // LML streams by file name, so the same name in two folders still collides.
        var streams = winners.Values
            .Where(c => RelPath.FirstSegment(c.Dest).Equals("lml", StringComparison.OrdinalIgnoreCase) &&
                        c.Dest.Split('/').SkipLast(1).Any(s => s.Equals("stream", StringComparison.OrdinalIgnoreCase)))
            .GroupBy(c => RelPath.FileName(c.Dest), RelPath.Comparer)
            .Where(g => g.Select(c => c.Sha256).Distinct().Count() > 1);
        foreach (var g in streams)
        {
            var top = g.OrderBy(c => c.Priority).Last();
            findings.Add(new(StreamName, Severity.Warn,
                $"{g.Key} is streamed by {string.Join(", ", g.Select(c => c.ModName).Distinct())} from different folders; LML loads only one.",
                $"Saddlebag ranks {top.ModName} highest. Disable or `route --skip` the copy you don't want."));
        }

        var asis = winners.Values.Where(c => !c.Dest.Contains('/') && c.Dest.EndsWith(".asi", StringComparison.OrdinalIgnoreCase)).ToList();
        var usesLml = winners.Values.Any(c => RelPath.FirstSegment(c.Dest).Equals("lml", StringComparison.OrdinalIgnoreCase));
        var scripts = winners.Values.Where(c => RelPath.FirstSegment(c.Dest).Equals("scripts", StringComparison.OrdinalIgnoreCase) &&
                                                c.Dest.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList();

        if (Has("dinput8.dll") && Has("version.dll"))
            findings.Add(new(TwoAsiLoaders, Severity.Warn, "Two ASI loaders: dinput8.dll and the older version.dll loader.",
                "Keep dinput8.dll. Skip version.dll with `saddlebag route <mod> --skip version.dll`, or delete it if you put it there by hand."));

        if (usesLml && !Has("vfs.asi"))
            findings.Add(new(MissingLml, Severity.Block, $"{Who(c => RelPath.FirstSegment(c.Dest).Equals("lml", StringComparison.OrdinalIgnoreCase))} need Lenny's Mod Loader (vfs.asi), which isn't installed or enabled.",
                "Install LML as a mod: `saddlebag install <LML archive>`."));

        var needsHook = asis.Count > 0 || usesLml || scripts.Count > 0;
        if ((asis.Count > 0 || usesLml) && !asiLoaders.Any(Has))
            findings.Add(new(MissingAsiLoader, Severity.Block, $"{(asis.Count > 0 ? Who(c => asis.Contains(c)) : "LML")} need an ASI loader (dinput8.dll), which isn't installed or enabled.",
                "Install Script Hook RDR2 (it ships dinput8.dll) as a mod."));
        if (needsHook && !Has("ScriptHookRDR2.dll"))
            findings.Add(new(MissingScriptHook, Severity.Block, "ASI plugins, LML and .NET scripts need ScriptHookRDR2.dll, which isn't installed or enabled.",
                "Install Script Hook RDR2 as a mod."));
        if (scripts.Count > 0 && !Has("ScriptHookRDRDotNet.asi"))
            findings.Add(new(MissingDotNetHook, Severity.Block, $"{Who(c => scripts.Contains(c))} need the .NET script hook (ScriptHookRDRDotNet.asi), which isn't installed or enabled.",
                "Install ScriptHookRDR2 .NET as a mod."));

        if (winners.TryGetValue("ScriptHookRDRDotNet.asi", out var hook) && winners.TryGetValue("ScriptHookRDRNetAPI.dll", out var api) &&
            FileVersion(hook.Source) is { } hv && FileVersion(api.Source) is { } av && hv != av)
            findings.Add(new(DotNetMismatch, Severity.Warn,
                $"ScriptHookRDRDotNet.asi is v{hv} ({hook.ModName}) but ScriptHookRDRNetAPI.dll is v{av} ({api.ModName}); they must come from the same release.",
                "Disable the mod that bundles the stray copy, or install both files from one release."));

        // install.xml packages: what each declares, checked against the files actually deployed with it.
        var packages = winners.Values
            .Where(c => c.Dest.Split('/') is [var l, _, var f] && l.Equals("lml", StringComparison.OrdinalIgnoreCase) && f.Equals("install.xml", StringComparison.OrdinalIgnoreCase))
            .Select(c => (Candidate: c, Folder: RelPath.Dir(c.Dest), Info: InstallXml.Read(c.Source)))
            .ToList();
        foreach (var (c, folder, info) in packages)
        {
            if (info is null)
            {
                findings.Add(new(BrokenInstallXml, Severity.Warn, $"{c.ModName}: {c.Dest} isn't valid XML, so LML may skip the whole package."));
                continue;
            }
            var missing = info.Replacements.Where(r => r.FilePath.Length > 0 && !winners.ContainsKey(RelPath.Combine(folder, r.FilePath))).Select(r => r.FilePath)
                .Concat(info.StreamingFolders.Where(sf => !winners.Keys.Any(k => RelPath.IsUnder(k, RelPath.Combine(folder, sf)))).Select(sf => sf + "/"))
                .ToList();
            if (missing.Count > 0)
                findings.Add(new(BrokenInstallXml, Severity.Warn,
                    $"{c.ModName}: install.xml points at {string.Join(", ", missing.Take(3))}{(missing.Count > 3 ? $" and {missing.Count - 3} more" : "")}, which aren't in the package.",
                    "The archive may be missing files, or a file in it was skipped. Check `saddlebag route <mod>`."));
        }
        var targets = packages.Where(p => p.Info is not null)
            .SelectMany(p => p.Info!.Replacements.Select(r => (r.GamePath, p.Candidate.ModName)))
            .GroupBy(x => x.GamePath, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.ModName).Distinct().Count() > 1);
        foreach (var g in targets)
            findings.Add(new(ReplaceTarget, Severity.Warn,
                $"{g.Key} is replaced by {string.Join(" and ", g.Select(x => x.ModName).Distinct())}; only one takes effect.",
                "LML's own load order decides which (its Mod Manager). Saddlebag's priority doesn't apply inside LML."));

        if (gameUpdated)
            findings.Add(new(GameUpdated, Severity.Warn, "RDR2.exe changed since the baseline: the game updated. Hook-based mods may crash until they're updated.",
                "Purge, verify files in your launcher, then `saddlebag baseline capture`."));

        return findings;
    }
}
