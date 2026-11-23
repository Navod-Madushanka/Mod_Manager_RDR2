using System.Xml.Linq;
using Saddlebag.Core;
using Saddlebag.Core.Baseline;
using Saddlebag.Core.Deploy;
using Saddlebag.Core.Routing;

namespace Saddlebag.Tests;

public class InstallXmlTests : IDisposable
{
    private readonly Sandbox _sb = new();
    public void Dispose() => _sb.Dispose();

    [Fact]
    public void Generated_file_uses_the_EasyInstall_layout_real_RDR2_mods_use()
    {
        var xml = InstallXml.Generate("Coats", "Navod", "1.2", "https://example.org/coats", "Packaged by Saddlebag", "stream",
            [new FileReplacement("update:/x64/data/metadata/cameras.ymt", "replace/update/x64/data/metadata/cameras.ymt")]);

        var root = XDocument.Parse(xml).Root!;
        Assert.Equal("EasyInstall", root.Name.LocalName);
        Assert.Equal(["Name", "Author", "Version", "Link", "Metadata", "Resources"], root.Elements().Select(e => e.Name.LocalName));
        var resource = root.Element("Resources")!.Element("Resource")!;
        Assert.Equal("stream", resource.Element("StreamingFiles")!.Value);
        var rep = resource.Element("FileReplacement")!;
        Assert.Equal("update:/x64/data/metadata/cameras.ymt", rep.Element("GamePath")!.Value);
        Assert.Equal("replace/update/x64/data/metadata/cameras.ymt", rep.Element("FilePath")!.Value);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml);
    }

    [Fact]
    public void Reader_tolerates_what_real_files_contain()
    {
        // Shaped like published RDR2 packages: XML declaration, comments, odd indentation, a stray '>' after an element.
        var path = Sandbox.Write(_sb.Src, "install.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <EasyInstall>
                <Name>Bigger Horses</Name>
                <Author>someone</Author>
                <Version>1.0</Version>
                <Link></Link>
                <Metadata>
                </Metadata>
                <Resources>
                    <Resource>
                        <!-- models -->
                        <StreamingFiles>stream</StreamingFiles>
                        <FileReplacement>
                            <GamePath>update:/x64/packs/base/data/ai/weapons.ymt</GamePath>
                            <FilePath>weapons.ymt</FilePath>
                        </FileReplacement>>
            		</Resource>
            	</Resources>
            </EasyInstall>
            """);
        var info = InstallXml.Read(path)!;
        Assert.Equal(("Bigger Horses", "1.0"), (info.Name, info.Version));
        Assert.Equal(["stream"], info.StreamingFolders);
        Assert.Equal(new FileReplacement("update:/x64/packs/base/data/ai/weapons.ymt", "weapons.ymt"), Assert.Single(info.Replacements));

        Sandbox.Write(_sb.Src, "bad.xml", "<EasyInstall><Name>broken");
        Assert.Null(InstallXml.Read(Path.Combine(_sb.Src, "bad.xml")));
    }

    [Theory]
    [InlineData("update:/x64/data/metadata/cameras.ymt", true)]
    [InlineData("dlc_mp009:/content.xml", true)]
    [InlineData("x64/data/metadata/cameras.ymt", false)]
    [InlineData("update:/../RDR2.exe", false)]
    [InlineData("update:/x64/data/", false)]
    [InlineData("C:/Games/RDR2/x.ymt", false)]
    public void Game_paths_need_a_mount(string path, bool ok) =>
        Assert.Equal(ok, InstallXml.IsGamePath(path));
}

public class LmlPackTests : IDisposable
{
    private readonly Sandbox _sb = new();
    public void Dispose() => _sb.Dispose();

    private Workspace Ready()
    {
        var ws = _sb.Init();
        ws.CaptureBaseline(false);
        _sb.EnableFrameworks(ws, lml: true);
        return ws;
    }

    private string Enable(Workspace ws, string zipName, params (string, string)[] files)
    {
        var id = ws.Library.Import(_sb.Zip(zipName, files)).Mod.Id;
        ws.Store.SetEnabled(id, true);
        return id;
    }

    [Fact]
    public void Loose_stream_mod_becomes_an_lml_package_with_install_xml()
    {
        using var ws = Ready();
        var id = Enable(ws, "Coats", ("Coats/textures/coat_01.ytd", "c1"), ("Coats/models/coat_01.ydr", "m1"), ("Coats/readme.txt", "doc"));

        var plan = ws.Library.SetLmlPack(id, new LmlPackOptions { Enabled = true, Author = "Navod" });
        Assert.Equal("lml-pack", plan.PackageType);
        ws.Deploy();

        Assert.True(File.Exists(_sb.GameFile("lml/Coats/stream/coat_01.ytd")));
        Assert.True(File.Exists(_sb.GameFile("lml/Coats/stream/coat_01.ydr")));
        Assert.False(File.Exists(_sb.GameFile("lml/stream/coat_01.ytd")), "no longer loose in lml/stream");
        var info = InstallXml.Read(_sb.GameFile("lml/Coats/install.xml"))!;
        Assert.Equal(("Coats", "Navod"), (info.Name, info.Author));
        Assert.Equal(["stream"], info.StreamingFolders);
        Assert.DoesNotContain(ws.Deployer.Plan().Checks, c => c.Id == DeployChecks.BrokenInstallXml);

        // The generated file lives in Saddlebag's folder, not among the mod's own files.
        Assert.False(File.Exists(Path.Combine(ws.Library.FilesRoot(ws.Store.GetMod(id)!), "install.xml")));
        Assert.Equal("copy", ws.Store.GetDeployed().Single(d => d.DestPath == "lml/Coats/install.xml").Method);
    }

    [Fact]
    public void Mapped_data_file_becomes_a_FileReplacement_and_needs_no_other_answer()
    {
        using var ws = Ready();
        var id = Enable(ws, "No Black Bars", ("cameras.ymt", "cams"));
        Assert.Throws<DeployException>(() => ws.Deploy()); // loose .ymt: rule 8 asks where it goes

        var opt = new LmlPackOptions { Enabled = true };
        opt.GamePaths["cameras.ymt"] = "update:/x64/data/metadata/cameras.ymt";
        ws.Library.SetLmlPack(id, opt);
        ws.Deploy();

        const string inPkg = "replace/update/x64/data/metadata/cameras.ymt";
        Assert.Equal("cams", File.ReadAllText(_sb.GameFile("lml/No Black Bars/" + inPkg)));
        var info = InstallXml.Read(_sb.GameFile("lml/No Black Bars/install.xml"))!;
        Assert.Empty(info.StreamingFolders);
        Assert.Equal(new FileReplacement("update:/x64/data/metadata/cameras.ymt", inPkg), Assert.Single(info.Replacements));
        Assert.Equal("update:/x64/data/metadata/cameras.ymt", ws.Library.LoadManifest(id).LmlPack!.GamePaths["CAMERAS.ymt"]);
    }

    [Fact]
    public void Bad_options_are_refused_and_leave_the_mod_as_it_was()
    {
        using var ws = Ready();
        var id = Enable(ws, "Coats", ("coat.ytd", "c"));
        var bad = new LmlPackOptions { Enabled = true };
        bad.GamePaths["coat.ytd"] = "x64/no/mount.ytd";
        Assert.Throws<InvalidOperationException>(() => ws.Library.SetLmlPack(id, bad));
        Assert.Null(ws.Library.LoadManifest(id).LmlPack);
        Assert.Equal("lml/stream/coat.ytd", ws.Store.GetModFiles(id).Single().DestPath);

        var unknown = new LmlPackOptions { Enabled = true };
        unknown.GamePaths["nope.ymt"] = "update:/x.ymt";
        Assert.Throws<InvalidOperationException>(() => ws.Library.SetLmlPack(id, unknown));
    }

    [Fact]
    public void Nothing_to_package_or_already_packaged_is_refused()
    {
        using var ws = Ready();
        var asi = Enable(ws, "Trainer", ("Trainer.asi", "a"));
        Assert.Throws<InvalidOperationException>(() => ws.Library.SetLmlPack(asi, new LmlPackOptions { Enabled = true }));

        var pkg = Enable(ws, "Saddles", ("Saddles/install.xml", "<EasyInstall/>"), ("Saddles/stream/s.ytd", "s"));
        var ex = Assert.Throws<InvalidOperationException>(() => ws.Library.SetLmlPack(pkg, new LmlPackOptions { Enabled = true }));
        Assert.Contains("already ships", ex.Message);
    }

    [Fact]
    public void Same_stream_name_twice_in_one_mod_is_refused()
    {
        using var ws = Ready();
        var id = Enable(ws, "Dupes", ("a/horse.ytd", "1"), ("b/horse.ytd", "2"));
        var ex = Assert.Throws<InvalidOperationException>(() => ws.Library.SetLmlPack(id, new LmlPackOptions { Enabled = true }));
        Assert.Contains("horse.ytd", ex.Message);
    }

    [Fact]
    public void Legacy_replace_files_stay_put_with_a_note()
    {
        using var ws = Ready();
        var id = Enable(ws, "Mixed", ("coat.ytd", "c"), ("replace/x64/data/hud.ymt", "h"));
        ws.Library.SetLmlPack(id, new LmlPackOptions { Enabled = true });
        Assert.Contains(ws.Store.GetModFiles(id), f => f.DestPath == "lml/replace/x64/data/hud.ymt");
        Assert.Contains(ws.Library.LoadManifest(id).Notes, n => n.Contains("lml/replace/"));
    }

    [Fact]
    public void Turning_packaging_off_moves_files_back_and_repacking_unchanged_moves_nothing()
    {
        using var ws = Ready();
        var id = Enable(ws, "Coats", ("coat.ytd", "c"));
        ws.Library.SetLmlPack(id, new LmlPackOptions { Enabled = true });
        ws.Deploy();

        ws.Library.Reroute(id); // same options again: install.xml content and hash unchanged
        Assert.True(ws.Deployer.Plan().IsEmpty);

        ws.Library.SetLmlPack(id, null);
        var r = ws.Deploy();
        Assert.Equal((2, 1), (r.Removed, r.Placed));
        Assert.True(File.Exists(_sb.GameFile("lml/stream/coat.ytd")));
        Assert.False(Directory.Exists(_sb.GameFile("lml/Coats")));
        Assert.False(Directory.Exists(ws.Library.GeneratedDir(id)));
    }

    [Fact]
    public void Packaging_a_linked_dev_folder_never_writes_into_it()
    {
        using var ws = Ready();
        var build = _sb.Folder("bin/Release", ("horse_tex.ytd", "t"));
        var id = ws.Library.Link(build, "Horse Textures").Mod.Id;
        ws.Store.SetEnabled(id, true);
        ws.Library.SetLmlPack(id, new LmlPackOptions { Enabled = true });
        ws.Deploy();
        Assert.Equal(["horse_tex.ytd"], Directory.GetFiles(build).Select(Path.GetFileName));
        Assert.True(File.Exists(_sb.GameFile("lml/Horse Textures/install.xml")));
    }

    [Fact]
    public void Online_mode_removes_the_package_and_goes_green()
    {
        using var ws = Ready();
        var id = Enable(ws, "Coats", ("coat.ytd", "c"));
        ws.Library.SetLmlPack(id, new LmlPackOptions { Enabled = true });
        ws.GoStory();
        Assert.Equal(VerifyStatus.Green, ws.GoOnline().Report.Status);
        Assert.False(Directory.Exists(_sb.GameFile("lml")));
    }

    // ---------- checks that read install.xml ----------

    [Fact]
    public void Two_packages_replacing_one_game_path_are_flagged()
    {
        using var ws = Ready();
        const string xml = """
            <EasyInstall><Name>{0}</Name><Resources><Resource>
              <FileReplacement><GamePath>update:/x64/data/metadata/cameras.ymt</GamePath><FilePath>cameras.ymt</FilePath></FileReplacement>
            </Resource></Resources></EasyInstall>
            """;
        Enable(ws, "bars-a", ("BarsA/install.xml", string.Format(xml, "A")), ("BarsA/cameras.ymt", "a"));
        Enable(ws, "bars-b", ("BarsB/install.xml", string.Format(xml, "B")), ("BarsB/cameras.ymt", "b"));

        var f = Assert.Single(ws.Deployer.Plan().Checks, c => c.Id == DeployChecks.ReplaceTarget);
        Assert.Contains("update:/x64/data/metadata/cameras.ymt", f.Message);
        Assert.Contains("bars-a and bars-b", f.Message);
    }

    [Fact]
    public void Install_xml_pointing_at_missing_files_or_not_xml_is_flagged()
    {
        using var ws = Ready();
        Enable(ws, "holey", ("Holey/install.xml", """
            <EasyInstall><Resources><Resource>
              <StreamingFiles>stream</StreamingFiles>
              <FileReplacement><GamePath>update:/x64/a.ymt</GamePath><FilePath>a.ymt</FilePath></FileReplacement>
            </Resource></Resources></EasyInstall>
            """), ("Holey/readme.txt", "no files here"));
        Enable(ws, "garbled", ("Garbled/install.xml", "<EasyInstall><Name>oops"), ("Garbled/x.ytd", "x"));

        var broken = ws.Deployer.Plan().Checks.Where(c => c.Id == DeployChecks.BrokenInstallXml).ToList();
        Assert.Equal(2, broken.Count);
        Assert.Contains(broken, c => c.Message.Contains("a.ymt") && c.Message.Contains("stream/"));
        Assert.Contains(broken, c => c.Message.Contains("isn't valid XML"));
    }
}
