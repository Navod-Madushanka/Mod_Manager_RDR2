using System.ComponentModel;
using Saddlebag.Core;
using Saddlebag.Core.Baseline;
using Saddlebag.Core.Deploy;
using Saddlebag.Core.Routing;
using Saddlebag.Core.State;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Saddlebag.Cli;

// ---------- setup ----------

public sealed class InitCommand : Command<InitCommand.Settings>
{
    public sealed class Settings : HomeSettings
    {
        [CommandOption("--game <DIR>")]
        [Description("Folder that holds RDR2.exe.")]
        public string Game { get; set; } = "";
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.Game)) throw new InvalidOperationException("Pass --game <folder that holds RDR2.exe>.");
        var home = s.Home ?? throw new InvalidOperationException("Pass --home <folder>, on the same drive as the game so hard links work (e.g. D:\\Saddlebag).");
        using var ws = Workspace.Init(home, s.Game);
        Ctx.RememberHome(ws.Home);
        AnsiConsole.MarkupLine($"Game: [bold]{Markup.Escape(ws.GameDir)}[/]\nHome: [bold]{Markup.Escape(ws.Home)}[/]");
        if (ws.HardLinksWork()) AnsiConsole.MarkupLine("[green]Hard links work:[/] mods are stored once and cost no extra disk.");
        else AnsiConsole.MarkupLine("[yellow]Hard links don't work here[/] (different drive, or no write access to the game folder). Files will be copied instead.");
        AnsiConsole.MarkupLine("\nNext: move any mods out of the game folder, run your launcher's [bold]verify files[/], then [bold]saddlebag baseline capture[/].");
        return ExitCodes.Ok;
    }
}

public sealed class StatusCommand : Command<HomeSettings>
{
    public override int Execute(CommandContext context, HomeSettings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        var (version, at) = ws.Store.GetBaselineInfo();
        var mods = ws.Store.GetMods();
        var enabled = ws.Store.GetProfileMods().Count(p => p.Enabled);
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("Game", Markup.Escape(ws.GameDir));
        grid.AddRow("Home", Markup.Escape(ws.Home));
        grid.AddRow("Mode", ws.Store.GetSetting("mode") ?? "not set");
        grid.AddRow("Baseline", at is null ? "[red]none: run `saddlebag baseline capture`[/]" : $"{ws.Store.BaselineCount()} files, {at:yyyy-MM-dd HH:mm} UTC, game {Markup.Escape(version ?? "?")}");
        grid.AddRow("Mods", $"{mods.Count} in library, {enabled} enabled");
        grid.AddRow("Deployed", $"{ws.Store.GetDeployed().Count} files, {ws.Store.GetBackups().Count} originals backed up");
        grid.AddRow("Profile", Markup.Escape(ws.Store.FindProfile(ws.Store.ActiveProfile)?.Name ?? ws.Store.ActiveProfile));
        AnsiConsole.Write(grid);
        if (at is not null && ws.Baseline.GameUpdatedSinceBaseline())
            AnsiConsole.MarkupLine("[yellow]RDR2.exe changed since the baseline: the game updated.[/] Hook-based mods may crash. Purge, verify files in your launcher, then re-capture the baseline.");
        return ExitCodes.Ok;
    }
}

public sealed class BaselineCaptureCommand : Command<BaselineCaptureCommand.Settings>
{
    public sealed class Settings : HomeSettings
    {
        [CommandOption("--force")]
        [Description("Capture even though mod files were found. Only if you're sure they're vanilla.")]
        public bool Force { get; set; }
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        AnsiConsole.Progress().Start(p =>
        {
            var task = p.AddTask("Hashing game files");
            ws.CaptureBaseline(s.Force, Show.Progress(task));
            task.Value = task.MaxValue;
        });
        AnsiConsole.MarkupLine($"[green]Baseline captured:[/] {ws.Store.BaselineCount()} files, game {Markup.Escape(ws.Store.GetBaselineInfo().GameVersion ?? "?")}.");
        return ExitCodes.Ok;
    }
}

public sealed class BaselineInfoCommand : Command<HomeSettings>
{
    public override int Execute(CommandContext context, HomeSettings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        var (version, at) = ws.Store.GetBaselineInfo();
        if (at is null) { AnsiConsole.MarkupLine("[red]No baseline yet.[/]"); return ExitCodes.Red; }
        var rows = ws.Store.GetBaseline();
        AnsiConsole.MarkupLine($"{rows.Count} files, {rows.Sum(r => r.Size) / (1024.0 * 1024 * 1024):F1} GiB, captured {at:yyyy-MM-dd HH:mm} UTC, game {Markup.Escape(version ?? "?")}.");
        return ExitCodes.Ok;
    }
}

