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

        var (subject, html, text) = ReportBuilder.Build(_db, _cfg, day, Environment.MachineName);
        return await Mailer.SendAsync(_cfg, subject, html, text).ConfigureAwait(false);
    }

    public Task<(bool ok, string message)> SendTodayAsync()
    {
        var now = DateTime.Now;
        return SendForDayAsync(_clock.Today(), _clock.DayStart(now));
    }
}
