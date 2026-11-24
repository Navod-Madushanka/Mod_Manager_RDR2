using System.ComponentModel;
using Saddlebag.Core;
using Saddlebag.Core.Baseline;
using Saddlebag.Core.Deploy;
using Saddlebag.Core.Routing;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Saddlebag.Cli;

public static class ExitCodes
{
    public const int Ok = 0;
    public const int Amber = 1;
    public const int Red = 2;
    public const int Error = 3;
}

public class HomeSettings : CommandSettings
{
    [CommandOption("--home <DIR>")]
    [Description("Saddlebag folder (library, backups, state.db). Defaults to $SADDLEBAG_HOME, then the folder from the last `init`.")]
    public string? Home { get; set; }
}

public static class Ctx
{
    public static string ResolveHome(string? explicitHome) =>
        HomeLocator.TryResolve(explicitHome)
        ?? throw new InvalidOperationException("No Saddlebag folder yet. Run `saddlebag init --game <RDR2 folder> --home <folder on the same drive>`.");

    public static void RememberHome(string home) => HomeLocator.Remember(home);

    public static Workspace Open(HomeSettings s)
    {
        var ws = Workspace.Open(ResolveHome(s.Home));
        var recovered = ws.Store.GetPendingOps().Count;
        if (recovered > 0)
        {
            AnsiConsole.MarkupLine("[yellow]An earlier operation didn't finish. Finishing it now…[/]");
            ws.Prepare();
            AnsiConsole.MarkupLine("[green]Recovered.[/]");
        }
        return ws;
    }
}

public static class Show
{
    public static string Color(Confidence c) => c switch { Confidence.High => "green", Confidence.Medium => "yellow", _ => "red" };

