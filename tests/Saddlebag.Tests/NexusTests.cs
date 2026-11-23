using System.Net;
using System.Text;
using Saddlebag.Core;
using Saddlebag.Core.Nexus;

namespace Saddlebag.Tests;

/// <summary>Stands in for api.nexusmods.com: canned JSON per path, and a record of every request.</summary>
public sealed class FakeNexus : HttpMessageHandler
{
    public Dictionary<string, (HttpStatusCode Status, string Body)> Routes { get; } = [];
    public List<HttpRequestMessage> Requests { get; } = [];
    public int Daily { get; set; } = 2400;

    public void Mod(long id, string version, string status = "published", bool available = true) =>
        Routes[$"/v1/games/reddeadredemption2/mods/{id}"] = (HttpStatusCode.OK,
            $$"""{"mod_id":{{id}},"domain_name":"reddeadredemption2","name":"Mod {{id}}","version":"{{version}}","status":"{{status}}","available":{{(available ? "true" : "false")}},"updated_timestamp":1760000000}""");

    public void Files(long id, string filesJson, string updatesJson = "[]") =>
        Routes[$"/v1/games/reddeadredemption2/mods/{id}/files"] = (HttpStatusCode.OK, $$"""{"files":{{filesJson}},"file_updates":{{updatesJson}}}""");

    public static string File(long fileId, int category, string version, string fileName, long ts) =>
        $$"""{"file_id":{{fileId}},"category_id":{{category}},"category_name":"{{(category == 1 ? "MAIN" : category == 4 ? "OLD_VERSION" : "OPTIONAL")}}","name":"File {{fileId}}","version":"{{version}}","file_name":"{{fileName}}","uploaded_timestamp":{{ts}},"is_primary":false}""";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        var (status, body) = Routes.GetValueOrDefault(request.RequestUri!.AbsolutePath, (HttpStatusCode.NotFound, """{"message":"No Mod Found"}"""));
        var res = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        res.Headers.Add("x-rl-daily-remaining", (Daily--).ToString());
        res.Headers.Add("x-rl-hourly-remaining", "99");
        return Task.FromResult(res);
    }
}

public class NexusNameTests
{
    [Theory]
    [InlineData("https://www.nexusmods.com/reddeadredemption2/mods/1234", "reddeadredemption2", 1234)]
    [InlineData("nexusmods.com/reddeadredemption2/mods/70?tab=files", "reddeadredemption2", 70)]
    [InlineData("https://www.nexusmods.com/RedDeadRedemption2/mods/5180/", "reddeadredemption2", 5180)]
    public void Mod_urls_parse(string url, string game, long id) => Assert.Equal((game, id), NexusNames.ParseUrl(url));

    [Fact]
    public void Non_nexus_urls_do_not_parse() => Assert.Null(NexusNames.ParseUrl("https://www.rdr2mods.com/downloads/rdr2/tools/76-lml/"));

    [Theory]
    [InlineData("Trainer-1234-1-2-1700000000.zip", 1234L, "1.2", 1700000000L)]
    [InlineData("Better Saddles v2-87-2-0-1-1712345678.7z", 87L, "2.0.1", 1712345678L)]
    [InlineData("HorsePersonalities-4410-1-0-0-1763000000.rar", 4410L, "1.0.0", 1763000000L)]
    public void Nexus_download_names_give_id_version_and_upload_time(string name, long id, string version, long ts)
    {
        var info = NexusNames.ParseArchiveName(name)!;
        Assert.Equal((id, version, ts), (info.ModId!.Value, info.Version, info.Timestamp));
    }

    [Theory]
    [InlineData("trainer.zip")]
    [InlineData("mod-v2.zip")]
    [InlineData("pack-2024-05-01.zip")]
    public void Ordinary_names_are_not_mistaken_for_nexus_downloads(string name) => Assert.Null(NexusNames.ParseArchiveName(name));

    [Theory]
    [InlineData("1.2.9", "1.2.10", -1)]
    [InlineData("v2", "2.0", 0)]
    [InlineData("1.0.6.8470.31280", "1.0.6.8470.31280", 0)]
    [InlineData("1.3", "1.2.99", 1)]
    [InlineData("1.2b", "1.2a", 1)]
    [InlineData("10", "9", 1)]
    public void Versions_compare_like_authors_write_them(string a, string b, int expected) => Assert.Equal(expected, ModVersion.Compare(a, b));

    [Fact]
    public void Missing_versions_compare_as_unknown() => Assert.Null(ModVersion.Compare(null, "1.0"));
}

