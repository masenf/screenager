using Screenager.Data;
using Screenager.Reporting;

namespace Screenager.Tests;

public class FocusGroupingTests
{
    [Theory]
    [InlineData("(1) I Lied. THIS is the Best Among Us Map - YouTube - Google Chrome", "I Lied. THIS is the Best Among Us Map - YouTube")]
    [InlineData("Tasks | Among Us Wiki | Fandom - Google Chrome", "Tasks | Among Us Wiki | Fandom")]
    [InlineData("Untitled and 14 more pages - Personal - Microsoft\u200b Edge", "Untitled")]
    [InlineData("Docs - Microsoft Edge", "Docs")]
    [InlineData("Among Us - Wiki - Google Chrome", "Among Us - Wiki")] // inner separators survive
    [InlineData("Minecraft 26.3", "Minecraft 26.3")]
    [InlineData("", AppUsage.NoTitle)]
    public void CleanTitleStripsBrowserNoise(string raw, string expected)
    {
        Assert.Equal(expected, FocusGrouping.CleanTitle(raw));
    }

    [Theory]
    [InlineData("(1) We Answer Your Dumb Questions - YouTube", "We Answer Your Dumb Questions")]
    [InlineData("(12+) Stream - Twitch", "Stream")]
    [InlineData("Plain title", "Plain title")]
    public void CleanPageTitleStripsNotificationCountAndSiteSuffix(string raw, string expected)
    {
        Assert.Equal(expected, FocusGrouping.CleanPageTitle(raw));
    }

    [Fact]
    public void GroupsWindowsByAppWithFriendlyNames()
    {
        var apps = FocusGrouping.Group(
        [
            new FocusRow("chrome", "(1) Video A - YouTube - Google Chrome", 600),
            new FocusRow("chrome", "Video A - YouTube - Google Chrome", 60), // same page once notifications cleared
            new FocusRow("chrome", "Wiki - Google Chrome", 300),
            new FocusRow("javaw", "Minecraft 26.3 - Multiplayer (3rd-party Server)", 900),
            new FocusRow("ApplicationFrameHost", "Microsoft Store", 120),
            new FocusRow("Minecraft", "Minecraft Launcher", 90),
        ]);

        Assert.Equal(new[] { "Google Chrome", "Minecraft (Java)", "Microsoft Store", "Minecraft Launcher" }, apps.Select(a => a.Name));
        var chrome = apps[0];
        Assert.Equal(960, chrome.Seconds);
        Assert.Equal(new WindowUsage("Video A - YouTube", 660), chrome.Windows[0]);
        Assert.True(chrome.ShowWindows);
        Assert.False(apps.Single(a => a.Name == "Minecraft Launcher").ShowWindows); // single window named like the app
    }

    [Fact]
    public void SmallAppsBeyondTheTopFewFoldIntoOther()
    {
        var rows = Enumerable.Range(1, 6).Select(i => new FocusRow($"big{i}", "w", 600 + i))
            .Concat([new FocusRow("tiny1", "w", 10), new FocusRow("tiny2", "w", 5)]);
        var apps = FocusGrouping.Group(rows);
        Assert.Equal(7, apps.Count);
        Assert.Equal("Other apps (2)", apps[^1].Name);
        Assert.Equal(15, apps[^1].Seconds);
    }
}

public class DayTimelineTests : IDisposable
{
    private readonly TempEnv _env = new();
    public void Dispose() => _env.Dispose();

    private const string Day = "2026-11-10";
    private Config Cfg => _env.Config("[limits]\nreset_hour = 4\nbedtime_start = 21:00\nbedtime_end = 06:00\n");

    // Minute offsets are relative to the 04:00 day start.
    private static int M(string hhmm) => (int)TimeSpan.Parse(hhmm).TotalMinutes - 240;

    private static Dictionary<int, int> Used(string from, string to, int secondsPerMinute = 60) =>
        Enumerable.Range(M(from), M(to) - M(from)).ToDictionary(m => m, _ => secondsPerMinute);

