using System.Diagnostics;
using Screenager.Data;
using Screenager.Native;

namespace Screenager.Tracking;

public sealed record TrackerSnapshot(
    string Day,
    int RemainingSeconds,
    int BudgetSeconds,
    int ActiveSeconds,
    bool Paused,
    bool Bedtime,
    bool Expired);

/// <summary>
/// The core accounting contract. Ticks every second; credits the real (monotonic) elapsed
/// time to today's counter only while the user is active, unlocked, and awake. Persists to
/// SQLite periodically and rolls the logical day over at the configured reset hour.
/// </summary>
public sealed class ActivityTracker : IDisposable
{
    private const int TickMs = 1000;
    private const double MaxCreditPerTickSeconds = 2.0; // self-heals missed sleep/lock + clock jumps
    private const int SaveEveryTicks = 10;

    private readonly Database _db;
    private readonly Config _cfg;
    private readonly LogicalClock _clock;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Stopwatch _stopwatch = new();

    private string _day;
    private double _activeSeconds;
    private int _bonusSeconds;
    private bool _expired;
    private int _ticksSinceSave;

    public bool Locked { get; set; }
    public bool Suspended { get; set; }

    public event Action<TrackerSnapshot>? StateChanged;

    public ActivityTracker(Database db, Config cfg, LogicalClock clock)
    {
        _db = db;
        _cfg = cfg;
        _clock = clock;

        _day = _clock.Today();
        var usage = _db.GetUsage(_day);
        _activeSeconds = usage.ActiveSeconds;
        _bonusSeconds = usage.BonusSeconds;
        _expired = usage.Expired;

        _timer = new System.Windows.Forms.Timer { Interval = TickMs };
        _timer.Tick += (_, _) => OnTick();
    }

    public int BudgetSeconds => _cfg.DailyMinutes * 60 + _bonusSeconds;

    public void Start()
    {
        _stopwatch.Restart();
        _timer.Start();
        OnTick();
    }

    /// <summary>Grant extra minutes for today (parent override). Persists immediately.</summary>
    public void AddBonusMinutes(int minutes)
    {
        if (minutes == 0) return;
        _bonusSeconds += minutes * 60;
        _db.AddBonusSeconds(_day, minutes * 60);
        if (BudgetSeconds - (int)_activeSeconds > 0)
        {
            _expired = false;
            _db.SetExpired(_day, false);
        }
        OnTick();
    }

    public TrackerSnapshot Snapshot()
    {
        var now = DateTime.Now;
        bool bedtime = _clock.IsBedtime(now);
        bool paused = Locked || Suspended || NativeMethods.GetIdleSeconds() >= _cfg.IdleThresholdSeconds;
        int active = (int)Math.Round(_activeSeconds);
        int remaining = Math.Max(0, BudgetSeconds - active);
        bool expired = _expired || bedtime || remaining <= 0;
        return new TrackerSnapshot(_day, remaining, BudgetSeconds, active, paused, bedtime, expired);
    }

    private void OnTick()
    {
        double elapsed = _stopwatch.Elapsed.TotalSeconds;
        _stopwatch.Restart();
        double delta = Math.Clamp(elapsed, 0, MaxCreditPerTickSeconds);

        var now = DateTime.Now;
        string today = _clock.Today();
        if (today != _day)
            RollToDay(today);

        bool bedtime = _clock.IsBedtime(now);
        bool paused = Locked || Suspended || NativeMethods.GetIdleSeconds() >= _cfg.IdleThresholdSeconds;

        if (!paused && !bedtime)
            _activeSeconds += delta;

        int active = (int)Math.Round(_activeSeconds);
        int remaining = Math.Max(0, BudgetSeconds - active);
        bool expired = bedtime || remaining <= 0;

        if (expired && !_expired && !bedtime)
        {
            _expired = true;
            _db.SetExpired(_day, true);
        }

        if (++_ticksSinceSave >= SaveEveryTicks || (paused && _ticksSinceSave > 0))
        {
            _db.SetActiveSeconds(_day, active);
            _ticksSinceSave = 0;
        }

        StateChanged?.Invoke(new TrackerSnapshot(_day, remaining, BudgetSeconds, active, paused, bedtime, _expired || expired));
    }

    private void RollToDay(string newDay)
    {
        // Persist the day we are leaving, then load (fresh) state for the new day.
        _db.SetActiveSeconds(_day, (int)Math.Round(_activeSeconds));
        _day = newDay;
        var usage = _db.GetUsage(newDay);
        _activeSeconds = usage.ActiveSeconds;
        _bonusSeconds = usage.BonusSeconds;
        _expired = usage.Expired;
        _ticksSinceSave = 0;
    }

    /// <summary>Flush the in-memory counter to disk (e.g. on suspend/shutdown).</summary>
    public void Flush() => _db.SetActiveSeconds(_day, (int)Math.Round(_activeSeconds));

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        Flush();
    }
}