public class NexusClientTests
{
    [Fact]
    public async Task Sends_the_key_and_identifies_the_app()
    {
        var fake = new FakeNexus();
        fake.Mod(1234, "1.0");
        var c = new NexusClient("secret-key", "0.4.0", fake);
        var mod = await c.GetModAsync("reddeadredemption2", 1234);

        Assert.Equal("1.0", mod.Version);
        var req = Assert.Single(fake.Requests);
        Assert.Equal("https://api.nexusmods.com/v1/games/reddeadredemption2/mods/1234", req.RequestUri!.ToString());
        Assert.Equal("secret-key", req.Headers.GetValues("APIKEY").Single());
        Assert.Equal("Saddlebag", req.Headers.GetValues("Application-Name").Single());
        Assert.Equal("0.4.0", req.Headers.GetValues("Application-Version").Single());
        Assert.Equal((2400, 99), (c.DailyRemaining, c.HourlyRemaining));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, typeof(NexusRateLimitException))]
    [InlineData(HttpStatusCode.Unauthorized, typeof(NexusAuthException))]
    [InlineData(HttpStatusCode.NotFound, typeof(NexusNotFoundException))]
    [InlineData(HttpStatusCode.InternalServerError, typeof(NexusException))]
    public async Task Errors_map_to_clear_exceptions(HttpStatusCode status, Type expected)
    {
        var fake = new FakeNexus();
        fake.Routes["/v1/games/reddeadredemption2/mods/1"] = (status, "{}");
        var c = new NexusClient("k", "1.0", fake);
        var ex = await Assert.ThrowsAnyAsync<NexusException>(() => c.GetModAsync("reddeadredemption2", 1));
        Assert.IsType(expected, ex);
    }

    [Fact]
    public void A_missing_key_is_refused_before_any_request() =>
        Assert.Throws<NexusException>(() => new NexusClient(" ", "1.0", new FakeNexus()));
}

public class UpdateEvaluateTests
{
    private static NexusModInfo Info(string version, string status = "published", bool available = true) =>
        new(1234, "reddeadredemption2", "Trainer", version, status, available, 1760000000);

    private static NexusFile F(long id, int cat, string ver, string name, long ts) => new(id, cat, null, $"File {id}", ver, name, ts, false);

    [Fact]
    public void Follows_the_file_updates_chain_to_the_newest_file()
    {
        var link = new NexusLink { ModId = 1234, FileName = "Trainer-1234-1-0-1700000000.zip", UploadedTimestamp = 1700000000 };
        var files = new NexusFiles(
            [F(10, 4, "1.0", "Trainer-1234-1-0-1700000000.zip", 1700000000), F(11, 4, "1.1", "Trainer-1234-1-1-1710000000.zip", 1710000000),
             F(12, 1, "1.2", "Trainer-1234-1-2-1720000000.zip", 1720000000)],
            [new(10, 11, "Trainer-1234-1-1-1710000000.zip", 1710000000), new(11, 12, "Trainer-1234-1-2-1720000000.zip", 1720000000)]);

        var r = UpdateChecker.Evaluate("trainer", "Trainer", "1.0", link, Info("1.2"), files);
        Assert.Equal(UpdateStatus.UpdateAvailable, r.Status);
        Assert.Equal(("1.0", "1.2", "Trainer-1234-1-2-1720000000.zip"), (r.InstalledVersion, r.LatestVersion, r.LatestFile));
        Assert.True(r.MatchedByFile);
    }

    [Fact]
    public void Exact_file_with_no_successor_is_up_to_date_even_if_versions_look_odd()
    {
        // The author's mod-level version is stale ("0.9"), but the file you have is the current main file.
        var link = new NexusLink { ModId = 1234, UploadedTimestamp = 1720000000 };
        var files = new NexusFiles([F(12, 1, "1.2", "Trainer-1234-1-2-1720000000.zip", 1720000000)], []);
        var r = UpdateChecker.Evaluate("trainer", "Trainer", "1.2", link, Info("0.9"), files);
        Assert.Equal(UpdateStatus.UpToDate, r.Status);
    }

    [Fact]
    public void A_file_moved_to_old_versions_without_a_successor_points_at_the_newest_main_file()
    {
        var link = new NexusLink { ModId = 1234, FileName = "old.zip" };
        var files = new NexusFiles([F(10, 4, "1.0", "old.zip", 1700000000), F(20, 1, "2.0", "new.zip", 1750000000)], []);
        var r = UpdateChecker.Evaluate("trainer", "Trainer", "1.0", link, Info("2.0"), files);
        Assert.Equal(UpdateStatus.OldFile, r.Status);
        Assert.Equal("new.zip", r.LatestFile);
    }