    [Fact]
    public void SpansAllowedHoursAndMarksActiveSlots()
    {
        var tl = DayTimeline.Build(Used("15:30", "16:15"), Cfg, Day, DateTime.Parse($"{Day} 20:00"));

        Assert.True(tl.HasData);
        Assert.Equal(M("06:00"), tl.RangeStart);
        Assert.Equal(M("21:00"), tl.RangeEnd);
        Assert.Equal(tl.RangeEnd - tl.RangeStart, tl.Runs.Sum(r => r.Minutes));
        var period = Assert.Single(tl.ActivePeriods());
        Assert.Equal((DateTime.Parse($"{Day} 15:30"), DateTime.Parse($"{Day} 16:15")), period);
        Assert.Equal(SlotKind.Future, tl.Runs[^1].Kind);
        Assert.Equal(M("20:00"), tl.Runs[^1].StartMinute);
    }

    [Fact]
    public void SlotNeedsAtLeastHalfItsTimeToCountAsActive()
    {
        var mostly = DayTimeline.Build(Used("10:00", "10:03"), Cfg, Day, DateTime.Parse($"{Day} 20:00")); // 3 of 5 min
        var barely = DayTimeline.Build(Used("10:00", "10:02"), Cfg, Day, DateTime.Parse($"{Day} 20:00")); // 2 of 5 min
        Assert.Single(mostly.ActivePeriods());
        Assert.Empty(barely.ActivePeriods());
    }

    [Fact]
    public void UseOutsideAllowedHoursWidensTheBarAndIsFlagged()
    {
        var tl = DayTimeline.Build(Used("21:00", "21:20"), Cfg, Day, DateTime.Parse($"{Day} 22:30"));
        Assert.Equal(M("22:00"), tl.RangeEnd); // widened to the next whole hour
        Assert.Contains(tl.Runs, r => r.Kind == SlotKind.ActiveOutside && r.StartMinute == M("21:00") && r.Minutes == 20);
        Assert.DoesNotContain(tl.Runs, r => r.Kind == SlotKind.Active);
    }

    [Fact]
    public void NoBedtimeMeansTheWholeLogicalDay()
    {
        var cfg = _env.Config("[limits]\nreset_hour = 4\n");
        var tl = DayTimeline.Build(new Dictionary<int, int>(), cfg, Day, DateTime.Parse($"{Day} 20:00"));
        Assert.False(tl.HasData);
        Assert.Equal(0, tl.RangeStart);
        Assert.Equal(1440, tl.RangeEnd);
    }

    [Fact]
    public void TicksLabelBothEndsWithoutCrowding()
    {
        var tl = DayTimeline.Build(Used("10:00", "11:00"), Cfg, Day, DateTime.Parse($"{Day} 20:00"));
        var ticks = tl.Ticks();
        Assert.Equal(tl.RangeStart, ticks[0]);
        Assert.Equal(tl.RangeEnd, ticks[^1]);
        Assert.InRange(ticks.Count, 3, 7);
        Assert.Equal(ticks.OrderBy(t => t), ticks);
    }
}

public class ReportBuilderTests : IDisposable
{
    private readonly TempEnv _env = new();
    public void Dispose() => _env.Dispose();

    private const string Day = "2026-11-10";

    private (string subject, string html, string text) Build(Action<Database>? seed = null, IReadOnlyList<CrashRecord>? crashes = null)
    {
        var cfg = _env.Config("[limits]\ndaily_minutes = 180\nreset_hour = 4\nbedtime_start = 21:00\nbedtime_end = 06:00\n");
        using var db = _env.OpenDb();
        db.SetActiveSeconds(Day, 2 * 3600 + 56 * 60);
        seed?.Invoke(db);
        return ReportBuilder.Build(db, cfg, Day, "PC-1", crashes, crashes?.Count ?? 0, DateTime.Parse($"{Day} 20:00"));
    }

    private static BrowserVisit Visit(string url, string title, string time = "18:29") =>
        new("Chrome", url, title, 1, DateTime.Parse($"{Day} {time}"));

