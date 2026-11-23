using Saddlebag.Cli;
using Saddlebag.Core.Deploy;
using Saddlebag.Core.Safety;
using Spectre.Console;
using Spectre.Console.Cli;

// When output is piped (logs, scripts) Spectre can't read a width and wraps everything to "…".
if (Console.IsOutputRedirected)
    AnsiConsole.Profile.Width = int.TryParse(Environment.GetEnvironmentVariable("COLUMNS"), out var cols) && cols > 40 ? cols : 160;

var app = new CommandApp();
app.Configure(c =>
{
    c.SetApplicationName("saddlebag");
    c.AddCommand<InitCommand>("init").WithDescription("Point Saddlebag at your game folder and pick where the library lives (same drive).");
    c.AddCommand<StatusCommand>("status").WithDescription("Mode, baseline, mods, and anything that needs attention.");
    c.AddBranch("baseline", b =>
    {
        b.SetDescription("Snapshot of your clean install: the reference Online mode verifies against.");
        b.AddCommand<BaselineCaptureCommand>("capture").WithDescription("Hash every file in the (clean, launcher-verified) game folder.");
        b.AddCommand<BaselineInfoCommand>("info").WithDescription("When the baseline was captured and for which game version.");
    });
    c.AddCommand<ImportCommand>("import").WithDescription("Add an archive or folder to the library and show where each file would go. Doesn't touch the game.");
    c.AddCommand<InstallCommand>("install").WithDescription("Import, answer any routing questions, enable, and deploy.");
    c.AddCommand<LinkCommand>("link").WithDescription("Register a build output folder (e.g. Horse Personalities bin/Release) as a dev mod.");
    c.AddCommand<ListCommand>("list").WithDescription("Mods in the library, in priority order.");
    c.AddCommand<RouteCommand>("route").WithDescription("Show or change where a mod's files go. Answers are saved per mod.");
    c.AddCommand<EnableCommand>("enable").WithDescription("Enable a mod (takes effect on the next deploy).");
    c.AddCommand<DisableCommand>("disable").WithDescription("Disable a mod (takes effect on the next deploy).");
    c.AddCommand<RemoveCommand>("remove").WithDescription("Delete a mod from the library. It must not be deployed.");
    c.AddCommand<PlanCommand>("plan").WithDescription("Dry run: every file operation the next deploy (or purge) would make.");
    c.AddCommand<DeployCommand>("deploy").WithDescription("Make the game folder match the enabled mods.");
    c.AddCommand<PurgeCommand>("purge").WithDescription("Remove every tracked mod file and restore originals.");
    c.AddCommand<VerifyCommand>("verify").WithDescription("Compare the game folder with the baseline.");
    c.AddCommand<OnlineCommand>("online").WithDescription("Purge, restore, then verify the folder matches your clean baseline.");
    c.AddCommand<StoryCommand>("story").WithDescription("Put your enabled mods back (only changed files move).");
    c.AddBranch("profile", b =>
    {
        b.SetDescription("Named sets of enabled mods with their own order, e.g. Story-Full and Story-Dev.");
        b.AddCommand<ProfileListCommand>("list").WithDescription("Profiles, with the active one marked.");
        b.AddCommand<ProfileCreateCommand>("create").WithDescription("New profile, copied from the active one unless --from or --empty.");
        b.AddCommand<ProfileUseCommand>("use").WithDescription("Switch profile; in Story mode only the differing files move.");
        b.AddCommand<ProfileDeleteCommand>("delete").WithDescription("Delete a profile (not the active or default one).");
    });
    c.AddCommand<OrderCommand>("order").WithDescription("Move a mod to a priority position in the active profile; higher wins.");
    c.AddCommand<AdoptCommand>("adopt").WithDescription("Turn untracked mod files in the game folder into a tracked mod.");
    c.AddCommand<AllowCommand>("allow").WithDescription("Allow an extra file in Online mode (shows amber), e.g. x64/data/startup.meta.");
    c.AddCommand<ExpectCommand>("expect").WithDescription("Mark a vanilla file the launcher rewrites (e.g. a DLSS DLL) as expected to change.");

    c.SetExceptionHandler((ex, _) =>
    {
        var msg = ex switch
        {
            GameRunningException g => g.Message,
            DeployException d => d.Message,
            InvalidOperationException or FileNotFoundException or DirectoryNotFoundException or InvalidDataException => ex.Message,
            CommandAppException => ex.Message,
            _ => ex.ToString(),
        };
        AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(msg)}");
        return ExitCodes.Error;
    });
});
return app.Run(args);