// ---------- library ----------

public class ImportSettings : HomeSettings
{
    [CommandArgument(0, "<SOURCE>")]
    [Description("Archive (.zip, .7z, .rar), folder, or single file.")]
    public string Source { get; set; } = "";

    [CommandOption("--name <NAME>")] public string? Name { get; set; }
    [CommandOption("--version <VERSION>")] public string? Version { get; set; }
    [CommandOption("--url <URL>")] [Description("Where you got it, e.g. the Nexus page.")] public string? Url { get; set; }
}

public sealed class ImportCommand : Command<ImportSettings>
{
    public override int Execute(CommandContext context, ImportSettings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        var r = ws.Library.Import(s.Source, s.Name, s.Version, s.Url);
        if (r.AlreadyImported) AnsiConsole.MarkupLine($"[grey]Already in the library as[/] {Markup.Escape(r.Mod.Id)}.");
        else AnsiConsole.MarkupLine($"[green]Imported[/] as [bold]{Markup.Escape(r.Mod.Id)}[/].");
        Show.Routing(r.Plan, r.Mod.Name);
        if (!r.Plan.IsComplete) AnsiConsole.MarkupLine($"Answer the open questions with [bold]saddlebag route {Markup.Escape(r.Mod.Id)}[/], then enable it.");
        else AnsiConsole.MarkupLine($"Enable with [bold]saddlebag enable {Markup.Escape(r.Mod.Id)}[/].");
        return ExitCodes.Ok;
    }
}

public sealed class InstallCommand : Command<InstallCommand.Settings>
{
    public sealed class Settings : ImportSettings
    {
        [CommandOption("--no-deploy")] [Description("Import and enable, but don't touch the game folder yet.")]
        public bool NoDeploy { get; set; }
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        var r = ws.Library.Import(s.Source, s.Name, s.Version, s.Url);
        var plan = r.Plan;
        var interactive = AnsiConsole.Profile.Capabilities.Interactive && !Console.IsInputRedirected;

        if (plan.NeedsVariant)
        {
            if (!interactive) throw new InvalidOperationException($"Pick a variant: saddlebag route {r.Mod.Id} --variant <one of: {string.Join(", ", plan.Variants)}>");
            var v = AnsiConsole.Prompt(new SelectionPrompt<string>().Title($"[bold]{Markup.Escape(r.Mod.Name)}[/] has several options. Which one?").AddChoices(plan.Variants));
            plan = ws.Library.SetVariant(r.Mod.Id, v);
        }
        foreach (var f in plan.Unresolved.ToList())
        {
            if (!interactive) throw new InvalidOperationException($"'{f.Source}' needs a destination: saddlebag route {r.Mod.Id} --set \"{f.Source}=<game path>\" (or --skip).");
            var dest = AnsiConsole.Prompt(new TextPrompt<string>($"Where does [bold]{Markup.Escape(f.Source)}[/] go? Game-relative path, or [grey]skip[/]:"));
            plan = ws.Library.SetOverride(r.Mod.Id, f.Source, dest.Trim().Equals("skip", StringComparison.OrdinalIgnoreCase) ? null : dest.Trim());
        }

        Show.Routing(plan, r.Mod.Name);
        ws.Store.SetEnabled(r.Mod.Id, true);
        AnsiConsole.MarkupLine($"[green]Enabled[/] {Markup.Escape(r.Mod.Id)}.");
        if (s.NoDeploy) return ExitCodes.Ok;
        ws.Prepare();
        var dp = ws.Deployer.Plan();
        Show.Plan(dp);
        if (!dp.CanApply)
        {
            AnsiConsole.MarkupLine("[yellow]Installed and enabled, but not deployed yet.[/] Fix the above, then run [bold]saddlebag deploy[/].");
            return ExitCodes.Red;
        }
        Show.Applied(ws.Deployer.Apply(dp));
        ws.Store.SetSetting("mode", "story");
        return ExitCodes.Ok;
    }
}

public sealed class LinkCommand : Command<LinkCommand.Settings>
{
    public sealed class Settings : HomeSettings
    {
        [CommandArgument(0, "<FOLDER>")] public string Folder { get; set; } = "";
        [CommandOption("--name <NAME>")] public string? Name { get; set; }
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        var name = s.Name ?? new DirectoryInfo(s.Folder).Name;
        var r = ws.Library.Link(s.Folder, name);
        ws.Store.SetEnabled(r.Mod.Id, true);
        Show.Routing(r.Plan, r.Mod.Name);
        AnsiConsole.MarkupLine($"[green]Linked and enabled[/] as [bold]{Markup.Escape(r.Mod.Id)}[/]. Each [bold]saddlebag deploy[/] now copies your latest build in; only changed files move.");
        return ExitCodes.Ok;
    }
}