    [Fact]
    public void SummaryShowsUsageAgainstBudget()
    {
        var (subject, html, text) = Build();
        Assert.Equal("Screenager report — 2026-11-10 (2h 56m active)", subject);
        Assert.Contains("2h 56m", html);
        Assert.Contains("of 3h 0m", html);
        Assert.Contains("98%", html);
        Assert.Contains("4m 0s left", html);
        Assert.Contains("2h 56m of 3h 0m", text);
    }

    [Fact]
    public void IsResponsiveSingleColumnEmail()
    {
        var (_, html, _) = Build();
        Assert.Contains("name=\"viewport\"", html);
        Assert.Contains("max-width:640px", html);
        Assert.Contains("x-apple-data-detectors", html);
    }

    [Fact]
    public void VideosAndSitesLinkToTheFullUrl()
    {
        const string video = "https://www.youtube.com/watch?v=aB3dE5fG7hJ&t=42";
        const string page = "https://minecraft.menu/server-pixelblock/vote";
        var (_, html, text) = Build(db => db.ReplaceBrowserVisits(Day,
            [Visit(video, "(1) Among Us Evidence - YouTube"), Visit(page, "Vote for PixelBlock", "17:37")]));

        Assert.Contains("href=\"https://www.youtube.com/watch?v=aB3dE5fG7hJ&amp;t=42\"", html);
        Assert.Contains($"href=\"{page}\"", html);
        Assert.Contains(">Among Us Evidence</a>", html);           // video title cleaned
        Assert.DoesNotContain(">Site<", html);                     // no separate Site column
        Assert.Contains(video, text);
        Assert.Contains(page, text);
    }

    [Fact]
    public void OnlyHttpUrlsBecomeLinksAndTitlesAreEscaped()
    {
        var (_, html, _) = Build(db => db.ReplaceBrowserVisits(Day,
        [
            Visit("javascript:alert(1)", "evil"),
            Visit("file:///C:/secret.txt", "local file"),
            Visit("https://example.com/", "<script>alert('x')</script>"),
        ]));
        Assert.DoesNotContain("href=\"javascript:", html);
        Assert.DoesNotContain("href=\"file:", html);
        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void AppsAreGroupedWithShareBars()
    {
        var (_, html, text) = Build(db =>
        {
            db.AddFocusSeconds(Day, "chrome", "A - Google Chrome", 300);
            db.AddFocusSeconds(Day, "chrome", "B - Google Chrome", 100);
            db.AddFocusSeconds(Day, "javaw", "Minecraft 26.3", 400);
        });
        Assert.Contains("<b>Google Chrome</b>", html);
        Assert.Contains("50%", html);
        Assert.Contains("Google Chrome — 6m 40s (50%)", text);
    }

    [Fact]
    public void DayWithoutPerMinuteDataSaysSo()
    {
        var (_, html, _) = Build();
        Assert.Contains("No hourly data recorded for this day.", html);
    }

    [Fact]
    public void StabilitySectionAndSubjectReflectCrashes()
    {
        var (subjectClean, htmlClean, _) = Build();
        Assert.DoesNotContain("stability", subjectClean);
        Assert.Contains("No crashes, hangs, or restarts since the last report.", htmlClean);

        var crash = new CrashRecord { Id = 7, Time = DateTime.Parse($"{Day} 12:00"), Kind = "hang", Summary = "Timer app hung", Details = "threads…" };
        var (subject, html, text) = Build(crashes: [crash]);
        Assert.EndsWith("— 1 stability issue", subject);
        Assert.Contains("Timer app hung", html);
        Assert.Contains("threads…", text);
    }
}

public class ReportServiceTests : IDisposable
{
    private readonly TempEnv _env = new();
    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task CrashesStayQueuedWhenTheEmailIsNotSent()
    {
        var cfg = _env.Config("[report]\ncollect_browser_history = false\n"); // no Mailgun settings → send fails
        using var db = _env.OpenDb();
        db.InsertCrash(new CrashRecord { Time = DateTime.Now, Kind = "exit", Summary = "x" });
        var clock = new Screenager.Tracking.LogicalClock(cfg.ResetHour, null, null);

        var (ok, _) = await new ReportService(db, cfg, clock).SendTodayAsync();

        Assert.False(ok);
        Assert.Equal(1, db.CountUnreportedCrashes());
    }
}
