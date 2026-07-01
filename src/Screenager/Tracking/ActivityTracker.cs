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
    private const float AudioPeakThreshold = 0.001f; // above this, sound is actually playing
    private const int AudioPollTicks = 5;   // sample the audio meter every ~5s to save CPU
    private const int AudioGraceTicks = 15; // treat audio as active this long after last heard

    private readonly Database _db;
    private readonly Config _cfg;
    private readonly LogicalClock _clock;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Stopwatch _stopwatch = new();
    private readonly AudioActivity? _audio;

    private string _day;
    private double _activeSeconds;
    private int _bonusSeconds;
    private bool _expired;
    private int _ticksSinceSave;
    private int _lastSavedActive;
    private int _audioActiveTicks;
    private int _sinceAudioPoll;

    // Wall-clock instant until which an active parent override suppresses bedtime locking.
    private const string OverrideUntilKey = "override_until";
    private DateTime _overrideUntil = DateTime.MinValue;

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
        _lastSavedActive = usage.ActiveSeconds;
        _bonusSeconds = usage.BonusSeconds;
        _expired = usage.Expired;
        _overrideUntil = LoadOverrideUntil();
        _audio = cfg.AudioCountsAsActivity ? new AudioActivity() : null;

        _timer = new System.Windows.Forms.Timer { Interval = TickMs };
        _timer.Tick += (_, _) => OnTick();
    }

    public int BudgetSeconds => _cfg.DailyMinutes * 60 + _bonusSeconds;

    /// <summary>Total extra time granted today (seconds), shown in the override dialog.</summary>
    public int GrantedBonusSeconds => _bonusSeconds;

    /// <summary>True while a parent override is in effect (bedtime suppressed).</summary>
    public bool OverrideActive => DateTime.Now < _overrideUntil;

    private DateTime LoadOverrideUntil()
    {
        var raw = _db.GetMeta(OverrideUntilKey);
        return DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt : DateTime.MinValue;
    }

    private bool IsBedtimeNow(DateTime now) => _clock.IsBedtime(now) && now >= _overrideUntil;

    public void Start()
    {
        _stopwatch.Restart();
        _timer.Start();
        OnTick();
    }

    /// <summary>
    /// Grant extra minutes for today (parent override). Extends the active-time budget and the
    /// bedtime-suppression window, and clears the expired flag. Persists immediately.
    /// </summary>
    public void AddBonusMinutes(int minutes)
    {
        if (minutes <= 0) return;
        _bonusSeconds += minutes * 60;
        _db.AddBonusSeconds(_day, minutes * 60);

        // Suppress bedtime for the granted span (wall-clock), stacking on any existing override.
        var now = DateTime.Now;
        var basis = _overrideUntil > now ? _overrideUntil : now;
        _overrideUntil = basis.AddMinutes(minutes);
        _db.SetMeta(OverrideUntilKey, _overrideUntil.ToString("o"));

        if (BudgetSeconds - (int)_activeSeconds > 0)
        {
            _expired = false;
            _db.SetExpired(_day, false);
        }
        OnTick();
    }

    /// <summary>Revoke all extra time granted today and end any bedtime-suppression window.</summary>
    public void RevokeBonus()
    {
        _bonusSeconds = 0;
        _db.SetBonusSeconds(_day, 0);
        _overrideUntil = DateTime.MinValue;
        _db.SetMeta(OverrideUntilKey, _overrideUntil.ToString("o"));
        OnTick();
    }

    /// <summary>
    /// Paused when locked, asleep, or idle. Idle is overridden by active audio output (a playing
    /// video keeps counting) when that option is enabled. The meter is sampled only every
    /// <see cref="AudioPollTicks"/> to save CPU; the longer grace window bridges those gaps and
    /// smooths over brief silences (e.g. between spoken words).
    /// </summary>
    private bool IsPaused()
    {
        if (Locked || Suspended)
            return true;

        if (_audio is not null)
        {
            if (--_sinceAudioPoll <= 0)
            {
                _sinceAudioPoll = AudioPollTicks;
                if (_audio.IsPlaying(AudioPeakThreshold))
                    _audioActiveTicks = AudioGraceTicks;
            }
            if (_audioActiveTicks > 0)
                _audioActiveTicks--;
        }

        bool idle = NativeMethods.GetIdleSeconds() >= _cfg.IdleThresholdSeconds;
        if (!idle)
            return false; // real input — active regardless of audio
        return _audioActiveTicks <= 0; // idle: active only while audio is (recently) playing
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

        bool bedtime = IsBedtimeNow(now);
        bool paused = IsPaused();

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

        // Persist at most every SaveEveryTicks, and only when the value actually changed. While
        // paused/idle the counter doesn't move, so this writes nothing (no idle DB churn).
        if (++_ticksSinceSave >= SaveEveryTicks)
        {
            if (active != _lastSavedActive)
            {
                _db.SetActiveSeconds(_day, active);
                _lastSavedActive = active;
            }
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
        _lastSavedActive = usage.ActiveSeconds;
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
        _audio?.Dispose();
        Flush();
    }
}
