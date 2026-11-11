using Screenager.Tracking;

namespace Screenager.Tests;

public class LogicalClockTests
{
    private static readonly LogicalClock Clock = new(resetHour: 4, TimeSpan.FromHours(21), TimeSpan.FromHours(6));

    [Theory]
    [InlineData("2026-11-10 03:59", "2026-11-09")] // late night counts against the previous day
    [InlineData("2026-11-10 04:00", "2026-11-10")] // new day starts at the reset hour
    [InlineData("2026-11-10 23:59", "2026-11-10")]
    public void DayRollsOverAtResetHour(string local, string expectedDay)
    {
        Assert.Equal(expectedDay, Clock.DayKey(DateTime.Parse(local)));
    }

    [Theory]
    [InlineData("2026-11-10 03:00", "2026-11-09 04:00")]
    [InlineData("2026-11-10 04:00", "2026-11-10 04:00")]
    [InlineData("2026-11-10 18:30", "2026-11-10 04:00")]
    public void DayStartIsTheMostRecentResetHour(string local, string expected)
    {
        Assert.Equal(DateTime.Parse(expected), Clock.DayStart(DateTime.Parse(local)));
    }

    [Theory]
    [InlineData("20:59", false)]
    [InlineData("21:00", true)]
    [InlineData("23:30", true)]
    [InlineData("02:00", true)]  // window wraps past midnight
    [InlineData("05:59", true)]
    [InlineData("06:00", false)]
    [InlineData("12:00", false)]
    public void BedtimeWindowWrapsMidnight(string time, bool expected)
    {
        Assert.Equal(expected, Clock.IsBedtime(DateTime.Parse("2026-11-10 " + time)));
    }

    [Fact]
    public void NonWrappingBedtimeWindow()
    {
        var clock = new LogicalClock(4, TimeSpan.FromHours(13), TimeSpan.FromHours(14));
        Assert.True(clock.IsBedtime(DateTime.Parse("2026-11-10 13:30")));
        Assert.False(clock.IsBedtime(DateTime.Parse("2026-11-10 14:00")));
        Assert.False(clock.IsBedtime(DateTime.Parse("2026-11-10 12:59")));
    }

    [Fact]
    public void NoBedtimeConfiguredMeansNeverBedtime()
    {
        var clock = new LogicalClock(4, null, null);
        Assert.False(clock.IsBedtime(DateTime.Parse("2026-11-10 23:00")));
    }

    [Fact]
    public void BedtimeEndDefaultsToResetHour()
    {
        var clock = new LogicalClock(5, TimeSpan.FromHours(22), null);
        Assert.True(clock.IsBedtime(DateTime.Parse("2026-11-10 04:59")));
        Assert.False(clock.IsBedtime(DateTime.Parse("2026-11-10 05:00")));
    }
}
