using Screenager.Data;

namespace Screenager.Tests;

public class DatabaseTests : IDisposable
{
    private readonly TempEnv _env = new();
    public void Dispose() => _env.Dispose();

    [Fact]
    public void MigrationIsIdempotentAndDataSurvivesReopen()
    {
        using (var db = _env.OpenDb())
            db.SetActiveSeconds("2026-11-10", 100);
        using (var db = _env.OpenDb())
            Assert.Equal(100, db.GetUsage("2026-11-10").ActiveSeconds);
    }

    [Fact]
    public void UsageDefaultsToZeroAndUpserts()
    {
        using var db = _env.OpenDb();
        Assert.Equal(new DailyUsage("d", 0, 0, false), db.GetUsage("d"));

        db.SetActiveSeconds("d", 60);
        db.SetActiveSeconds("d", 90);
        db.AddBonusSeconds("d", 300);
        db.AddBonusSeconds("d", 300);
        db.SetExpired("d", true);
        Assert.Equal(new DailyUsage("d", 90, 600, true), db.GetUsage("d"));

        db.SetBonusSeconds("d", 0);
        Assert.Equal(0, db.GetUsage("d").BonusSeconds);
        Assert.Equal(90, db.GetUsage("d").ActiveSeconds); // other columns untouched
    }

    [Fact]
    public void FocusSecondsAccumulatePerWindowAndSortDescending()
    {
        using var db = _env.OpenDb();
        db.AddFocusSeconds("d", "chrome", "A", 10);
        db.AddFocusSeconds("d", "chrome", "A", 15);
        db.AddFocusSeconds("d", "javaw", "Minecraft", 40);
        db.AddFocusSeconds("d", "x", "ignored", 0);
        db.AddFocusSeconds("other-day", "chrome", "A", 999);

        var rows = db.GetFocus("d");
        Assert.Equal(new[] { new FocusRow("javaw", "Minecraft", 40), new FocusRow("chrome", "A", 25) }, rows);
    }

    [Fact]
    public void BrowserVisitsAreReplacedPerDay()
    {
        using var db = _env.OpenDb();
        var t = new DateTime(2026, 11, 10, 18, 0, 0);
        db.ReplaceBrowserVisits("d", [new BrowserVisit("Chrome", "https://a", "A", 1, t)]);
        db.ReplaceBrowserVisits("d", [new BrowserVisit("Chrome", "https://b", "B", 2, t.AddMinutes(5))]);

        var visits = db.GetBrowserVisits("d");
        var v = Assert.Single(visits);
        Assert.Equal("https://b", v.Url);
        Assert.Equal(t.AddMinutes(5), v.LastVisit);
    }

    [Fact]
    public void MetaSetGetDelete()
    {
        using var db = _env.OpenDb();
        Assert.Null(db.GetMeta("k"));
        db.SetMeta("k", "1");
        db.SetMeta("k", "2");
        Assert.Equal("2", db.GetMeta("k"));
        db.DeleteMeta("k");
        Assert.Null(db.GetMeta("k"));
    }

    [Fact]
    public void ActivityMinutesAccumulateAndCapAtSixtySeconds()
    {
        using var db = _env.OpenDb();
        db.AddActivityMinutes("d", [(10, 30), (11, 20)]);
        db.AddActivityMinutes("d", [(10, 45), (12, 0)]);
        var map = db.GetActivityMinutes("d");
        Assert.Equal(60, map[10]);          // 30 + 45 capped
        Assert.Equal(20, map[11]);
        Assert.False(map.ContainsKey(12));  // zero credit is not stored
    }

    [Fact]
    public void CrashesStayUnreportedUntilMarked()
    {
        using var db = _env.OpenDb();
        long a = db.InsertCrash(new CrashRecord { Time = DateTime.Now, Kind = "exit", Summary = "a" });
        long b = db.InsertCrash(new CrashRecord { Time = DateTime.Now, Kind = "hang", Summary = "b" });
        Assert.Equal(2, db.CountUnreportedCrashes());

        db.MarkCrashesReported(a);
        var left = Assert.Single(db.GetUnreportedCrashes());
        Assert.Equal(b, left.Id);
        Assert.Equal("hang", left.Kind);
    }

    [Fact]
    public void WatchdogExitDetailsMergeIntoTheWorkersExceptionRecord()
    {
        using var db = _env.OpenDb();
        var start = DateTime.Now.AddMinutes(-5);
        db.InsertCrash(new CrashRecord { Time = DateTime.Now, Kind = "exception", Pid = 42, Summary = "boom", Details = "stack" });

        Assert.False(db.CompleteExceptionCrash(pid: 99, start, -532462766, "other pid", null));
        Assert.False(db.CompleteExceptionCrash(pid: 42, DateTime.Now.AddMinutes(1), -532462766, "too late", null));
        Assert.True(db.CompleteExceptionCrash(pid: 42, start, -532462766, "exit info", @"C:\d.dmp"));

        var c = Assert.Single(db.GetUnreportedCrashes());
        Assert.Equal(-532462766, c.ExitCode);
        Assert.Equal("stack\n\nexit info", c.Details);
        Assert.Equal(@"C:\d.dmp", c.DumpPath);
        // A completed record is not completed twice.
        Assert.False(db.CompleteExceptionCrash(pid: 42, start, 1, "again", null));
    }
}
