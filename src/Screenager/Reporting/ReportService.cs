using Screenager.Data;
using Screenager.Tracking;

namespace Screenager.Reporting;

/// <summary>
/// Collects browser history, persists it, builds the report, and emails it. Shared by the
/// scheduled daily send and the manual <c>--report-now</c> path.
/// </summary>
public sealed class ReportService
{
    private readonly Database _db;
    private readonly Config _cfg;
    private readonly LogicalClock _clock;

    public ReportService(Database db, Config cfg, LogicalClock clock)
    {
        _db = db;
        _cfg = cfg;
        _clock = clock;
    }

    public async Task<(bool ok, string message)> SendForDayAsync(string day, DateTime sinceLocal)
    {
        if (_cfg.CollectBrowserHistory)
        {
            try
            {
                var visits = BrowserHistory.Collect(sinceLocal);
                _db.ReplaceBrowserVisits(day, visits);
            }
            catch (Exception ex)
            {
                // History collection is best-effort; still send the screen-time report.
                Console.WriteLine($"browser history collection failed: {ex.Message}");
            }
        }

        // Every crash/hang/restart not yet emailed — not just today's — so nothing is lost when a
        // send fails or the app was down at send time. Marked reported only after a successful send.
        var crashes = _db.GetUnreportedCrashes();
        int totalUnreported = _db.CountUnreportedCrashes();

        var (subject, html, text) = ReportBuilder.Build(_db, _cfg, day, Environment.MachineName, crashes, totalUnreported);
        // No ConfigureAwait(false): the DB connection is single-threaded, so resume on the caller's (UI) thread.
        var result = await Mailer.SendAsync(_cfg, subject, html, text);
        if (result.ok && crashes.Count > 0)
            _db.MarkCrashesReported(crashes.Max(c => c.Id));
        return result;
    }

    public Task<(bool ok, string message)> SendTodayAsync()
    {
        var now = DateTime.Now;
        return SendForDayAsync(_clock.Today(), _clock.DayStart(now));
    }
}
