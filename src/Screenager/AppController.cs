using Screenager.Data;
using Screenager.Enforcement;
using Screenager.Reporting;
using Screenager.Tracking;
using Screenager.Ui;

namespace Screenager;

/// <summary>
/// Owns and wires every runtime component. Translates tracker state + session/power events
/// into UI updates and lock enforcement.
/// </summary>
public sealed class AppController : IDisposable
{
    private readonly Config _cfg;
    private readonly Database _db;
    private readonly LogicalClock _clock;
    private readonly MessageWindow _msg;
    private readonly ActivityTracker _tracker;
    private readonly FocusTracker _focus;
    private readonly TimerWindow _timerWindow;
    private readonly WarningWindow _warning;
    private readonly TimeUpWindow _timeUp;
    private readonly Enforcer _enforcer;
    private readonly OverrideManager _override;
    private readonly ReportScheduler _scheduler;

    private TrackerSnapshot? _last;
    private bool _locked;
    private bool _relockPending;

    public AppController(Config cfg, string dbPath)
    {
        _cfg = cfg;
        _db = new Database(dbPath);
        _clock = new LogicalClock(cfg.ResetHour, cfg.BedtimeStart, cfg.BedtimeEnd);

        _msg = new MessageWindow();
        _ = _msg.Handle; // accessing Handle forces creation so WTS/power notifications register

        _tracker = new ActivityTracker(_db, cfg, _clock);
        _focus = new FocusTracker(_db, _clock) { ShouldCount = () => _last is { Paused: false } };
        _timerWindow = new TimerWindow(cfg.WarnSeconds, LoadTimerLocation());
        _timerWindow.Moved += SaveTimerLocation;
        _warning = new WarningWindow();
        _timeUp = new TimeUpWindow();
        _enforcer = new Enforcer();
        _override = new OverrideManager(_msg.Handle, _tracker, cfg);
        _scheduler = new ReportScheduler(_db, cfg, _clock, new ReportService(_db, cfg, _clock));

        _msg.SessionLocked += OnSessionLocked;
        _msg.SessionUnlocked += OnSessionUnlocked;
        _msg.SystemSuspend += OnSuspend;
        _msg.SystemResume += OnResume;
        _msg.HotKeyPressed += id => _override.OnHotKey(id);
        _tracker.StateChanged += OnState;
        _timeUp.Elapsed += OnTimeUpElapsed;
    }

    public void Start()
    {
        _timerWindow.Show();
        _focus.Start();
        _scheduler.Start();
        _tracker.Start();
    }

    private Point? LoadTimerLocation()
    {
        if (int.TryParse(_db.GetMeta("timer_x"), out var x) && int.TryParse(_db.GetMeta("timer_y"), out var y))
            return new Point(x, y);
        return null;
    }

    private void SaveTimerLocation(Point p)
    {
        _db.SetMeta("timer_x", p.X.ToString());
        _db.SetMeta("timer_y", p.Y.ToString());
    }

    private void OnState(TrackerSnapshot s)
    {
        _last = s;
        _timerWindow.UpdateState(s);

        // While the parent override dialog is open, suspend all enforcement so the parent has
        // time to enter the PIN and grant/revoke without the screen locking out from under them.
        if (_override.DialogOpen)
        {
            _warning.HideWarning();
            return;
        }

        if (_locked || _relockPending)
            return;

        bool inWarnWindow = !s.Bedtime && s.RemainingSeconds > 0 && s.RemainingSeconds <= _cfg.WarnSeconds;
        if (inWarnWindow)
            _warning.ShowWarning(s.RemainingSeconds);
        else
            _warning.HideWarning();

        if (s.Expired)
        {
            _warning.HideWarning();
            _enforcer.Lock();
        }
    }

    private void OnSessionLocked()
    {
        _locked = true;
        _relockPending = false;
        _tracker.Locked = true;
        _warning.HideWarning();
    }

    private void OnSessionUnlocked()
    {
        _locked = false;
        _tracker.Locked = false;

        // If the day's time is already spent, briefly explain then re-lock.
        if (_last is { Expired: true })
            BeginRelock();
    }

    private void OnSuspend()
    {
        _tracker.Suspended = true;
        _tracker.Flush();
    }

    private void OnResume() => _tracker.Suspended = false;

    private void BeginRelock()
    {
        _relockPending = true;
        _warning.HideWarning();
        _timeUp.Flash();
    }

    private void OnTimeUpElapsed()
    {
        // Allow the normal OnState path to retry the lock if this one is rate-limited.
        _relockPending = false;
        _enforcer.Lock();
    }

    public void Dispose()
    {
        _tracker.Dispose();
        _focus.Dispose();
        _override.Dispose();
        _scheduler.Dispose();
        _warning.Dispose();
        _timeUp.Dispose();
        _timerWindow.Dispose();
        _msg.Dispose();
        _db.Dispose();
    }
}
