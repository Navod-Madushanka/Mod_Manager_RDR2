using Saddlebag.Core;
using Saddlebag.Core.Routing;

namespace Saddlebag.Tests;

public class RuleSetTests : IDisposable
{
    private readonly Sandbox _sb = new();
    public void Dispose() => _sb.Dispose();

    private string WriteRules(string json) => Sandbox.Write(_sb.Root, "rules.json", json);

    [Fact]
    public void Built_in_rules_hold_every_list_and_a_title_for_every_rule_id()
    {
        var r = RuleSet.Default;
        Assert.Equal("built-in", r.Source);
        Assert.Contains("ScriptHookRDRNetAPI.dll", r.Frameworks);
        Assert.Contains(".ytd", r.StreamExtensions);
        Assert.Equal(["dinput8.dll", "version.dll"], r.AsiLoaders);
        foreach (var id in new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "override", "lml-pack" })
            Assert.True(r.Rules.ContainsKey(id), $"rule {id} has no title");
        Assert.Equal("ASI plugin: game root", r.Reason("asi"));
    }

    [Fact]
    public void A_user_file_replaces_lists_and_merges_titles_and_reasons()
    {
        var path = WriteRules("""
            {
              // comments and trailing commas are fine
              "frameworks": ["ScriptHookRDR2.dll", "dinput8.dll", "ScriptHookRDR2DotNet.asi"],
              "reasons": { "asi": "ASI plugin, goes next to RDR2.exe" },
            }
            """);
        var r = RuleSet.Load(path);
        Assert.Equal(path, r.Source);
        Assert.Equal(["ScriptHookRDR2.dll", "dinput8.dll", "ScriptHookRDR2DotNet.asi"], r.Frameworks);
        Assert.Equal(RuleSet.Default.StreamExtensions, r.StreamExtensions); // untouched keys stay built-in
        Assert.Equal("ASI plugin, goes next to RDR2.exe", r.Reason("asi"));
        Assert.Equal(RuleSet.Default.Reason("docs"), r.Reason("docs"));
        Assert.NotEqual(RuleSet.Default.Hash, r.Hash);
    }

    [Fact]
    public void A_user_file_that_changes_nothing_is_the_built_in_set()
    {
        var path = WriteRules("""{ "asiLoaders": ["dinput8.dll", "version.dll"] }""");
        Assert.Same(RuleSet.Default, RuleSet.Load(path));
        Assert.Same(RuleSet.Default, RuleSet.Load(Path.Combine(_sb.Root, "missing.json")));
    }

    [Theory]
    [InlineData("""{ "frameworks": "dinput8.dll" }""", "must be a list")]
    [InlineData("""{ "streamExtensions": ["ytd"] }""", "must start with a dot")]
    [InlineData("""{ "frameworks": ["bin/dinput8.dll"] }""", "names, not paths")]
    [InlineData("""{ "framework": ["dinput8.dll"] }""", "isn't a rules setting")]
    [InlineData("""{ "reasons": { "asi": 5 } }""", "must map names to text")]
    [InlineData("""{ "frameworks": [""] }""", "non-empty strings")]
    [InlineData("""not json""", "isn't valid JSON")]
    public void Mistakes_are_explained_with_the_file_name(string json, string expected)
    {
        var path = WriteRules(json);
        var ex = Assert.Throws<InvalidOperationException>(() => RuleSet.Load(path));
        Assert.Contains(expected, ex.Message);
        Assert.Contains(path, ex.Message);
    }

    [Fact]
    public void A_new_hook_name_routes_as_a_framework_and_existing_mods_are_rerouted_on_open()
    {
        using (var ws = _sb.Init())
        {
            ws.Library.Import(_sb.Zip("dotnet-v2", ("ScriptHookRDR2DotNet.asi", "v2 hook")));
            var f = ws.Store.GetModFiles(ws.Store.GetMods().Single().Id).Single();
            Assert.Equal(("Asi", "4"), (f.Kind, f.RuleId)); // unknown name: just an ASI plugin
        }

        File.WriteAllText(Workspace.RulesPath(_sb.Home), """
            { "frameworks": ["ScriptHookRDR2.dll", "dinput8.dll", "version.dll", "vfs.asi", "ScriptHookRDRDotNet.asi", "ScriptHookRDRNetAPI.dll", "ScriptHookRDR2DotNet.asi"] }
            """);
        using (var ws = _sb.Open())
        {
            Assert.Equal(Workspace.RulesPath(_sb.Home), ws.Rules.Source);
            var f = ws.Store.GetModFiles(ws.Store.GetMods().Single().Id).Single();
            Assert.Equal(("Framework", "3"), (f.Kind, f.RuleId));
        }
    }

    [Fact]
    public void A_broken_rules_file_stops_the_workspace_opening_with_a_clear_message()
    {
        using (_sb.Init()) { }
        File.WriteAllText(Workspace.RulesPath(_sb.Home), "{ broken");
        var ex = Assert.Throws<InvalidOperationException>(() => _sb.Open());
        Assert.Contains("rules.json", ex.Message);
        Assert.Contains("delete it to use the built-in rules", ex.Message);
    }

    [Fact]
    public void A_renamed_dotnet_api_is_recognised_through_the_prefix_list()
    {
        var dir = _sb.Folder("v2-script");
        Sandbox.WriteBytes(dir, "HorsePersonalities.dll", Dlls.Script); // references "ScriptHookRDRNetAPI"
        var none = RuleSet.Load(WriteRules("""{ "dotNetApiReferencePrefixes": ["SomethingElse"] }"""));
        Assert.Equal("11", new Router(none).Route(dir, "x").Files.Single().RuleId);
        Assert.Equal("5", new Router().Route(dir, "x").Files.Single().RuleId);
    }
}
