namespace Screenager.Tracking;

/// <summary>
/// Maps real local time to a "logical day" string that rolls over at a configurable hour
/// (so e.g. a 4am reset keeps late-night use on the previous day's budget), and answers
/// bedtime-window questions. Anchored on the local date, never on elapsed seconds, so DST
/// transitions don't distort the day.
/// </summary>
public sealed class LogicalClock
{
    private readonly int _resetHour;
    private readonly TimeSpan? _bedtimeStart;
    private readonly TimeSpan? _bedtimeEnd;

    public LogicalClock(int resetHour, TimeSpan? bedtimeStart, TimeSpan? bedtimeEnd)
    {
        _resetHour = resetHour;
        _bedtimeStart = bedtimeStart;
        _bedtimeEnd = bedtimeEnd;
    }

    /// <summary>The logical day key (yyyy-MM-dd) for a given local time.</summary>
    public string DayKey(DateTime localNow)
    {
        var d = localNow.AddHours(-_resetHour);
        return d.ToString("yyyy-MM-dd");
    }

    public string Today() => DayKey(DateTime.Now);

    /// <summary>The local wall-clock instant at which the given time's logical day began.</summary>
    public DateTime DayStart(DateTime localNow)
    {
        var anchor = localNow.Date.AddHours(_resetHour);
        return localNow < anchor ? anchor.AddDays(-1) : anchor;
    }

    /// <summary>
    /// True when the given local time falls inside the configured bedtime window.
    /// Handles windows that wrap past midnight (e.g. 21:00 -> 06:00).
    /// </summary>
    public bool IsBedtime(DateTime localNow)
    {
        if (_bedtimeStart is not { } start)
            return false;
        var end = _bedtimeEnd ?? TimeSpan.FromHours(_resetHour);
        var t = localNow.TimeOfDay;

        if (start <= end)
            return t >= start && t < end;
        // wraps midnight
        return t >= start || t < end;
    }
}