public sealed class ListCommand : Command<HomeSettings>
{
    public override int Execute(CommandContext context, HomeSettings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        var profile = ws.Store.GetProfileMods().ToDictionary(p => p.ModId);
        var deployed = ws.Store.GetDeployed().GroupBy(d => d.ModId).ToDictionary(g => g.Key, g => g.Count());
        var t = new Table().Border(TableBorder.Rounded).AddColumns("#", "Id", "Name", "Version", "Enabled", "Deployed files", "Source");
        foreach (var m in ws.Store.GetMods().OrderBy(m => profile.TryGetValue(m.Id, out var p) ? p.Priority : int.MaxValue))
        {
            profile.TryGetValue(m.Id, out var p);
            t.AddRow(p?.Priority.ToString() ?? "-", Markup.Escape(m.Id), Markup.Escape(m.Name), Markup.Escape(m.Version ?? ""),
                p?.Enabled == true ? "[green]yes[/]" : "[grey]no[/]", deployed.GetValueOrDefault(m.Id).ToString(),
                Markup.Escape(m.LinkedPath is not null ? "linked: " + m.LinkedPath : m.SourceUrl ?? ""));
        }
        AnsiConsole.MarkupLine($"Profile: [bold]{Markup.Escape(ws.Store.FindProfile(ws.Store.ActiveProfile)?.Name ?? ws.Store.ActiveProfile)}[/]");
        AnsiConsole.Write(t);
        AnsiConsole.MarkupLine("[grey]Higher # wins when two mods write the same file. Change it with `saddlebag order <mod> <#>`.[/]");
        return ExitCodes.Ok;
    }
}

public sealed class RouteCommand : Command<RouteCommand.Settings>
{
    public sealed class Settings : HomeSettings
    {
        [CommandArgument(0, "<MOD>")] public string Mod { get; set; } = "";
        [CommandOption("--variant <FOLDER>")] [Description("Choose one option folder of a multi-option archive.")] public string? Variant { get; set; }
        [CommandOption("--set <SRC=DEST>")] [Description("Send a source file to a game-relative path. Repeatable.")] public string[] Set { get; set; } = [];
        [CommandOption("--skip <SRC>")] [Description("Don't deploy this file. Repeatable.")] public string[] Skip { get; set; } = [];
        [CommandOption("--clear <SRC>")] [Description("Forget a saved answer. Repeatable.")] public string[] Clear { get; set; } = [];
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        var mod = ws.Library.Require(s.Mod);
        RoutePlan plan;
        if (s.Variant is not null) ws.Library.SetVariant(mod.Id, s.Variant);
        foreach (var kv in s.Set)
        {
            var i = kv.IndexOf('=');
            if (i <= 0) throw new InvalidOperationException($"--set expects SRC=DEST, got '{kv}'.");
            ws.Library.SetOverride(mod.Id, kv[..i], kv[(i + 1)..]);
        }
        foreach (var src in s.Skip) ws.Library.SetOverride(mod.Id, src, null);
        foreach (var src in s.Clear) ws.Library.ClearOverride(mod.Id, src);
        plan = ws.Library.Reroute(mod.Id);
        Show.Routing(plan, mod.Name);
        return ExitCodes.Ok;
    }
}

public class ModArgSettings : HomeSettings
{
    [CommandArgument(0, "<MOD>")] public string Mod { get; set; } = "";
}

public sealed class EnableCommand : Command<ModArgSettings>
{
    public override int Execute(CommandContext context, ModArgSettings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        ws.ThrowIfBisecting();
        var m = ws.Library.Require(s.Mod);
        ws.Store.SetEnabled(m.Id, true);
        AnsiConsole.MarkupLine($"[green]Enabled[/] {Markup.Escape(m.Id)}. Run [bold]saddlebag deploy[/] to apply.");
        return ExitCodes.Ok;
    }
}

public sealed class DisableCommand : Command<ModArgSettings>
{
    public override int Execute(CommandContext context, ModArgSettings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        ws.ThrowIfBisecting();
        var m = ws.Library.Require(s.Mod);
        ws.Store.SetEnabled(m.Id, false);
        AnsiConsole.MarkupLine($"Disabled {Markup.Escape(m.Id)}. Run [bold]saddlebag deploy[/] to apply.");
        return ExitCodes.Ok;
    }
}

public sealed class RemoveCommand : Command<ModArgSettings>
{
    public override int Execute(CommandContext context, ModArgSettings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        var m = ws.Library.Require(s.Mod);
        ws.Library.Remove(m.Id);
        AnsiConsole.MarkupLine($"Removed {Markup.Escape(m.Id)} from the library.");
        return ExitCodes.Ok;
    }
}

