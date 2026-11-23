using System.Text;
using Saddlebag.Core;
using Saddlebag.Core.Baseline;
using Saddlebag.Core.Logs;

namespace Saddlebag.Tests;

public class LogReadingTests : IDisposable
{
    private readonly Sandbox _sb = new();
    public void Dispose() => _sb.Dispose();

    [Theory]
    [InlineData("[12:00:01] Unhandled exception in HorsePersonalities.Main.OnTick", LogLevel.Error)]
    [InlineData("Failed to load plugin Trainer.asi", LogLevel.Error)]
    [InlineData("FATAL: access violation at 0x7FF6", LogLevel.Error)]
    [InlineData("WARNING: config value missing, using default", LogLevel.Warn)]
    [InlineData("Loaded 12 scripts", LogLevel.Info)]
    [InlineData("errorlevel=0 terrorist", LogLevel.Info)] // words, not substrings
    public void Lines_are_classified_by_whole_words(string line, LogLevel level) => Assert.Equal(level, LogService.Classify(line));

    [Fact]
    public void Tail_reads_only_the_end_of_a_large_file()
    {
        var path = Path.Combine(_sb.Src, "big.log");
        using (var w = new StreamWriter(path))
            for (var i = 1; i <= 200_000; i++) w.Write($"line {i}\r\n");
        var tail = LogService.Tail(path, 3);
        Assert.Equal(["line 199998", "line 199999", "line 200000"], tail);
    }

    [Fact]
    public void Tail_handles_short_files_missing_final_newline_and_utf16()
    {
        var a = Sandbox.Write(_sb.Src, "a.log", "one\ntwo\nthree");
        Assert.Equal(["one", "two", "three"], LogService.Tail(a, 10));
        Assert.Equal(["three"], LogService.Tail(a, 1));

        var u = Path.Combine(_sb.Src, "u.log");
        File.WriteAllText(u, "première\r\nseconde\r\n", Encoding.Unicode); // UTF-16 LE with BOM
        Assert.Equal(["première", "seconde"], LogService.Tail(u, 10));
    }

    [Fact]
    public void A_log_still_open_for_writing_can_be_read()
    {
        var path = Path.Combine(_sb.Src, "live.log");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        writer.Write("hello\n"u8);
        writer.Flush();
        Assert.Equal(["hello"], LogService.Tail(path, 5));
    }

    [Fact]
    public void Follower_returns_new_lines_waits_for_partial_lines_and_notices_restarts()
    {
        var path = Sandbox.Write(_sb.Src, "f.log", "old line\n");
        var f = new LogFollower(path);
        Assert.Empty(f.Poll().Lines); // starts at the end

        File.AppendAllText(path, "new 1\nnew 2\npart");
        Assert.Equal(["new 1", "new 2"], f.Poll().Lines);
        File.AppendAllText(path, "ial\n");
        Assert.Equal(["partial"], f.Poll().Lines);

        File.WriteAllText(path, "game restarted\n"); // the game recreates its log on start
        var c = f.Poll();
        Assert.True(c.Restarted);
        Assert.Equal(["game restarted"], c.Lines);

        File.Delete(path);
        Assert.Empty(f.Poll().Lines);
    }
}

public class LogServiceTests : IDisposable
{
    private readonly Sandbox _sb = new();
    public void Dispose() => _sb.Dispose();

    private Workspace Ready(out string trainer, out string horse)
    {
        var ws = _sb.Init();
        ws.CaptureBaseline(false);
        _sb.EnableFrameworks(ws, lml: true);
        trainer = ws.Library.Import(_sb.Zip("Trainer", ("Trainer.asi", "a"), ("Trainer.ini", "i"))).Mod.Id;
        ws.Store.SetEnabled(trainer, true);
        var dir = _sb.Folder("hp");
        Sandbox.WriteBytes(dir, "HorsePersonalities.dll", Dlls.Script);
        horse = ws.Library.Import(dir, name: "Horse Personalities").Mod.Id;
        ws.Store.SetEnabled(horse, true);
        ws.GoStory();
        return ws;
    }

    [Fact]
    public void Discovers_logs_and_guesses_who_wrote_them()
    {
        using var ws = Ready(out var trainer, out _);
        Sandbox.Write(_sb.Game, "ScriptHookRDR2.log", "init ok\n");
        Sandbox.Write(_sb.Game, "Trainer.log", "loaded\n");
        Sandbox.Write(_sb.Game, "ScriptHookRDRDotNet.log", "dotnet\n");
        Sandbox.Write(_sb.Game, "lml/lml.log", "vfs\n");
        Sandbox.Write(_sb.Game, "x64/data/deep.log", "not a place mods write; not scanned\n");

        var logs = ws.Logs.Discover();
        Assert.Equal(["ScriptHookRDR2.log", "ScriptHookRDRDotNet.log", "Trainer.log", "lml/lml.log"], logs.Select(l => l.GamePath).Order(StringComparer.Ordinal));
        Assert.Equal(trainer, logs.Single(l => l.GamePath == "Trainer.log").OwnerModId);
        Assert.StartsWith("scripthook", logs.Single(l => l.GamePath == "ScriptHookRDR2.log").OwnerName);
        Assert.StartsWith("scripthook", logs.Single(l => l.GamePath == "ScriptHookRDRDotNet.log").OwnerName); // longest stem wins
    }

