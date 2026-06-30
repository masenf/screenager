using Microsoft.Data.Sqlite;
using Screenager.Data;

namespace Screenager.Reporting;

/// <summary>
/// Reads Chrome/Edge/Firefox history. Because the live DB is locked while the browser runs,
/// each file (plus its -wal/-shm sidecars) is copied to a temp location and opened read-only.
/// </summary>
public static class BrowserHistory
{
    private static readonly DateTime ChromeEpoch = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static List<BrowserVisit> Collect(DateTime sinceLocal)
    {
        var results = new List<BrowserVisit>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // Chromium-based browsers (Chrome, Edge): one "History" DB per profile.
        CollectChromium(results, "Chrome", Path.Combine(local, "Google", "Chrome", "User Data"), sinceLocal);
        CollectChromium(results, "Edge", Path.Combine(local, "Microsoft", "Edge", "User Data"), sinceLocal);

        // Firefox: places.sqlite per profile.
        CollectFirefox(results, Path.Combine(roaming, "Mozilla", "Firefox", "Profiles"), sinceLocal);

        return results;
    }

    private static void CollectChromium(List<BrowserVisit> sink, string browser, string userDataDir, DateTime sinceLocal)
    {
        if (!Directory.Exists(userDataDir))
            return;

        foreach (var profileDir in EnumerateProfileDirs(userDataDir))
        {
            var historyPath = Path.Combine(profileDir, "History");
            if (!File.Exists(historyPath))
                continue;

            ReadSnapshot(historyPath, conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT url, title, visit_count, last_visit_time
                    FROM urls
                    WHERE last_visit_time > 0
                    ORDER BY last_visit_time DESC
                    LIMIT 2000;
                """;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var last = ChromeEpoch.AddTicks(r.GetInt64(3) * 10).ToLocalTime();
                    if (last < sinceLocal)
                        break; // ordered DESC, so the rest are older
                    sink.Add(new BrowserVisit(browser, r.GetString(0), SafeTitle(r, 1), r.GetInt32(2), last));
                }
            });
        }
    }

    private static void CollectFirefox(List<BrowserVisit> sink, string profilesDir, DateTime sinceLocal)
    {
        if (!Directory.Exists(profilesDir))
            return;

        foreach (var profileDir in Directory.GetDirectories(profilesDir))
        {
            var placesPath = Path.Combine(profileDir, "places.sqlite");
            if (!File.Exists(placesPath))
                continue;

            ReadSnapshot(placesPath, conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT url, title, visit_count, last_visit_date
                    FROM moz_places
                    WHERE last_visit_date IS NOT NULL
                    ORDER BY last_visit_date DESC
                    LIMIT 2000;
                """;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var last = UnixEpoch.AddTicks(r.GetInt64(3) * 10).ToLocalTime();
                    if (last < sinceLocal)
                        break;
                    sink.Add(new BrowserVisit("Firefox", r.GetString(0), SafeTitle(r, 1), r.GetInt32(2), last));
                }
            });
        }
    }

    private static IEnumerable<string> EnumerateProfileDirs(string userDataDir)
    {
        foreach (var dir in Directory.GetDirectories(userDataDir))
        {
            var name = Path.GetFileName(dir);
            if (name.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
                yield return dir;
        }
    }

    /// <summary>Copies the DB (+ wal/shm) to temp, opens it read-only, runs <paramref name="read"/>, cleans up.</summary>
    private static void ReadSnapshot(string dbPath, Action<SqliteConnection> read)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "screenager_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            var tempDb = Path.Combine(tempDir, Path.GetFileName(dbPath));
            CopyIfExists(dbPath, tempDb);
            CopyIfExists(dbPath + "-wal", tempDb + "-wal");
            CopyIfExists(dbPath + "-shm", tempDb + "-shm");

            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = tempDb,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                Cache = SqliteCacheMode.Private,
            }.ToString();

            using (var conn = new SqliteConnection(cs))
            {
                conn.Open();
                read(conn);
            }
        }
        catch
        {
            // A single unreadable/locked profile must not abort the whole report.
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private static void CopyIfExists(string src, string dst)
    {
        if (File.Exists(src))
            File.Copy(src, dst, overwrite: true);
    }

    private static string SafeTitle(SqliteDataReader r, int col)
        => r.IsDBNull(col) ? "" : r.GetString(col);
}