// ---------- deploy ----------

public sealed class PlanCommand : Command<PlanCommand.Settings>
{
    public sealed class Settings : DeploySettings
    {
        [CommandOption("--purge")] [Description("Plan a purge instead of a deploy.")] public bool Purge { get; set; }
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        var plan = ws.Deployer.Plan(s.Purge, s.IgnoreDeps);
        Show.Plan(plan);
        return plan.CanApply ? ExitCodes.Ok : ExitCodes.Red;
    }
}

public class DeploySettings : HomeSettings
{
    [CommandOption("--ignore-deps")]
    [Description("Deploy even though a dependency check failed (e.g. an .asi that truly doesn't need Script Hook).")]
    public bool IgnoreDeps { get; set; }
}

public sealed class DeployCommand : Command<DeploySettings>
{
    public override int Execute(CommandContext context, DeploySettings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        ws.Prepare();
        var plan = ws.Deployer.Plan(ignoreBlockingChecks: s.IgnoreDeps);
        if (!plan.CanApply) { Show.Plan(plan); return ExitCodes.Red; }
        Show.Plan(plan);
        Show.Applied(ws.Deployer.Apply(plan));
        ws.Store.SetSetting("mode", "story");
        return ExitCodes.Ok;
    }
}

public sealed class PurgeCommand : Command<HomeSettings>
{
    public override int Execute(CommandContext context, HomeSettings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        Show.Applied(ws.Purge());
        return ExitCodes.Ok;
    }
}

public class VerifySettings : HomeSettings
{
    [CommandOption("--deep")] [Description("Hash every file instead of trusting size and modified time.")] public bool Deep { get; set; }
}

public sealed class VerifyCommand : Command<VerifySettings>
{
    public override int Execute(CommandContext context, VerifySettings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        VerifyReport? report = null;
        AnsiConsole.Progress().Start(p =>
        {
            var task = p.AddTask(s.Deep ? "Hashing every file" : "Checking files");
            report = ws.Baseline.Verify(s.Deep, Show.Progress(task));
            task.Value = task.MaxValue;
        });
        return Show.Report(report!);
    }
}

public sealed class OnlineCommand : Command<VerifySettings>
{
    public override int Execute(CommandContext context, VerifySettings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        AnsiConsole.MarkupLine("[bold]Online mode:[/] guard → purge → verify");
        var r = ws.GoOnline(s.Deep);
        Show.Applied(r.Purge);
        return Show.Report(r.Report);
    }
}

public sealed class StoryCommand : Command<DeploySettings>
{
    public override int Execute(CommandContext context, DeploySettings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        ws.Prepare();
        var plan = ws.Deployer.Plan(ignoreBlockingChecks: s.IgnoreDeps);
        Show.Plan(plan);
        if (!plan.CanApply) return ExitCodes.Red;
        Show.Applied(ws.GoStory(s.IgnoreDeps));
        return ExitCodes.Ok;
    }
}

public class PathRuleSettings : HomeSettings
{
    [CommandArgument(0, "<PATH>")] [Description("Game-relative path, e.g. x64/data/startup.meta")] public string Path { get; set; } = "";
    [CommandOption("--remove")] public bool Remove { get; set; }
}

public sealed class AllowCommand : Command<PathRuleSettings>
{
    public override int Execute(CommandContext context, PathRuleSettings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        var p = Saddlebag.Core.Util.RelPath.Normalize(s.Path);
        ws.Store.SetPathRule(p, s.Remove ? null : StateStore.RuleAllow);
        AnsiConsole.MarkupLine(s.Remove ? $"No longer allowed: {Markup.Escape(p)}" : $"Allowed: {Markup.Escape(p)}. It will show [yellow]amber[/] every time; it's still not vanilla.");
        return ExitCodes.Ok;
    }
}

public sealed class ExpectCommand : Command<PathRuleSettings>
{
    public override int Execute(CommandContext context, PathRuleSettings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        var p = Saddlebag.Core.Util.RelPath.Normalize(s.Path);
        ws.Store.SetPathRule(p, s.Remove ? null : StateStore.RuleExpectChange);
        AnsiConsole.MarkupLine(s.Remove ? $"No longer expected to change: {Markup.Escape(p)}" : $"Expected to change: {Markup.Escape(p)} (e.g. the launcher rewrites it).");
        return ExitCodes.Ok;
    }
}

// ---------- Phase 2: profiles, order, adopt ----------

