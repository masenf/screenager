using Microsoft.Data.Sqlite;

namespace Screenager.Data;

public sealed record DailyUsage(string Day, int ActiveSeconds, int BonusSeconds, bool Expired);

public sealed record FocusRow(string Process, string Title, int Seconds);

public sealed record BrowserVisit(string Browser, string Url, string Title, int VisitCount, DateTime LastVisit);

/// <summary>
/// One stability incident: a crash, an unexpected exit, a hang, or a period where nothing was
/// running. Written by the worker (managed exception detail) and/or the supervisor (exit code,
/// console output, event log, resource usage), and emailed in the next daily report.
/// </summary>
public sealed record CrashRecord
{
    public long Id { get; init; }
    public DateTime Time { get; init; }
    /// <summary>exception | exit | hang | supervisor-exception | not-running</summary>
    public string Kind { get; init; } = "";
    public int? Pid { get; init; }
    public string? AppVersion { get; init; }
    public long? UptimeSeconds { get; init; }
    public int? ExitCode { get; init; }
    public string Summary { get; init; } = "";
    public string Details { get; init; } = "";
    public string? DumpPath { get; init; }
}

/// <summary>
/// SQLite persistence. The connection is kept open for the app's lifetime (WAL mode).
/// All accounting is keyed by the "logical day" string (see <see cref="Tracking.LogicalClock"/>).
/// </summary>
public sealed class Database : IDisposable
{
    private readonly SqliteConnection _conn;

