using Screenager.Tracking;

namespace Screenager.Tests;

/// <summary>
/// The core accounting contract from the README: time counts only while the user is active,
/// unlocked and outside bedtime; per-tick credit is capped; the limit (plus granted bonus)
/// expires the day; state persists across restarts. Ticks are driven manually with real
/// elapsed time, so these tests take a few seconds.
/// </summary>
public class ActivityTrackerTests : IDisposable
{
    private readonly TempEnv _env = new();
    public void Dispose() => _env.Dispose();

    private (ActivityTracker tracker, Func<TrackerSnapshot> last, Screenager.Data.Database db, LogicalClock clock) Make(
        string limits = "daily_minutes = 60", double idleSeconds = 0)
    {
        var cfg = _env.Config($"[limits]\nidle_threshold_seconds = 60\naudio_counts_as_activity = false\n{limits}\n");
        var db = _env.OpenDb();
        var clock = new LogicalClock(cfg.ResetHour, cfg.BedtimeStart, cfg.BedtimeEnd);
        var tracker = new ActivityTracker(db, cfg, clock) { IdleSeconds = () => idleSeconds };
        TrackerSnapshot? snap = null;
        tracker.StateChanged += s => snap = s;
        return (tracker, () => snap!, db, clock);
    }

    [Fact]
    public void CreditsActiveTimeAndPersistsPerMinuteBreakdown()
    {
        var (tracker, last, db, clock) = Make();
        using (db)
        {
            tracker.Start();
            Thread.Sleep(1300);
            tracker.TickNow();
            Assert.False(last().Paused);
            Assert.InRange(last().ActiveSeconds, 1, 2);

            tracker.Dispose(); // flushes
            int saved = db.GetUsage(clock.Today()).ActiveSeconds;
            Assert.Equal(last().ActiveSeconds, saved);
            Assert.Equal(saved, db.GetActivityMinutes(clock.Today()).Values.Sum());
        }
    }

    [Fact]
    public void IdleUserIsNotCharged()
    {
        var (tracker, last, db, _) = Make(idleSeconds: 600);
        using (db)
        using (tracker)
        {
            tracker.Start();
            Thread.Sleep(1200);
            tracker.TickNow();
            Assert.True(last().Paused);
            Assert.Equal(0, last().ActiveSeconds);
        }
    }

    [Fact]
    public void LockedSessionIsNotCharged()
    {
        var (tracker, last, db, _) = Make();
        using (db)
        using (tracker)
        {
            tracker.Locked = true;
            tracker.Start();
            Thread.Sleep(1200);
            tracker.TickNow();
            Assert.True(last().Paused);
            Assert.Equal(0, last().ActiveSeconds);
        }
    }

    [Fact]
    public void CreditPerTickIsCappedSoMissedEventsCannotOverCount()
    {
        var (tracker, last, db, _) = Make();
        using (db)
        using (tracker)
        {
            tracker.Start();
            Thread.Sleep(3500); // e.g. a missed resume event: far more than one tick elapsed
            tracker.TickNow();
            Assert.Equal(2, last().ActiveSeconds);
        }
    }

    [Fact]
    public void ExhaustedBudgetExpiresTheDayAndPersists()
    {
        var (tracker, last, db, clock) = Make("daily_minutes = 0");
        using (db)
        using (tracker)
        {
            tracker.Start();
            Assert.True(last().Expired);
            Assert.Equal(0, last().RemainingSeconds);
            Assert.True(db.GetUsage(clock.Today()).Expired);
        }
    }

    [Fact]
    public void GrantedBonusExtendsBudgetAndRevokeRemovesIt()
    {
        var (tracker, last, db, clock) = Make("daily_minutes = 0");
        using (db)
        using (tracker)
        {
            tracker.Start();
            tracker.AddBonusMinutes(10);
            Assert.False(last().Expired);
            Assert.Equal(600, last().BudgetSeconds);
            Assert.True(tracker.OverrideActive);
            Assert.Equal(600, db.GetUsage(clock.Today()).BonusSeconds);
            Assert.False(db.GetUsage(clock.Today()).Expired);

            tracker.RevokeBonus();
            Assert.True(last().Expired);
            Assert.False(tracker.OverrideActive);
            Assert.Equal(0, db.GetUsage(clock.Today()).BonusSeconds);
        }
    }

    [Fact]
    public void BedtimeExpiresAndStopsCountingEvenWithBudgetLeft()
    {
        var now = DateTime.Now.TimeOfDay;
        string start = TimeOnly.FromTimeSpan(now).AddHours(-1).ToString("HH:mm");
        string end = TimeOnly.FromTimeSpan(now).AddHours(1).ToString("HH:mm");
        var (tracker, last, db, _) = Make($"daily_minutes = 600\nbedtime_start = {start}\nbedtime_end = {end}");
        using (db)
        using (tracker)
        {
            tracker.Start();
            Thread.Sleep(1200);
            tracker.TickNow();
            Assert.True(last().Bedtime);
            Assert.True(last().Expired);
            Assert.Equal(0, last().ActiveSeconds);
        }
    }

    [Fact]
    public void BonusSuspendsBedtime()
    {
        var now = DateTime.Now.TimeOfDay;
        string start = TimeOnly.FromTimeSpan(now).AddHours(-1).ToString("HH:mm");
        string end = TimeOnly.FromTimeSpan(now).AddHours(1).ToString("HH:mm");
        var (tracker, last, db, _) = Make($"daily_minutes = 600\nbedtime_start = {start}\nbedtime_end = {end}");
        using (db)
        using (tracker)
        {
            tracker.Start();
            tracker.AddBonusMinutes(15);
            Assert.False(last().Bedtime);
            Assert.False(last().Expired);
        }
    }

    [Fact]
    public void ResumesTodaysUsageAfterRestart()
    {
        var cfgText = "[limits]\ndaily_minutes = 60\naudio_counts_as_activity = false\n";
        var cfg = _env.Config(cfgText);
        var clock = new LogicalClock(cfg.ResetHour, cfg.BedtimeStart, cfg.BedtimeEnd);
        using var db = _env.OpenDb();
        db.SetActiveSeconds(clock.Today(), 1234);
        db.AddBonusSeconds(clock.Today(), 300);

        using var tracker = new ActivityTracker(db, cfg, clock) { IdleSeconds = () => 600 };
        TrackerSnapshot? snap = null;
        tracker.StateChanged += s => snap = s;
        tracker.Start();
        Assert.Equal(1234, snap!.ActiveSeconds);
        Assert.Equal(3600 + 300, snap.BudgetSeconds);
        Assert.Equal(3600 + 300 - 1234, snap.RemainingSeconds);
    }
}