public sealed class ProfileListCommand : Command<HomeSettings>
{
    public override int Execute(CommandContext context, HomeSettings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        var t = new Table().Border(TableBorder.Rounded).AddColumns("", "Id", "Name", "Enabled mods");
        foreach (var (id, name) in ws.Store.GetProfiles())
            t.AddRow(id == ws.Store.ActiveProfile ? "[green]●[/]" : "", Markup.Escape(id), Markup.Escape(name),
                ws.Store.GetProfileMods(id).Count(p => p.Enabled).ToString());
        AnsiConsole.Write(t);
        return ExitCodes.Ok;
    }
}

public sealed class ProfileCreateCommand : Command<ProfileCreateCommand.Settings>
{
    public sealed class Settings : HomeSettings
    {
        [CommandArgument(0, "<NAME>")] public string Name { get; set; } = "";
        [CommandOption("--from <PROFILE>")] [Description("Start as a copy of this profile (default: the active one). Use --empty for none.")]
        public string? From { get; set; }
        [CommandOption("--empty")] public bool Empty { get; set; }
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        string? from = null;
        if (!s.Empty)
            from = s.From is null ? ws.Store.ActiveProfile
                : ws.Store.FindProfile(s.From)?.Id ?? throw new InvalidOperationException($"No profile '{s.From}'.");
        var id = Saddlebag.Core.Library.ModLibrary.Slug(s.Name);
        ws.Store.CreateProfile(id, s.Name, from);
        AnsiConsole.MarkupLine($"[green]Created[/] {Markup.Escape(s.Name)} ({Markup.Escape(id)}){(from is null ? ", empty" : $", copied from {Markup.Escape(from)}")}. Switch with [bold]saddlebag profile use {Markup.Escape(id)}[/].");
        return ExitCodes.Ok;
    }
}

public sealed class ProfileUseCommand : Command<ProfileUseCommand.Settings>
{
    public sealed class Settings : HomeSettings
    {
        [CommandArgument(0, "<PROFILE>")] public string Profile { get; set; } = "";
        [CommandOption("--no-deploy")] [Description("Switch without touching the game folder.")] public bool NoDeploy { get; set; }
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        var r = ws.SwitchProfile(s.Profile, deploy: !s.NoDeploy);
        AnsiConsole.MarkupLine($"Active profile: [bold]{Markup.Escape(ws.Store.FindProfile(ws.Store.ActiveProfile)!.Value.Name)}[/]");
        if (r is null)
            AnsiConsole.MarkupLine(ws.Store.GetSetting("mode") == "online"
                ? "[grey]You're in Online mode, so nothing was deployed. `saddlebag story` puts this profile's mods in.[/]"
                : "[grey]Not deployed. Run `saddlebag deploy` when ready.[/]");
        else Show.Applied(r);
        return ExitCodes.Ok;
    }
}

public sealed class ProfileDeleteCommand : Command<ProfileUseCommand.Settings>
{
    public override int Execute(CommandContext context, ProfileUseCommand.Settings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        ws.ThrowIfBisecting();
        var p = ws.Store.FindProfile(s.Profile) ?? throw new InvalidOperationException($"No profile '{s.Profile}'.");
        ws.Store.DeleteProfile(p.Id);
        AnsiConsole.MarkupLine($"Deleted profile {Markup.Escape(p.Name)}. Its mods stay in the library.");
        return ExitCodes.Ok;
    }
}

public sealed class OrderCommand : Command<OrderCommand.Settings>
{
    public sealed class Settings : ModArgSettings
    {
        [CommandArgument(1, "<POSITION>")] [Description("1 is lowest priority; the highest number wins conflicts.")]
        public int Position { get; set; }
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        ws.ThrowIfBisecting();
        var m = ws.Library.Require(s.Mod);
        ws.Store.MoveMod(m.Id, s.Position);
        AnsiConsole.MarkupLine($"Moved {Markup.Escape(m.Id)} to #{s.Position}. Run [bold]saddlebag plan[/] to see what changes.");
        return ExitCodes.Ok;
    }
}

