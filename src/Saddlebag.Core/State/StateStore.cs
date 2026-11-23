using Microsoft.Data.Sqlite;

namespace Saddlebag.Core.State;

public sealed record ModRecord(string Id, string Name, string? Version, string? SourceUrl, string? ArchiveSha256, DateTime InstalledAt, string? LinkedPath)
{
    public bool IsLinked => LinkedPath is not null;
}

public sealed record ModFileRecord(string ModId, string LibraryPath, string? DestPath, string Sha256, long Size, string Kind, string RuleId, string Confidence);

public sealed record DeployedRecord(string DestPath, string ModId, string LibraryPath, string Sha256, long Size, long MTime, string Method, DateTime DeployedAt);

public sealed record BackupRecord(string DestPath, string BackupPath, string Sha256, string ReplacedByMod);

public sealed record BaselineRecord(string Path, long Size, long MTime, string Sha256);

public sealed record ProfileModRecord(string ModId, bool Enabled, int Priority);

public sealed record JournalStepRecord(string OpId, int Step, string Status, string Payload);

/// <summary>
/// All state lives in one SQLite file so the ledger and the journal commit atomically.
/// </summary>
public sealed class StateStore : IDisposable
{
    public const string DefaultProfile = "default";
    private readonly SqliteConnection _db;

    public StateStore(string dbPath)
    {
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;");
        Migrate();
    }

    public void Dispose() => _db.Dispose();