    public static void Routing(RoutePlan plan, string modName)
    {
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(modName)}[/]  type: [blue]{plan.PackageType}[/]" +
                               (plan.PackageRoot.Length > 0 ? $"  root: {Markup.Escape(plan.PackageRoot)}/" : ""));
        if (plan.NeedsVariant)
        {
            AnsiConsole.MarkupLine("[yellow]This archive has several options. Pick one:[/]");
            foreach (var v in plan.Variants) AnsiConsole.MarkupLine($"  • {Markup.Escape(v)}");
            return;
        }
        var t = new Table().Border(TableBorder.Rounded).AddColumns("Source", "Destination", "Rule", "Confidence", "Why");
        foreach (var f in plan.Files.Where(f => f.Kind != FileKind.Skipped || f.RuleId == "override"))
            t.AddRow(
                Markup.Escape(f.Source),
                f.Dest is null ? (f.NeedsDecision ? "[red]? needs an answer[/]" : "[grey]not deployed[/]") : Markup.Escape(f.Dest),
                f.RuleId,
                $"[{Color(f.Confidence)}]{f.Confidence}[/]",
                $"[grey]{Markup.Escape(f.Reason)}[/]");
        AnsiConsole.Write(t);
        var skipped = plan.Files.Count(f => f.Kind == FileKind.Skipped && f.RuleId != "override");
        if (skipped > 0) AnsiConsole.MarkupLine($"[grey]{skipped} file(s) outside the package root or chosen variant aren't shown.[/]");
    }

    public static void Plan(DeployPlan plan)
    {
        foreach (var c in plan.Checks.OrderByDescending(c => c.Severity))
        {
            var tag = c.Severity switch { Severity.Block => "[red]Blocked[/]", Severity.Warn => "[yellow]Warning[/]", _ => "[blue]Info[/]" };
            AnsiConsole.MarkupLine($"{tag} [grey]({c.Id})[/] {Markup.Escape(c.Message)}");
            if (c.Hint is not null) AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(c.Hint)}[/]");
        }
        foreach (var b in plan.Blockers.Where(b => !plan.Checks.Any(c => b.StartsWith(c.Message))))
            AnsiConsole.MarkupLine($"[red]Blocked:[/] {Markup.Escape(b)}");
        if (!plan.CanApply)
            AnsiConsole.MarkupLine("[grey]Fix the blocked items, or pass --ignore-deps if a mod really doesn't need what's missing.[/]");
        if (plan.IsEmpty) { AnsiConsole.MarkupLine("[green]Nothing to do: the game folder already matches.[/]"); return; }
        var t = new Table().Border(TableBorder.Simple).AddColumns("Step", "Game path", "Mod", "Detail");
        foreach (var s in plan.Steps)
            t.AddRow(
                s.Type switch { StepType.Place => "[green]place[/]", StepType.Remove => "[red]remove[/]", StepType.Backup => "[yellow]backup[/]", _ => "[blue]restore[/]" },
                Markup.Escape(s.Dest),
                Markup.Escape(s.ModId ?? ""),
                Markup.Escape(s.Type == StepType.Place ? s.Method! : s.Note ?? (s.Type == StepType.Restore ? "original back from backups" : "")));
        AnsiConsole.Write(t);
    }

    public static void Applied(ApplyResult r)
    {
        AnsiConsole.MarkupLine($"Placed [green]{r.Placed}[/], removed [red]{r.Removed}[/], backed up [yellow]{r.BackedUp}[/], restored [blue]{r.Restored}[/] file(s).");
        foreach (var n in r.Notes) AnsiConsole.MarkupLine($"[yellow]•[/] {Markup.Escape(n)}");
    }

    public static int Report(VerifyReport r)
    {
        var (color, label) = r.Status switch
        {
            VerifyStatus.Green => ("green", "GREEN: folder matches your clean baseline"),
            VerifyStatus.Amber => ("yellow", "AMBER: matches the baseline except extras you chose to allow"),
            _ => ("red", "RED: folder does not match the baseline"),
        };
        var shown = r.Findings.Where(f => f.Kind != FindingKind.ExpectedChange || r.Status != VerifyStatus.Green).ToList();
        if (shown.Count > 0)
        {
            var t = new Table().Border(TableBorder.Simple).AddColumns("Finding", "Path", "Detail");
            foreach (var f in shown.Take(200))
                t.AddRow(f.Kind switch
                {
                    FindingKind.AllowedExtra or FindingKind.ExpectedChange => $"[yellow]{f.Kind}[/]",
                    FindingKind.SettingsChanged => $"[blue]{f.Kind}[/]",
                    _ => $"[red]{f.Kind}[/]",
                }, Markup.Escape(f.Path), Markup.Escape(f.Detail));
            AnsiConsole.Write(t);
            if (shown.Count > 200) AnsiConsole.MarkupLine($"[grey]…and {shown.Count - 200} more.[/]");
        }
        AnsiConsole.Write(new Panel($"[bold {color}]{label}[/]\n[grey]{r.FilesChecked} files checked, {r.FilesHashed} hashed{(r.Deep ? " (deep)" : "")}. " +
                                    "This proves file state only; it says nothing about bans.[/]").BorderColor(color == "green" ? Spectre.Console.Color.Green : color == "yellow" ? Spectre.Console.Color.Yellow : Spectre.Console.Color.Red));
        if (r.Status == VerifyStatus.Red && r.Findings.Any(f => f.Kind is FindingKind.Extra or FindingKind.ModSignature))
            AnsiConsole.MarkupLine("[grey]Untracked extras aren't removed automatically. Turn them into a tracked mod with `saddlebag adopt <path>... --name <name>`, move them out, or `saddlebag allow <path>` to keep one.[/]");
        if (r.Findings.Any(f => f.Kind == FindingKind.SettingsChanged))
            AnsiConsole.MarkupLine("[grey]Settings files are information only. If a mod changed one, `saddlebag restore-settings <path>` puts the baseline copy back.[/]");
        if (r.Findings.Any(f => f.Kind is FindingKind.Missing or FindingKind.Changed))
            AnsiConsole.MarkupLine("[grey]Missing or changed vanilla files: run your launcher's verify. After a game update, re-capture the baseline.[/]");
        return r.Status switch { VerifyStatus.Green => ExitCodes.Ok, VerifyStatus.Amber => ExitCodes.Amber, _ => ExitCodes.Red };
    }

    public static IProgress<(int Done, int Total, string Path)> Progress(ProgressTask task) =>
        new SyncProgress<(int Done, int Total, string Path)>(p =>
        {
            task.MaxValue = Math.Max(1, p.Total);
            task.Value = p.Done;
        });
}

/// <summary>Progress&lt;T&gt; posts to the thread pool; this reports inline, which is what Spectre's progress display wants.</summary>
public sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    private readonly Lock _gate = new();
    public void Report(T value) { lock (_gate) report(value); }
}