public sealed class AdoptCommand : Command<AdoptCommand.Settings>
{
    public sealed class Settings : HomeSettings
    {
        [CommandArgument(0, "<PATHS>")] [Description("Game-relative files or folders, e.g. dinput8.dll ScriptHookRDR2.dll lml")]
        public string[] Paths { get; set; } = [];
        [CommandOption("--name <NAME>")] public string? Name { get; set; }
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        var name = s.Name ?? throw new InvalidOperationException("Pass --name, e.g. --name \"Script Hook (manual install)\".");
        var r = ws.Adopt(s.Paths, name);
        AnsiConsole.MarkupLine($"[green]Adopted[/] {r.Import.Plan.Files.Count} file(s) as [bold]{Markup.Escape(r.Import.Mod.Id)}[/] and enabled it. They're in the library now, out of the game folder.");
        if (r.ReplacedVanilla.Count > 0)
        {
            AnsiConsole.MarkupLine("[yellow]These replaced vanilla files, so the originals are now missing:[/]");
            foreach (var f in r.ReplacedVanilla) AnsiConsole.MarkupLine($"  • {Markup.Escape(f)}");
            AnsiConsole.MarkupLine("Run your launcher's [bold]verify files[/] to get the originals back, then [bold]saddlebag story[/] (it backs them up before linking the mod in).");
        }
        else AnsiConsole.MarkupLine("Run [bold]saddlebag story[/] to put them back as tracked files.");
        return ExitCodes.Ok;
    }
}

// ---------- Phase 4: crash bisect ----------

public static class BisectView
{
    public static string Name(Workspace ws, string id) => ws.Store.GetMod(id)?.Name ?? id;

    public static void Show(Workspace ws, Saddlebag.Core.Bisect.BisectSession s, ApplyResult? applied = null)
    {
        if (applied is not null && (applied.Placed + applied.Removed) > 0)
            AnsiConsole.MarkupLine($"[grey]Game folder updated: placed {applied.Placed}, removed {applied.Removed} file(s).[/]");

        switch (s.Phase)
        {
            case Saddlebag.Core.Bisect.BisectPhase.Found:
                AnsiConsole.Write(new Panel($"[bold red]Culprit: {Markup.Escape(Name(ws, s.Culprit!))}[/] ({Markup.Escape(s.Culprit!)})\n" +
                                            $"[grey]It crashes the game on its own (with only the kept mods). Found in {s.Launches} launch(es).[/]")
                    .BorderColor(Color.Red));
                AnsiConsole.MarkupLine("Run [bold]saddlebag bisect reset --disable-culprit[/] to go back to your profile without it, or [bold]bisect reset[/] to keep it.");
                return;
            case Saddlebag.Core.Bisect.BisectPhase.Interaction:
                AnsiConsole.MarkupLine($"[yellow]No single mod crashes on its own.[/] The crash needs two or more of these {s.CrashSet.Count} mods together:");
                foreach (var m in s.CrashSet) AnsiConsole.MarkupLine($"  • {Markup.Escape(Name(ws, m))} [grey]({Markup.Escape(m)})[/]");
                AnsiConsole.MarkupLine("[grey]Look for mods that touch the same thing (same horse, same script). `saddlebag bisect reset` to go back.[/]");
                return;
            case Saddlebag.Core.Bisect.BisectPhase.NotFound:
                AnsiConsole.MarkupLine("[yellow]None of the suspects crashed the game.[/] The cause is probably in a kept mod (Script Hook, LML, the .NET hook), the game itself, or a combination that never came together.");
                AnsiConsole.MarkupLine($"[grey]Kept on every launch: {Markup.Escape(string.Join(", ", s.Kept.Select(k => Name(ws, k))))}. `saddlebag bisect reset` to go back.[/]");
                return;
        }

        var step = s.Phase == Saddlebag.Core.Bisect.BisectPhase.Confirm ? "Confirm launch" : $"Launch {s.Launches + 1}";
        AnsiConsole.MarkupLine($"[bold]{step}[/]: {s.Candidates.Count} suspect(s) left, about {s.RemainingEstimate} launch(es) to go.");
        var t = new Table().Border(TableBorder.Simple).AddColumns("This launch", "Mod");
        foreach (var k in s.Kept) t.AddRow("[blue]kept on[/]", Markup.Escape(Name(ws, k)));
        foreach (var m in s.Testing) t.AddRow("[green]testing[/]", Markup.Escape(Name(ws, m)));
        AnsiConsole.Write(t);
        var off = s.Suspects.Count - s.Testing.Count;
        if (off > 0) AnsiConsole.MarkupLine($"[grey]{off} other mod(s) are off for this launch.[/]");
        AnsiConsole.MarkupLine("Launch RDR2 in [bold]Story Mode[/] and do what made it crash. Then close the game and run [bold]saddlebag bisect bad[/] (it crashed) or [bold]saddlebag bisect good[/] (it didn't).");
    }
}