    [Fact]
    public void Without_an_exact_file_it_compares_versions()
    {
        var link = new NexusLink { ModId = 1234 };
        var files = new NexusFiles([F(20, 1, "2.0", "new.zip", 1750000000)], []);
        Assert.Equal(UpdateStatus.UpdateAvailable, UpdateChecker.Evaluate("t", "T", "1.9", link, Info("2.0"), files).Status);
        Assert.Equal(UpdateStatus.UpToDate, UpdateChecker.Evaluate("t", "T", "2.0", link, Info("2.0"), files).Status);
        var unknown = UpdateChecker.Evaluate("t", "T", null, link, Info("2.0"), files);
        Assert.Equal(UpdateStatus.Unknown, unknown.Status);
        Assert.False(unknown.MatchedByFile);
    }

    [Fact]
    public void A_guessed_link_that_matches_no_file_says_so()
    {
        var link = new NexusLink { ModId = 1234, FileName = "x-1234-1-0-1700000000.zip", UploadedTimestamp = 1700000000, Guessed = true };
        var r = UpdateChecker.Evaluate("t", "T", "1.0", link, Info("1.0"), new NexusFiles([F(99, 1, "1.0", "other.zip", 1690000000)], []));
        Assert.Contains("guessed", r.Detail);
    }

    [Theory]
    [InlineData("hidden", true)]
    [InlineData("removed", true)]
    [InlineData("published", false)]
    public void Hidden_removed_or_unavailable_pages_are_reported(string status, bool available)
    {
        var r = UpdateChecker.Evaluate("t", "T", "1.0", new NexusLink { ModId = 1 }, Info("1.0", status, available), new NexusFiles([], []));
        Assert.Equal(UpdateStatus.Unavailable, r.Status);
    }
}

[Collection("NexusKeyEnv")]
public class UpdateCheckerTests : IDisposable
{
    private readonly Sandbox _sb = new();
    public void Dispose() => _sb.Dispose();

    private Workspace Ready() { var ws = _sb.Init(); ws.CaptureBaseline(false); return ws; }

    [Fact]
    public void Import_of_a_nexus_download_records_the_link_version_and_file()
    {
        using var ws = Ready();
        var zip = _sb.Zip("Trainer-1234-1-0-1700000000", ("Trainer.asi", "a"));
        var mod = ws.Library.Import(zip).Mod;
        var link = ws.Library.LoadManifest(mod.Id).Nexus!;
        Assert.Equal((1234L, "Trainer-1234-1-0-1700000000.zip", 1700000000L, true), (link.ModId, link.FileName, link.UploadedTimestamp!.Value, link.Guessed));
        Assert.Equal("1.0", mod.Version);

        var withUrl = ws.Library.Import(_sb.Zip("other", ("Other.asi", "o")), url: "https://www.nexusmods.com/reddeadredemption2/mods/55").Mod;
        var l2 = ws.Library.LoadManifest(withUrl.Id).Nexus!;
        Assert.Equal((55L, false, (string?)null), (l2.ModId, l2.Guessed, l2.FileName));

        var plain = ws.Library.Import(_sb.Zip("plain", ("Plain.asi", "p"))).Mod;
        Assert.Null(ws.Library.LoadManifest(plain.Id).Nexus);
    }

    [Fact]
    public async Task Checks_every_linked_mod_caches_for_an_hour_and_refresh_refetches()
    {
        using var ws = Ready();
        var trainer = ws.Library.Import(_sb.Zip("Trainer-1234-1-0-1700000000", ("Trainer.asi", "a"))).Mod.Id;
        ws.Library.Import(_sb.Zip("local", ("Local.asi", "l")));
        var fake = new FakeNexus();
        fake.Mod(1234, "1.1");
        fake.Files(1234, $"[{FakeNexus.File(10, 4, "1.0", "Trainer-1234-1-0-1700000000.zip", 1700000000)},{FakeNexus.File(11, 1, "1.1", "Trainer-1234-1-1-1710000000.zip", 1710000000)}]",
            """[{"old_file_id":10,"new_file_id":11,"new_file_name":"Trainer-1234-1-1-1710000000.zip","uploaded_timestamp":1710000000}]""");
        var checker = new UpdateChecker(ws.Store, ws.Library, new NexusClient("k", "1.0", fake));

        var results = await checker.CheckAsync();
        Assert.Equal(UpdateStatus.UpdateAvailable, results.Single(r => r.ModId == trainer).Status);
        Assert.Equal(UpdateStatus.NotLinked, results.Single(r => r.ModId != trainer).Status);
        Assert.Equal(2, fake.Requests.Count); // mod info + file list

        await checker.CheckAsync();
        Assert.Equal(2, fake.Requests.Count); // served from the cache
        await checker.CheckAsync(refresh: true);
        Assert.Equal(4, fake.Requests.Count);
    }