    private void Migrate()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS mods (
                id TEXT PRIMARY KEY, name TEXT NOT NULL, version TEXT, source_url TEXT,
                archive_sha256 TEXT, installed_at TEXT NOT NULL, linked_path TEXT);
            CREATE TABLE IF NOT EXISTS mod_files (
                mod_id TEXT NOT NULL REFERENCES mods(id) ON DELETE CASCADE,
                library_path TEXT NOT NULL COLLATE NOCASE, dest_path TEXT COLLATE NOCASE,
                sha256 TEXT NOT NULL, size INTEGER NOT NULL, kind TEXT NOT NULL,
                rule_id TEXT NOT NULL, confidence TEXT NOT NULL,
                PRIMARY KEY (mod_id, library_path));
            CREATE TABLE IF NOT EXISTS deployed (
                dest_path TEXT PRIMARY KEY COLLATE NOCASE, mod_id TEXT NOT NULL, library_path TEXT NOT NULL,
                sha256 TEXT NOT NULL, size INTEGER NOT NULL, mtime INTEGER NOT NULL,
                method TEXT NOT NULL, deployed_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS backups (
                dest_path TEXT PRIMARY KEY COLLATE NOCASE, backup_path TEXT NOT NULL,
                sha256 TEXT NOT NULL, replaced_by_mod TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS baseline (
                path TEXT PRIMARY KEY COLLATE NOCASE, size INTEGER NOT NULL, mtime INTEGER NOT NULL,
                sha256 TEXT NOT NULL, game_version TEXT, captured_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS profiles (id TEXT PRIMARY KEY, name TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS profile_mods (
                profile_id TEXT NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
                mod_id TEXT NOT NULL REFERENCES mods(id) ON DELETE CASCADE,
                enabled INTEGER NOT NULL, priority INTEGER NOT NULL,
                PRIMARY KEY (profile_id, mod_id));
            CREATE TABLE IF NOT EXISTS journal_ops (
                op_id TEXT PRIMARY KEY, kind TEXT NOT NULL, status TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS journal (
                op_id TEXT NOT NULL REFERENCES journal_ops(op_id) ON DELETE CASCADE,
                step INTEGER NOT NULL, status TEXT NOT NULL, payload TEXT NOT NULL,
                PRIMARY KEY (op_id, step));
            CREATE TABLE IF NOT EXISTS path_rules (
                path TEXT PRIMARY KEY COLLATE NOCASE, rule TEXT NOT NULL);
            INSERT OR IGNORE INTO profiles (id, name) VALUES ('default', 'Default');
            """);
    }

    // ---------- plumbing ----------

    private SqliteTransaction? _tx;

    /// <summary>Starts a transaction that every command on this store joins until it is committed or disposed.</summary>
    public Tx Begin()
    {
        if (_tx is not null) throw new InvalidOperationException("Nested transactions are not supported.");
        _tx = _db.BeginTransaction();
        return new Tx(this);
    }

    public sealed class Tx(StateStore owner) : IDisposable
    {
        public void Commit() { owner._tx!.Commit(); Dispose(); }
        public void Dispose()
        {
            if (owner._tx is null) return;
            owner._tx.Dispose();
            owner._tx = null;
        }
    }

    private SqliteCommand Cmd(string sql, params (string, object?)[] args)
    {
        var c = _db.CreateCommand();
        c.CommandText = sql;
        c.Transaction = _tx;
        foreach (var (k, v) in args) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return c;
    }

    private void Exec(string sql, params (string, object?)[] args)
    {
        using var c = Cmd(sql, args);
        c.ExecuteNonQuery();
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        using var c = Cmd(sql, args);
        using var r = c.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static string Now() => DateTime.UtcNow.ToString("O");

    // ---------- settings ----------

    public string? GetSetting(string key) =>
        Query("SELECT value FROM settings WHERE key=$k", r => r.GetString(0), ("$k", key)).FirstOrDefault();

    public void SetSetting(string key, string value) =>
        Exec("INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$k", key), ("$v", value));

    // ---------- path rules (allowed extras / expected-to-change) ----------

    public const string RuleAllow = "allow";
    public const string RuleExpectChange = "expect-change";

    public Dictionary<string, string> GetPathRules() =>
        Query("SELECT path, rule FROM path_rules", r => (r.GetString(0), r.GetString(1)))
            .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.OrdinalIgnoreCase);

    public void SetPathRule(string path, string? rule)
    {
        if (rule is null) Exec("DELETE FROM path_rules WHERE path=$p", ("$p", path));
        else Exec("INSERT INTO path_rules(path,rule) VALUES($p,$r) ON CONFLICT(path) DO UPDATE SET rule=excluded.rule", ("$p", path), ("$r", rule));
    }

    // ---------- mods ----------

    public void UpsertMod(ModRecord m) => Exec("""
        INSERT INTO mods(id,name,version,source_url,archive_sha256,installed_at,linked_path)
        VALUES($id,$n,$v,$u,$a,$t,$l)
        ON CONFLICT(id) DO UPDATE SET name=excluded.name, version=excluded.version, source_url=excluded.source_url,
            archive_sha256=excluded.archive_sha256, linked_path=excluded.linked_path
        """, ("$id", m.Id), ("$n", m.Name), ("$v", m.Version), ("$u", m.SourceUrl), ("$a", m.ArchiveSha256),
        ("$t", m.InstalledAt.ToString("O")), ("$l", m.LinkedPath));

    private static ModRecord MapMod(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), Str(r, 2), Str(r, 3), Str(r, 4), DateTime.Parse(r.GetString(5)).ToUniversalTime(), Str(r, 6));

    public List<ModRecord> GetMods() =>
        Query("SELECT id,name,version,source_url,archive_sha256,installed_at,linked_path FROM mods ORDER BY installed_at", MapMod);

    public ModRecord? GetMod(string id) =>
        Query("SELECT id,name,version,source_url,archive_sha256,installed_at,linked_path FROM mods WHERE id=$id", MapMod, ("$id", id)).FirstOrDefault();

    public ModRecord? FindModByArchive(string sha) =>
        Query("SELECT id,name,version,source_url,archive_sha256,installed_at,linked_path FROM mods WHERE archive_sha256=$a", MapMod, ("$a", sha)).FirstOrDefault();

    public void DeleteMod(string id) => Exec("DELETE FROM mods WHERE id=$id", ("$id", id));

    public void ReplaceModFiles(string modId, IEnumerable<ModFileRecord> files)
    {
        using var tx = Begin();
        Exec("DELETE FROM mod_files WHERE mod_id=$m", ("$m", modId));
        foreach (var f in files)
            Exec("""
                INSERT INTO mod_files(mod_id,library_path,dest_path,sha256,size,kind,rule_id,confidence)
                VALUES($m,$l,$d,$s,$z,$k,$r,$c)
                """, ("$m", modId), ("$l", f.LibraryPath), ("$d", f.DestPath), ("$s", f.Sha256), ("$z", f.Size),
                ("$k", f.Kind), ("$r", f.RuleId), ("$c", f.Confidence));
        tx.Commit();
    }

    public List<ModFileRecord> GetModFiles(string modId) =>
        Query("SELECT mod_id,library_path,dest_path,sha256,size,kind,rule_id,confidence FROM mod_files WHERE mod_id=$m ORDER BY library_path",
            r => new ModFileRecord(r.GetString(0), r.GetString(1), Str(r, 2), r.GetString(3), r.GetInt64(4), r.GetString(5), r.GetString(6), r.GetString(7)),
            ("$m", modId));

    public void UpdateModFileHash(string modId, string libraryPath, string sha, long size) =>
        Exec("UPDATE mod_files SET sha256=$s, size=$z WHERE mod_id=$m AND library_path=$l",
            ("$s", sha), ("$z", size), ("$m", modId), ("$l", libraryPath));

    // ---------- profiles ----------

    /// <summary>The profile deploy, enable and order act on. Switching it and deploying moves only the diff.</summary>
    public string ActiveProfile => GetSetting("active_profile") ?? DefaultProfile;

    public List<(string Id, string Name)> GetProfiles() =>
        Query("SELECT id, name FROM profiles ORDER BY id='default' DESC, name", r => (r.GetString(0), r.GetString(1)));

    public (string Id, string Name)? FindProfile(string idOrName) =>
        GetProfiles().Where(p => p.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase) || p.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase))
            .Select(p => ((string, string)?)p).FirstOrDefault();

    public void CreateProfile(string id, string name, string? copyFrom = null)
    {
        if (FindProfile(id) is not null || FindProfile(name) is not null) throw new InvalidOperationException($"A profile called '{name}' already exists.");
        using var tx = Begin();
        Exec("INSERT INTO profiles(id,name) VALUES($i,$n)", ("$i", id), ("$n", name));
        if (copyFrom is not null)
            Exec("INSERT INTO profile_mods(profile_id,mod_id,enabled,priority) SELECT $i, mod_id, enabled, priority FROM profile_mods WHERE profile_id=$f",
                ("$i", id), ("$f", copyFrom));
        tx.Commit();
    }

    public void DeleteProfile(string id)
    {
        if (id == DefaultProfile) throw new InvalidOperationException("The default profile can't be deleted.");
        if (id == ActiveProfile) throw new InvalidOperationException("Switch to another profile before deleting this one.");
        Exec("DELETE FROM profiles WHERE id=$i", ("$i", id));
    }

    public void SetActiveProfile(string id)
    {
        if (!GetProfiles().Any(p => p.Id == id)) throw new InvalidOperationException($"No profile '{id}'.");
        SetSetting("active_profile", id);
    }

    public List<ProfileModRecord> GetProfileMods(string? profile = null) =>
        Query("SELECT mod_id, enabled, priority FROM profile_mods WHERE profile_id=$p ORDER BY priority",
            r => new ProfileModRecord(r.GetString(0), r.GetInt64(1) != 0, (int)r.GetInt64(2)), ("$p", profile ?? ActiveProfile));

    public void SetEnabled(string modId, bool enabled, string? profile = null)
    {
        profile ??= ActiveProfile;
        var next = Query("SELECT COALESCE(MAX(priority),0)+1 FROM profile_mods WHERE profile_id=$p", r => r.GetInt64(0), ("$p", profile))[0];
        Exec("""
            INSERT INTO profile_mods(profile_id,mod_id,enabled,priority) VALUES($p,$m,$e,$n)
            ON CONFLICT(profile_id,mod_id) DO UPDATE SET enabled=excluded.enabled
            """, ("$p", profile), ("$m", modId), ("$e", enabled ? 1 : 0), ("$n", next));
    }

    /// <summary>Moves a mod to a 1-based position in the profile's order (higher wins) and renumbers the rest.</summary>
    public void MoveMod(string modId, int position, string? profile = null)
    {
        profile ??= ActiveProfile;
        var order = GetProfileMods(profile).Select(p => p.ModId).ToList();
        if (!order.Remove(modId)) throw new InvalidOperationException("That mod isn't in this profile yet. Enable it first.");
        order.Insert(Math.Clamp(position - 1, 0, order.Count), modId);
        using var tx = Begin();
        for (var i = 0; i < order.Count; i++)
            Exec("UPDATE profile_mods SET priority=$n WHERE profile_id=$p AND mod_id=$m", ("$n", i + 1), ("$p", profile), ("$m", order[i]));
        tx.Commit();
    }

    // ---------- ledger ----------

    private static DeployedRecord MapDeployed(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5), r.GetString(6), DateTime.Parse(r.GetString(7)).ToUniversalTime());

    public List<DeployedRecord> GetDeployed() =>
        Query("SELECT dest_path,mod_id,library_path,sha256,size,mtime,method,deployed_at FROM deployed ORDER BY dest_path", MapDeployed);

    public void AddDeployed(DeployedRecord d) => Exec("""
        INSERT INTO deployed(dest_path,mod_id,library_path,sha256,size,mtime,method,deployed_at)
        VALUES($d,$m,$l,$s,$z,$t,$me,$at)
        ON CONFLICT(dest_path) DO UPDATE SET mod_id=excluded.mod_id, library_path=excluded.library_path, sha256=excluded.sha256,
            size=excluded.size, mtime=excluded.mtime, method=excluded.method, deployed_at=excluded.deployed_at
        """, ("$d", d.DestPath), ("$m", d.ModId), ("$l", d.LibraryPath), ("$s", d.Sha256), ("$z", d.Size), ("$t", d.MTime),
        ("$me", d.Method), ("$at", d.DeployedAt.ToString("O")));

    public void RemoveDeployed(string dest) => Exec("DELETE FROM deployed WHERE dest_path=$d", ("$d", dest));

    // ---------- backups ----------

    public List<BackupRecord> GetBackups() =>
        Query("SELECT dest_path,backup_path,sha256,replaced_by_mod FROM backups ORDER BY dest_path",
            r => new BackupRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));

    public void AddBackup(BackupRecord b) => Exec("""
        INSERT INTO backups(dest_path,backup_path,sha256,replaced_by_mod) VALUES($d,$b,$s,$m)
        ON CONFLICT(dest_path) DO UPDATE SET backup_path=excluded.backup_path, sha256=excluded.sha256, replaced_by_mod=excluded.replaced_by_mod
        """, ("$d", b.DestPath), ("$b", b.BackupPath), ("$s", b.Sha256), ("$m", b.ReplacedByMod));

    public void RemoveBackup(string dest) => Exec("DELETE FROM backups WHERE dest_path=$d", ("$d", dest));

    // ---------- baseline ----------

    public int BaselineCount() => (int)Query("SELECT COUNT(*) FROM baseline", r => r.GetInt64(0))[0];

    public List<BaselineRecord> GetBaseline() =>
        Query("SELECT path,size,mtime,sha256 FROM baseline", r => new BaselineRecord(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3)));

    public (string? GameVersion, DateTime? CapturedAt) GetBaselineInfo() =>
        Query("SELECT game_version, captured_at FROM baseline LIMIT 1", r => (Str(r, 0), (DateTime?)DateTime.Parse(r.GetString(1)).ToUniversalTime()))
            .DefaultIfEmpty((null, null)).First();

    public void ReplaceBaseline(IEnumerable<BaselineRecord> rows, string? gameVersion)
    {
        using var tx = Begin();
        Exec("DELETE FROM baseline");
        var now = Now();
        using var c = Cmd("INSERT INTO baseline(path,size,mtime,sha256,game_version,captured_at) VALUES($p,$z,$t,$s,$g,$c)");
        var p = c.Parameters.Add("$p", SqliteType.Text);
        var z = c.Parameters.Add("$z", SqliteType.Integer);
        var t = c.Parameters.Add("$t", SqliteType.Integer);
        var s = c.Parameters.Add("$s", SqliteType.Text);
        c.Parameters.AddWithValue("$g", (object?)gameVersion ?? DBNull.Value);
        c.Parameters.AddWithValue("$c", now);
        foreach (var row in rows)
        {
            p.Value = row.Path; z.Value = row.Size; t.Value = row.MTime; s.Value = row.Sha256;
            c.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ---------- journal ----------

    public void CreateOp(string opId, string kind, IReadOnlyList<string> payloads)
    {
        using var tx = Begin();
        Exec("INSERT INTO journal_ops(op_id,kind,status,created_at) VALUES($o,$k,'pending',$t)", ("$o", opId), ("$k", kind), ("$t", Now()));
        for (var i = 0; i < payloads.Count; i++)
            Exec("INSERT INTO journal(op_id,step,status,payload) VALUES($o,$s,'pending',$p)", ("$o", opId), ("$s", i), ("$p", payloads[i]));
        tx.Commit();
    }

    public List<string> GetPendingOps() =>
        Query("SELECT op_id FROM journal_ops WHERE status='pending' ORDER BY created_at", r => r.GetString(0));

    public List<JournalStepRecord> GetSteps(string opId) =>
        Query("SELECT op_id,step,status,payload FROM journal WHERE op_id=$o ORDER BY step",
            r => new JournalStepRecord(r.GetString(0), (int)r.GetInt64(1), r.GetString(2), r.GetString(3)), ("$o", opId));

    public void MarkStepDone(string opId, int step) =>
        Exec("UPDATE journal SET status='done' WHERE op_id=$o AND step=$s", ("$o", opId), ("$s", step));

    public void CompleteOp(string opId)
    {
        using var tx = Begin();
        // Completed ops carry no information worth keeping; drop their steps to keep the file small.
        Exec("DELETE FROM journal WHERE op_id=$o", ("$o", opId));
        Exec("UPDATE journal_ops SET status='done' WHERE op_id=$o", ("$o", opId));
        tx.Commit();
    }
}