public sealed class BisectStartCommand : Command<BisectStartCommand.Settings>
{
    public sealed class Settings : HomeSettings
    {
        [CommandOption("--keep <MOD>")] [Description("Keep this mod on in every launch (e.g. an asset pack others need). Repeatable.")]
        public string[] Keep { get; set; } = [];
        [CommandOption("--suspect <MOD>")] [Description("Suspect a mod that would be kept on by default (Script Hook, LML, .NET hook). Repeatable.")]
        public string[] Suspect { get; set; } = [];
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        var (session, applied) = ws.BisectStart(s.Keep, s.Suspect);
        AnsiConsole.MarkupLine($"[bold]Crash bisect started[/] on {session.Suspects.Count} suspect mod(s). Your profile isn't changed; this runs in a temporary one.");
        BisectView.Show(ws, session, applied);
        return ExitCodes.Ok;
    }
}

public sealed class BisectAnswerCommand(bool crashed) : Command<HomeSettings>
{
    public override int Execute(CommandContext context, HomeSettings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        var (session, applied) = ws.BisectAnswer(crashed);
        BisectView.Show(ws, session, applied);
        return ExitCodes.Ok;
    }
}

public sealed class BisectGoodCommand() : Command<HomeSettings>
{
    public override int Execute(CommandContext context, HomeSettings s, CancellationToken ct) => new BisectAnswerCommand(false).Execute(context, s, ct);
}

public sealed class BisectBadCommand() : Command<HomeSettings>
{
    public override int Execute(CommandContext context, HomeSettings s, CancellationToken ct) => new BisectAnswerCommand(true).Execute(context, s, ct);
}

public sealed class BisectStatusCommand : Command<HomeSettings>
{
    public override int Execute(CommandContext context, HomeSettings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        var session = ws.LoadBisect();
        if (session is null) { AnsiConsole.MarkupLine("No bisect running. Start one with [bold]saddlebag bisect start[/]."); return ExitCodes.Ok; }
        foreach (var (h, i) in session.History.Select((h, i) => (h, i)))
            AnsiConsole.MarkupLine($"[grey]Launch {i + 1}: {h.Testing.Count} testing → {(h.Answer == true ? "[red]crashed[/]" : "[green]fine[/]")}[/]");
        BisectView.Show(ws, session);
        return ExitCodes.Ok;
    }
}

public sealed class BisectUndoCommand : Command<HomeSettings>
{
    public override int Execute(CommandContext context, HomeSettings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        var (session, applied) = ws.BisectUndo();
        AnsiConsole.MarkupLine("Took back the last answer.");
        BisectView.Show(ws, session, applied);
        return ExitCodes.Ok;
    }
}

public sealed class BisectResetCommand : Command<BisectResetCommand.Settings>
{
    public sealed class Settings : HomeSettings
    {
        [CommandOption("--disable-culprit")] [Description("Disable the mod the bisect found in your profile before restoring.")]
        public bool DisableCulprit { get; set; }
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        var culprit = ws.LoadBisect()?.Culprit;
        var r = ws.BisectReset(s.DisableCulprit);
        AnsiConsole.MarkupLine($"Bisect ended. Back on [bold]{Markup.Escape(ws.Store.FindProfile(ws.Store.ActiveProfile)?.Name ?? ws.Store.ActiveProfile)}[/]" +
                               (s.DisableCulprit && culprit is not null ? $", with {Markup.Escape(BisectView.Name(ws, culprit))} disabled." : "."));
        Show.Applied(r);
        return ExitCodes.Ok;
    }
}

// ---------- Phase 4: install.xml generation ----------

public sealed class LmlPackCommand : Command<LmlPackCommand.Settings>
{
    public sealed class Settings : ModArgSettings
    {
        [CommandOption("--map <SRC=GAMEPATH>")]
        [Description("Declare a file as a replacement, e.g. cameras.ymt=update:/x64/data/metadata/cameras.ymt. Repeatable.")]
        public string[] Map { get; set; } = [];
        [CommandOption("--unmap <SRC>")] [Description("Remove a mapping. Repeatable.")] public string[] Unmap { get; set; } = [];
        [CommandOption("--folder <NAME>")] [Description("Folder name under lml/ (default: the mod's name).")] public string? Folder { get; set; }
        [CommandOption("--author <NAME>")] public string? Author { get; set; }
        [CommandOption("--off")] [Description("Stop packaging; files go back to their loose locations on the next deploy.")] public bool Off { get; set; }
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        var mod = ws.Library.Require(s.Mod);
        if (s.Off)
        {
            Show.Routing(ws.Library.SetLmlPack(mod.Id, null), mod.Name);
            AnsiConsole.MarkupLine("LML packaging is [bold]off[/]. Run [bold]saddlebag deploy[/] to apply.");
            return ExitCodes.Ok;
        }