    [Fact]
    public async Task Rate_limit_stops_further_requests_and_bad_key_fails_once()
    {
        using var ws = Ready();
        for (var i = 1; i <= 3; i++) ws.Library.Import(_sb.Zip($"m{i}", ($"M{i}.asi", $"{i}")), url: $"https://www.nexusmods.com/reddeadredemption2/mods/{i}");
        var fake = new FakeNexus();
        fake.Routes["/v1/games/reddeadredemption2/mods/1"] = (HttpStatusCode.TooManyRequests, "{}");
        var results = await new UpdateChecker(ws.Store, ws.Library, new NexusClient("k", "1.0", fake)).CheckAsync();
        Assert.Single(fake.Requests);
        Assert.All(results, r => Assert.Equal(UpdateStatus.Error, r.Status));
        Assert.Contains(results, r => r.Detail.Contains("skipped"));

        var bad = new FakeNexus();
        bad.Routes["/v1/games/reddeadredemption2/mods/1"] = (HttpStatusCode.Unauthorized, "{}");
        await Assert.ThrowsAsync<NexusAuthException>(() => new UpdateChecker(ws.Store, ws.Library, new NexusClient("k", "1.0", bad)).CheckAsync());
    }

    private sealed class Offline : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls++;
            throw new HttpRequestException("no route to host");
        }
    }

    [Fact]
    public async Task No_connection_stops_after_the_first_failure()
    {
        using var ws = Ready();
        for (var i = 1; i <= 3; i++) ws.Library.Import(_sb.Zip($"n{i}", ($"N{i}.asi", $"{i}")), url: $"https://www.nexusmods.com/reddeadredemption2/mods/{i}");
        var offline = new Offline();
        var results = await new UpdateChecker(ws.Store, ws.Library, new NexusClient("k", "1.0", offline)).CheckAsync();
        Assert.Equal(1, offline.Calls);
        Assert.Equal(2, results.Count(r => r.Detail == "skipped: Nexus unreachable"));
    }

    [Fact]
    public async Task Unknown_mod_id_is_an_error_for_that_mod_only()
    {
        using var ws = Ready();
        ws.Library.Import(_sb.Zip("gone", ("Gone.asi", "g")), url: "https://www.nexusmods.com/reddeadredemption2/mods/999");
        var r = Assert.Single(await new UpdateChecker(ws.Store, ws.Library, new NexusClient("k", "1.0", new FakeNexus())).CheckAsync());
        Assert.Equal(UpdateStatus.Error, r.Status);
        Assert.Contains("no mod 999", r.Detail);
    }

    [Fact]
    public void Linking_later_records_url_and_version()
    {
        using var ws = Ready();
        var id = ws.Library.Import(_sb.Zip("trainer", ("Trainer.asi", "a"))).Mod.Id;
        ws.Library.SetNexusLink(id, new NexusLink { ModId = 1234 }, version: "1.0");
        Assert.Equal("1.0", ws.Store.GetMod(id)!.Version);
        Assert.Equal("https://www.nexusmods.com/reddeadredemption2/mods/1234?tab=files", ws.Store.GetMod(id)!.SourceUrl);
        ws.Library.SetNexusLink(id, null);
        Assert.Null(ws.Library.LoadManifest(id).Nexus);
    }

    [Fact]
    public void Key_store_prefers_the_environment_and_never_stores_plain_text_off_windows()
    {
        var store = new NexusKeyStore(_sb.Root);
        var old = Environment.GetEnvironmentVariable(NexusKeyStore.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(NexusKeyStore.EnvVar, " from-env ");
            Assert.Equal(("from-env", "$SADDLEBAG_NEXUS_KEY"), store.Load());
            Environment.SetEnvironmentVariable(NexusKeyStore.EnvVar, null);
            Assert.Null(store.Load().Key);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Throws<InvalidOperationException>(() => store.Save("abc"));
                Assert.False(File.Exists(store.KeyFile));
            }
        }
        finally { Environment.SetEnvironmentVariable(NexusKeyStore.EnvVar, old); }
    }
}
