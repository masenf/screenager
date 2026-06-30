using Screenager.Data;
using Screenager.Tracking;

namespace Screenager.Reporting;

/// <summary>
/// Once per day, at or after the configured hour, sends the report. If the send fails (e.g. the
/// machine is offline) it is not marked done, so it retries on the next tick / next launch.
/// </summary>
public sealed class ReportScheduler : IDisposable
{
    private const string LastSentKey = "last_report_day";

    private readonly Database _db;
    private readonly Config _cfg;
    private readonly LogicalClock _clock;
    private readonly ReportService _service;
    private readonly System.Windows.Forms.Timer _timer;
    private bool _busy;

    public ReportScheduler(Database db, Config cfg, LogicalClock clock, ReportService service)
    {
        _db = db;
        _cfg = cfg;
        _clock = clock;
        _service = service;
        _timer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _timer.Tick += async (_, _) => await CheckAsync();
    }

    public void Start()
    {
        if (_cfg.ReportEnabled)
            _timer.Start();
    }

    private async Task CheckAsync()
    {
        if (_busy)
            return;

        var now = DateTime.Now;
        if (now.Hour < _cfg.ReportSendHour)
            return;

        var day = _clock.Today();
        if (_db.GetMeta(LastSentKey) == day)
            return;

        _busy = true;
        try
        {
            var (ok, message) = await _service.SendForDayAsync(day, _clock.DayStart(now));
            if (ok)
                _db.SetMeta(LastSentKey, day);
            else
                Console.WriteLine($"report send failed (will retry): {message}");
        }
        catch (Exception ex)
        {
            // async void event handler: never let an exception escape and crash the app.
            Console.WriteLine($"report tick error (will retry): {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