        var current = ws.Library.LoadManifest(mod.Id).LmlPack;
        var opt = new LmlPackOptions
        {
            Enabled = true,
            Folder = s.Folder ?? current?.Folder,
            Author = s.Author ?? current?.Author,
            GamePaths = new Dictionary<string, string>(current?.GamePaths ?? [], StringComparer.OrdinalIgnoreCase),
        };
        foreach (var kv in s.Map)
        {
            var i = kv.IndexOf('=');
            if (i <= 0) throw new InvalidOperationException($"--map expects SRC=GAMEPATH, got '{kv}'.");
            opt.GamePaths[Saddlebag.Core.Util.RelPath.Normalize(kv[..i])] = kv[(i + 1)..].Trim();
        }
        foreach (var src in s.Unmap) opt.GamePaths.Remove(Saddlebag.Core.Util.RelPath.Normalize(src));

        var plan = ws.Library.SetLmlPack(mod.Id, opt);
        Show.Routing(plan, mod.Name);
        foreach (var n in ws.Library.LoadManifest(mod.Id).Notes) AnsiConsole.MarkupLine($"[yellow]•[/] {Markup.Escape(n)}");
        var xml = File.ReadAllText(ws.Library.SourcePath(mod, LmlPackager.GeneratedInstallXml));
        AnsiConsole.Write(new Panel(Markup.Escape(xml.TrimEnd())).Header("generated install.xml").BorderColor(Color.Grey));
        AnsiConsole.MarkupLine("Run [bold]saddlebag deploy[/] to apply. LML's Mod Manager will then list it.");
        return ExitCodes.Ok;
    }
}

// ---------- Phase 4: build-folder watcher ----------

public sealed class WatchCommand : Command<WatchCommand.Settings>
{
    public sealed class Settings : HomeSettings
    {
        [CommandArgument(0, "[MODS]")] [Description("Linked mods to watch. Default: every enabled linked mod.")]
        public string[] Mods { get; set; } = [];
        [CommandOption("--quiet <MS>")] [Description("Wait this long after the last change before deploying (default 1500).")]
        public int QuietMs { get; set; } = 1500;
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        using var ws = Ctx.Open(s);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Stop cleanly between deploys on Ctrl+C, Ctrl+Break, closing the window, or SIGTERM. A deploy in progress
        // finishes its current step; anything left is recovered from the journal next time.
        var signals = new[] { System.Runtime.InteropServices.PosixSignal.SIGINT, System.Runtime.InteropServices.PosixSignal.SIGQUIT,
                              System.Runtime.InteropServices.PosixSignal.SIGTERM, System.Runtime.InteropServices.PosixSignal.SIGHUP }
            .Select(sig => System.Runtime.InteropServices.PosixSignalRegistration.Create(sig, c => { c.Cancel = true; cts.Cancel(); }))
            .ToList();

        var watcher = new Saddlebag.Core.Watch.BuildWatcher(ws, s.Mods, TimeSpan.FromMilliseconds(Math.Max(0, s.QuietMs)), e =>
        {
            var (color, tag) = e.Kind switch
            {
                Saddlebag.Core.Watch.WatchEventKind.Deployed => ("green", "deployed"),
                Saddlebag.Core.Watch.WatchEventKind.UpToDate => ("grey", "up to date"),
                Saddlebag.Core.Watch.WatchEventKind.WaitingForGame => ("yellow", "waiting"),
                Saddlebag.Core.Watch.WatchEventKind.Retrying => ("yellow", "retrying"),
                Saddlebag.Core.Watch.WatchEventKind.Blocked or Saddlebag.Core.Watch.WatchEventKind.Error => ("red", "blocked"),
                Saddlebag.Core.Watch.WatchEventKind.Changed => ("blue", "changed"),
                _ => ("grey", e.Kind.ToString().ToLowerInvariant()),
            };
            AnsiConsole.MarkupLine($"[grey]{DateTime.Now:HH:mm:ss}[/] [{color}]{tag,-10}[/] {Markup.Escape(e.Message)}");
            if (e.Paths is { Count: > 0 } paths)
                foreach (var p in paths.Take(8)) AnsiConsole.MarkupLine($"           [grey]{Markup.Escape(p)}[/]");
            if (e.Result is { Notes.Count: > 0 } r)
                foreach (var n in r.Notes) AnsiConsole.MarkupLine($"           [yellow]•[/] {Markup.Escape(n)}");
        });

        foreach (var (id, folder) in watcher.Targets)
            AnsiConsole.MarkupLine($"Watching [bold]{Markup.Escape(BisectView.Name(ws, id))}[/]: {Markup.Escape(folder)}");
        AnsiConsole.MarkupLine("[grey]Build in Visual Studio as usual; each build is deployed once it's finished. Ctrl+C to stop.[/]");
        try { watcher.Run(cts.Token); }
        finally { foreach (var r in signals) r.Dispose(); }
        return ExitCodes.Ok;
    }
}
