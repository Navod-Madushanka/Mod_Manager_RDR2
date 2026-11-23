using Saddlebag.Core.Routing;

namespace Saddlebag.Tests;

public class RouterTests : IDisposable
{
    private readonly Sandbox _sb = new();
    private readonly Router _router = new();
    public void Dispose() => _sb.Dispose();

    private static RoutedFile F(RoutePlan p, string source) =>
        p.Files.Single(f => f.Source.Equals(source, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void InstallXml_package_inside_wrapper_goes_to_lml_intact()
    {
        var dir = _sb.Folder("better-saddles",
            ("Better Saddles v2/BetterSaddles/install.xml", "<xml/>"),
            ("Better Saddles v2/BetterSaddles/stream/saddle.ytd", "tex"),
            ("Better Saddles v2/BetterSaddles/readme.txt", "hi"));

        var plan = _router.Route(dir, "Better Saddles");

        Assert.Equal("lml-package", plan.PackageType);
        Assert.Equal("Better Saddles v2/BetterSaddles", plan.PackageRoot);
        Assert.Equal("lml/BetterSaddles/stream/saddle.ytd", F(plan, "Better Saddles v2/BetterSaddles/stream/saddle.ytd").Dest);
        Assert.Equal("lml/BetterSaddles/install.xml", F(plan, "Better Saddles v2/BetterSaddles/install.xml").Dest);
        Assert.All(plan.Files, f => Assert.Equal("2", f.RuleId));
        Assert.True(plan.IsComplete);
    }

    [Fact]
    public void Prestructured_archive_maps_one_to_one_and_skips_root_docs()
    {
        var dir = _sb.Folder("lml",
            ("vfs.asi", "x"), ("lml/mods.xml", "<x/>"), ("ModManager.exe", "x"), ("Readme.txt", "doc"));

        var plan = _router.Route(dir, "LML");

        Assert.Equal("pre-structured", plan.PackageType);
        Assert.Equal(("vfs.asi", FileKind.Framework, "3"), (F(plan, "vfs.asi").Dest, F(plan, "vfs.asi").Kind, F(plan, "vfs.asi").RuleId)!);
        Assert.Equal("lml/mods.xml", F(plan, "lml/mods.xml").Dest);
        Assert.Equal("ModManager.exe", F(plan, "ModManager.exe").Dest);
        Assert.Null(F(plan, "Readme.txt").Dest);
        Assert.Equal(FileKind.Docs, F(plan, "Readme.txt").Kind);
    }

    [Fact]
    public void Asi_with_sibling_ini_and_folder_goes_to_root()
    {
        var dir = _sb.Folder("trainer",
            ("Trainer/Trainer.asi", "x"), ("Trainer/Trainer.ini", "x"), ("Trainer/TrainerData/a.dat", "x"), ("Trainer/readme.md", "x"));

        var plan = _router.Route(dir, "Trainer");

        Assert.Equal("Trainer.asi", F(plan, "Trainer/Trainer.asi").Dest);
        Assert.Equal("4", F(plan, "Trainer/Trainer.asi").RuleId);
        Assert.Equal("Trainer.ini", F(plan, "Trainer/Trainer.ini").Dest);
        Assert.Equal("TrainerData/a.dat", F(plan, "Trainer/TrainerData/a.dat").Dest);
        Assert.Null(F(plan, "Trainer/readme.md").Dest);
        Assert.True(plan.IsComplete);
    }

    [Fact]
    public void DotNet_api_dll_goes_to_root_but_scripts_go_to_scripts()
    {
        var dir = _sb.Folder("dotnet");
        Sandbox.WriteBytes(dir, "ScriptHookRDRNetAPI.dll", Dlls.Api);
        Sandbox.WriteBytes(dir, "ScriptHookRDRDotNet.asi", Dlls.Junk);
        var plan = _router.Route(dir, "dotnet");
        Assert.Equal("ScriptHookRDRNetAPI.dll", F(plan, "ScriptHookRDRNetAPI.dll").Dest);
        Assert.Equal(FileKind.Framework, F(plan, "ScriptHookRDRNetAPI.dll").Kind);

        var mod = _sb.Folder("horse");
        Sandbox.WriteBytes(mod, "Horse Personalities/HorsePersonalities.dll", Dlls.Script);
        Sandbox.Write(mod, "Horse Personalities/HorsePersonalities.ini", "[x]");
        var plan2 = _router.Route(mod, "Horse Personalities");
        Assert.Equal("scripts/HorsePersonalities.dll", F(plan2, "Horse Personalities/HorsePersonalities.dll").Dest);
        Assert.Equal(("5", Confidence.High), (F(plan2, "Horse Personalities/HorsePersonalities.dll").RuleId, F(plan2, "Horse Personalities/HorsePersonalities.dll").Confidence));
        Assert.Equal("scripts/HorsePersonalities.ini", F(plan2, "Horse Personalities/HorsePersonalities.ini").Dest);
    }

    [Fact]
    public void Managed_dll_without_api_reference_and_native_dll_are_unknown()
    {
        var dir = _sb.Folder("odd");
        Sandbox.WriteBytes(dir, "Helper.dll", Dlls.Plain);
        Sandbox.WriteBytes(dir, "native.dll", Dlls.Junk);
        var plan = _router.Route(dir, "odd");
        Assert.All(plan.Files, f => Assert.Equal(("11", true), (f.RuleId, f.NeedsDecision)));
    }

    [Fact]
    public void Loose_stream_assets_go_flat_into_lml_stream()
    {
        var dir = _sb.Folder("coats", ("Coats/textures/coat_01.ytd", "x"), ("Coats/models/coat_01.ydr", "x"));
        var plan = _router.Route(dir, "Coats");
        Assert.Equal("lml/stream/coat_01.ytd", F(plan, "Coats/textures/coat_01.ytd").Dest);
        Assert.Equal("lml/stream/coat_01.ydr", F(plan, "Coats/models/coat_01.ydr").Dest);
        Assert.All(plan.Files, f => Assert.Equal(("6", Confidence.Medium), (f.RuleId, f.Confidence)));
    }

    [Fact]
    public void Replace_folder_mirrors_game_path()
    {
        var dir = _sb.Folder("hud", ("replace/x64/data/ui/hud.ymt", "x"));
        var plan = _router.Route(dir, "HUD");
        Assert.Equal("lml/replace/x64/data/ui/hud.ymt", F(plan, "replace/x64/data/ui/hud.ymt").Dest);
        Assert.Equal("7", F(plan, "replace/x64/data/ui/hud.ymt").RuleId);
    }

    [Fact]
    public void Loose_data_file_asks_and_override_answers()
    {
        var dir = _sb.Folder("weather", ("weather.ymt", "x"), ("notes.txt", "x"));
        var plan = _router.Route(dir, "Weather");
        Assert.Equal(("8", true), (F(plan, "weather.ymt").RuleId, F(plan, "weather.ymt").NeedsDecision));
        Assert.False(plan.IsComplete);

        var overrides = new ModOverrides();
        overrides.Files["WEATHER.ymt"] = "lml/replace/x64/data/weather.ymt"; // keys match case-insensitively
        var plan2 = _router.Route(dir, "Weather", overrides);
        Assert.Equal(("lml/replace/x64/data/weather.ymt", "override"), (F(plan2, "weather.ymt").Dest, F(plan2, "weather.ymt").RuleId));
        Assert.True(plan2.IsComplete);
    }

    [Fact]
    public void Option_folders_are_variants_until_one_is_chosen()
    {
        var dir = _sb.Folder("options",
            ("Mod/Main/install.xml", "<x/>"), ("Mod/Main/a.ytd", "x"),
            ("Mod/Optional - Darker/install.xml", "<x/>"), ("Mod/Optional - Darker/a.ytd", "y"));

        var plan = _router.Route(dir, "Mod");
        Assert.True(plan.NeedsVariant);
        Assert.Equal(["Mod/Main", "Mod/Optional - Darker"], plan.Variants.Order());

        var chosen = _router.Route(dir, "Mod", new ModOverrides { Variant = "Mod/Optional - Darker" });
        Assert.Equal("lml/Optional - Darker/a.ytd", F(chosen, "Mod/Optional - Darker/a.ytd").Dest);
        Assert.Equal(FileKind.Skipped, F(chosen, "Mod/Main/a.ytd").Kind);
    }

    [Fact]
    public void Override_can_skip_a_file()
    {
        var dir = _sb.Folder("skip", ("a.asi", "x"), ("a.ini", "x"));
        var o = new ModOverrides();
        o.Files["a.ini"] = null;
        var plan = _router.Route(dir, "skip", o);
        Assert.Equal(FileKind.Skipped, F(plan, "a.ini").Kind);
        Assert.Null(F(plan, "a.ini").Dest);
    }
}
