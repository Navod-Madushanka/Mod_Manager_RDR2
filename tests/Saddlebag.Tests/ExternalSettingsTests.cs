using Saddlebag.Core.Baseline;
using Saddlebag.Core.Safety;

namespace Saddlebag.Tests;

public class ExternalSettingsTests : IDisposable
{
    private readonly Sandbox _sb = new();
    public void Dispose() => _sb.Dispose();

    private void WriteSystemXml(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_sb.SystemXml)!);
        File.WriteAllText(_sb.SystemXml, content);
    }

    [Fact]
    public void Baseline_captures_system_xml_and_verify_reports_changes_without_going_red()
    {
        WriteSystemXml("<rage__fwuiSettings><vr>false</vr></rage__fwuiSettings>");
        using var ws = _sb.Init();
        ws.CaptureBaseline(false);
        var rec = Assert.Single(ws.Baseline.External!.Records());
        Assert.Equal("Documents/Rockstar Games/Red Dead Redemption 2/Settings/system.xml", rec.Display);
        Assert.Equal(VerifyStatus.Green, ws.Baseline.Verify().Status);

        WriteSystemXml("<rage__fwuiSettings><vr>true</vr></rage__fwuiSettings>"); // what a VR mod might do
        var r = ws.Baseline.Verify();
        Assert.Equal(VerifyStatus.Green, r.Status); // settings don't make the game folder dirty
        var f = Assert.Single(r.Findings);
        Assert.Equal(FindingKind.SettingsChanged, f.Kind);
        Assert.Empty(r.Offenders);
    }

    [Fact]
    public void Same_size_edit_is_caught_by_deep_verify_even_with_the_old_time()
    {
        WriteSystemXml("<a>1</a>");
        using var ws = _sb.Init();
        ws.CaptureBaseline(false);
        var t = File.GetLastWriteTimeUtc(_sb.SystemXml);
        WriteSystemXml("<a>2</a>");
        File.SetLastWriteTimeUtc(_sb.SystemXml, t);
        Assert.Empty(ws.Baseline.Verify().Findings);
        Assert.Single(ws.Baseline.Verify(deep: true).Findings);
    }

    [Fact]
    public void Restore_puts_the_baseline_copy_back_and_saves_the_current_one()
    {
        WriteSystemXml("flat");
        using var ws = _sb.Init();
        ws.CaptureBaseline(false);
        WriteSystemXml("vr mode");

        var saved = ws.RestoreSettings("Documents/Rockstar Games/Red Dead Redemption 2/Settings/system.xml");
        Assert.Equal("flat", File.ReadAllText(_sb.SystemXml));
        Assert.Equal("vr mode", File.ReadAllText(saved!));
        Assert.Empty(ws.Baseline.Verify().Findings);
    }

    [Fact]
    public void Restore_recreates_a_deleted_file_and_is_blocked_while_the_game_runs()
    {
        WriteSystemXml("flat");
        using var ws = _sb.Init();
        ws.CaptureBaseline(false);
        File.Delete(_sb.SystemXml);
        Assert.Contains("deleted", Assert.Single(ws.Baseline.Verify().Findings).Detail);

        _sb.Guard.Running.Add("RDR2");
        Assert.Throws<GameRunningException>(() => ws.RestoreSettings("Rockstar Games/Red Dead Redemption 2/Settings/system.xml"));
        _sb.Guard.Running.Clear();
        Assert.Null(ws.RestoreSettings("Rockstar Games/Red Dead Redemption 2/Settings/system.xml")); // nothing to save first
        Assert.Equal("flat", File.ReadAllText(_sb.SystemXml));
    }

    [Fact]
    public void No_settings_file_means_nothing_captured_and_nothing_reported()
    {
        using var ws = _sb.Init();
        ws.CaptureBaseline(false);
        Assert.Empty(ws.Baseline.External!.Records());
        Assert.Empty(ws.Baseline.Verify().Findings);
        Assert.Throws<InvalidOperationException>(() => ws.RestoreSettings("Documents/whatever.xml"));
    }

    [Fact]
    public void Online_mode_reports_changed_settings_but_stays_green()
    {
        WriteSystemXml("flat");
        using var ws = _sb.Init();
        ws.CaptureBaseline(false);
        WriteSystemXml("changed");
        var online = ws.GoOnline();
        Assert.Equal(VerifyStatus.Green, online.Report.Status);
        Assert.Contains(online.Report.Findings, f => f.Kind == FindingKind.SettingsChanged);
    }
}