    public Database(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString());
        _conn.Open();

        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Migrate();
    }

    private void Migrate()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS daily_usage(
                day TEXT PRIMARY KEY,
                active_seconds INTEGER NOT NULL DEFAULT 0,
                bonus_seconds INTEGER NOT NULL DEFAULT 0,
                expired INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS focus_daily(
                day TEXT NOT NULL,
                process TEXT NOT NULL,
                title TEXT NOT NULL,
                seconds INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(day, process, title)
            );
            CREATE TABLE IF NOT EXISTS browser_visits(
                day TEXT NOT NULL,
                browser TEXT NOT NULL,
                url TEXT NOT NULL,
                title TEXT NOT NULL,
                visit_count INTEGER NOT NULL DEFAULT 0,
                last_visit TEXT NOT NULL,
                PRIMARY KEY(day, browser, url)
            );
            CREATE TABLE IF NOT EXISTS meta(
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS crashes(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL,
                kind TEXT NOT NULL,
                pid INTEGER,
                app_version TEXT,
                uptime_seconds INTEGER,
                exit_code INTEGER,
                summary TEXT NOT NULL,
                details TEXT NOT NULL DEFAULT '',
                dump_path TEXT,
                reported INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS crashes_unreported ON crashes(reported, id);
        """);
    }

    // ---------------- daily_usage ----------------
    public DailyUsage GetUsage(string day)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT active_seconds, bonus_seconds, expired FROM daily_usage WHERE day=$d;";
        cmd.Parameters.AddWithValue("$d", day);
        using var r = cmd.ExecuteReader();
        if (r.Read())
            return new DailyUsage(day, r.GetInt32(0), r.GetInt32(1), r.GetInt32(2) != 0);
        return new DailyUsage(day, 0, 0, false);
    }

    public void SetActiveSeconds(string day, int seconds)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO daily_usage(day, active_seconds) VALUES($d, $s)
            ON CONFLICT(day) DO UPDATE SET active_seconds=$s;
        """;
        cmd.Parameters.AddWithValue("$d", day);
        cmd.Parameters.AddWithValue("$s", seconds);
        cmd.ExecuteNonQuery();
    }

    public void AddBonusSeconds(string day, int seconds)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO daily_usage(day, bonus_seconds) VALUES($d, $s)
            ON CONFLICT(day) DO UPDATE SET bonus_seconds=bonus_seconds+$s;
        """;
        cmd.Parameters.AddWithValue("$d", day);
        cmd.Parameters.AddWithValue("$s", seconds);
        cmd.ExecuteNonQuery();
    }

    public void SetBonusSeconds(string day, int seconds)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO daily_usage(day, bonus_seconds) VALUES($d, $s)
            ON CONFLICT(day) DO UPDATE SET bonus_seconds=$s;
        """;
        cmd.Parameters.AddWithValue("$d", day);
        cmd.Parameters.AddWithValue("$s", seconds);
        cmd.ExecuteNonQuery();
    }

    public void SetExpired(string day, bool expired)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO daily_usage(day, expired) VALUES($d, $e)
            ON CONFLICT(day) DO UPDATE SET expired=$e;
        """;
        cmd.Parameters.AddWithValue("$d", day);
        cmd.Parameters.AddWithValue("$e", expired ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    // ---------------- focus_daily ----------------
    public void AddFocusSeconds(string day, string process, string title, int seconds)
    {
        if (seconds <= 0) return;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO focus_daily(day, process, title, seconds) VALUES($d, $p, $t, $s)
            ON CONFLICT(day, process, title) DO UPDATE SET seconds=seconds+$s;
        """;
        cmd.Parameters.AddWithValue("$d", day);
        cmd.Parameters.AddWithValue("$p", process);
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$s", seconds);
        cmd.ExecuteNonQuery();
    }

    public List<FocusRow> GetFocus(string day, int limit = 50)
    {
        var rows = new List<FocusRow>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT process, title, seconds FROM focus_daily WHERE day=$d ORDER BY seconds DESC LIMIT $l;";
        cmd.Parameters.AddWithValue("$d", day);
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new FocusRow(r.GetString(0), r.GetString(1), r.GetInt32(2)));
        return rows;
    }

    // ---------------- browser_visits ----------------
    public void ReplaceBrowserVisits(string day, IEnumerable<BrowserVisit> visits)
    {
        using var tx = _conn.BeginTransaction();
        using (var del = _conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM browser_visits WHERE day=$d;";
            del.Parameters.AddWithValue("$d", day);
            del.ExecuteNonQuery();
        }
        using (var ins = _conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO browser_visits(day, browser, url, title, visit_count, last_visit)
                VALUES($d, $b, $u, $t, $c, $lv)
                ON CONFLICT(day, browser, url) DO UPDATE SET
                    visit_count=excluded.visit_count, title=excluded.title, last_visit=excluded.last_visit;
            """;
            var pD = ins.Parameters.Add("$d", SqliteType.Text);
            var pB = ins.Parameters.Add("$b", SqliteType.Text);
            var pU = ins.Parameters.Add("$u", SqliteType.Text);
            var pT = ins.Parameters.Add("$t", SqliteType.Text);
            var pC = ins.Parameters.Add("$c", SqliteType.Integer);
            var pLv = ins.Parameters.Add("$lv", SqliteType.Text);
            foreach (var v in visits)
            {
                pD.Value = day;
                pB.Value = v.Browser;
                pU.Value = v.Url;
                pT.Value = v.Title;
                pC.Value = v.VisitCount;
                pLv.Value = v.LastVisit.ToString("o");
                ins.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    public List<BrowserVisit> GetBrowserVisits(string day, int limit = 200)
    {
        var rows = new List<BrowserVisit>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT browser, url, title, visit_count, last_visit FROM browser_visits WHERE day=$d ORDER BY last_visit DESC LIMIT $l;";
        cmd.Parameters.AddWithValue("$d", day);
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            DateTime.TryParse(r.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind, out var lv);
            rows.Add(new BrowserVisit(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), lv));
        }
        return rows;
    }

    // ---------------- meta ----------------
    public string? GetMeta(string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key=$k;";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string value)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO meta(key, value) VALUES($k, $v)
            ON CONFLICT(key) DO UPDATE SET value=$v;
        """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public void DeleteMeta(string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM meta WHERE key=$k;";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.ExecuteNonQuery();
    }

    // ---------------- crashes ----------------
    public long InsertCrash(CrashRecord c)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO crashes(ts, kind, pid, app_version, uptime_seconds, exit_code, summary, details, dump_path)
            VALUES($ts, $k, $pid, $v, $up, $ec, $s, $d, $dp)
            RETURNING id;
        """;
        cmd.Parameters.AddWithValue("$ts", c.Time.ToString("o"));
        cmd.Parameters.AddWithValue("$k", c.Kind);
        cmd.Parameters.AddWithValue("$pid", (object?)c.Pid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$v", (object?)c.AppVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$up", (object?)c.UptimeSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ec", (object?)c.ExitCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$s", c.Summary);
        cmd.Parameters.AddWithValue("$d", c.Details);
        cmd.Parameters.AddWithValue("$dp", (object?)c.DumpPath ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Attach exit information to the exception record the worker wrote just before it died
    /// (same pid, written after <paramref name="sinceLocal"/>, no exit code yet). Returns false
    /// if there is no such record, in which case the caller inserts a fresh one.
    /// </summary>
    public bool CompleteExceptionCrash(int pid, DateTime sinceLocal, int exitCode, string extraDetails, string? dumpPath)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE crashes SET
                exit_code = $ec,
                details = details || $extra,
                dump_path = COALESCE($dp, dump_path)
            WHERE id = (
                SELECT id FROM crashes
                WHERE pid=$pid AND kind='exception' AND exit_code IS NULL AND ts >= $since
                ORDER BY id DESC LIMIT 1
            );
        """;
        cmd.Parameters.AddWithValue("$ec", exitCode);
        cmd.Parameters.AddWithValue("$extra", "\n\n" + extraDetails);
        cmd.Parameters.AddWithValue("$dp", (object?)dumpPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pid", pid);
        cmd.Parameters.AddWithValue("$since", sinceLocal.ToString("o"));
        return cmd.ExecuteNonQuery() > 0;
    }

    public List<CrashRecord> GetUnreportedCrashes(int limit = 200)
    {
        var rows = new List<CrashRecord>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, ts, kind, pid, app_version, uptime_seconds, exit_code, summary, details, dump_path
            FROM crashes WHERE reported=0 ORDER BY id LIMIT $l;
        """;
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            DateTime.TryParse(r.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind, out var ts);
            rows.Add(new CrashRecord
            {
                Id = r.GetInt64(0),
                Time = ts,
                Kind = r.GetString(2),
                Pid = r.IsDBNull(3) ? null : r.GetInt32(3),
                AppVersion = r.IsDBNull(4) ? null : r.GetString(4),
                UptimeSeconds = r.IsDBNull(5) ? null : r.GetInt64(5),
                ExitCode = r.IsDBNull(6) ? null : r.GetInt32(6),
                Summary = r.GetString(7),
                Details = r.GetString(8),
                DumpPath = r.IsDBNull(9) ? null : r.GetString(9),
            });
        }
        return rows;
    }

    public int CountUnreportedCrashes()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM crashes WHERE reported=0;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Mark every unreported crash up to and including <paramref name="maxId"/> as emailed.</summary>
    public void MarkCrashesReported(long maxId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE crashes SET reported=1 WHERE reported=0 AND id <= $id;";
        cmd.Parameters.AddWithValue("$id", maxId);
        cmd.ExecuteNonQuery();
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