    [Fact]
    public void Summary_counts_errors_and_names_the_mods_they_mention()
    {
        using var ws = Ready(out _, out _);
        Sandbox.Write(_sb.Game, "ScriptHookRDRDotNet.log", """
            [10:00:00] Loading scripts
            [10:00:01] Loaded HorsePersonalities.dll
            [10:00:05] WARNING: slow tick
            [10:01:00] Unhandled exception in script HorsePersonalities.Main: NullReferenceException
            [10:01:00]    at HorsePersonalities.Main.OnTick()
            [10:02:00] Failed to load Trainer.asi config
            """);
        var log = ws.Logs.Find("ScriptHookRDRDotNet");
        var s = ws.Logs.Summarize(log);
        Assert.Equal((2, 1), (s.Errors, s.Warnings));
        Assert.Equal(1, s.ModMentions["Horse Personalities"]);
        Assert.Equal(1, s.ModMentions["Trainer"]);
        Assert.Contains("Trainer.asi", s.LastError);

        var errors = ws.Logs.Read(log, 50, errorsOnly: true);
        Assert.Equal([4, 6], errors.Select(e => e.Number));
        Assert.Equal(["Horse Personalities"], errors[0].MentionedMods);

        Assert.Equal([2, 4, 5], ws.Logs.Read(log, 50, grep: "horsepersonalities").Select(l => l.Number));
        Assert.Equal([5, 6], ws.Logs.Read(log, 2).Select(l => l.Number));
    }

    [Fact]
    public void Online_mode_archives_mod_logs_goes_green_and_keeps_them_viewable()
    {
        using var ws = Ready(out _, out _);
        Sandbox.Write(_sb.Game, "ScriptHookRDR2.log", "played a session\n");
        Sandbox.Write(_sb.Game, "lml/lml.log", "lml session\n");

        var online = ws.GoOnline();
        Assert.Equal(VerifyStatus.Green, online.Report.Status);
        Assert.Equal(["ScriptHookRDR2.log", "lml/lml.log"], online.ArchivedLogs!.Order(StringComparer.Ordinal));
        Assert.False(File.Exists(_sb.GameFile("ScriptHookRDR2.log")));
        Assert.False(Directory.Exists(_sb.GameFile("lml")));

        var archived = ws.Logs.Find("ScriptHookRDR2.log");
        Assert.True(archived.Archived);
        Assert.StartsWith("archive/", archived.Display);
        Assert.Equal(["played a session"], ws.Logs.Read(archived, 10).Select(l => l.Text));
    }

    [Fact]
    public void Vanilla_and_allowed_logs_are_left_in_place()
    {
        Sandbox.Write(_sb.Game, "launcher_vanilla.log", "shipped with the game");
        using var ws = _sb.Init();
        ws.CaptureBaseline(false);
        Sandbox.Write(_sb.Game, "keepme.log", "x");
        ws.Store.SetPathRule("keepme.log", Saddlebag.Core.State.StateStore.RuleAllow);

        var online = ws.GoOnline();
        Assert.Empty(online.ArchivedLogs!);
        Assert.True(File.Exists(_sb.GameFile("launcher_vanilla.log")));
        Assert.True(ws.Logs.Discover().Single(l => l.GamePath == "launcher_vanilla.log").Vanilla);
        Assert.Equal(VerifyStatus.Amber, online.Report.Status); // the allowed extra
    }

    [Fact]
    public void Find_prefers_live_logs_and_explains_ambiguity()
    {
        using var ws = Ready(out _, out _);
        Sandbox.Write(_sb.Game, "ScriptHookRDR2.log", "old session\n");
        ws.GoOnline();                       // archives it
        ws.GoStory();
        Sandbox.Write(_sb.Game, "ScriptHookRDR2.log", "new session\n");
        Assert.False(ws.Logs.Find("ScriptHookRDR2.log").Archived);

        Sandbox.Write(_sb.Game, "scripts/ScriptHookRDR2.log", "odd copy\n");
        Assert.Equal("ScriptHookRDR2.log", ws.Logs.Find("ScriptHookRDR2.log").Display); // an exact path is never ambiguous
        Assert.Equal("scripts/ScriptHookRDR2.log", ws.Logs.Find("scripts/ScriptHookRDR2.log").Display);
        var ex = Assert.Throws<InvalidOperationException>(() => ws.Logs.Find("ScriptHookRDR2"));
        Assert.Contains("scripts/ScriptHookRDR2.log", ex.Message);
        Assert.Throws<InvalidOperationException>(() => ws.Logs.Find("nothing-like-this"));
    }
}
