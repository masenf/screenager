using System.Globalization;

namespace Screenager.Reporting;

public enum SlotKind
{
    Active,         // used, within allowed hours
    ActiveOutside,  // used outside allowed hours (e.g. a parent override during bedtime)
    Inactive,       // not used
    Future,         // after the report was generated
}

public sealed record TimelineRun(SlotKind Kind, int StartMinute, int Minutes);

/// <summary>
/// When during the day the PC was in active use, in 5-minute slots across the allowed hours
/// (bedtime end → bedtime start, or the whole logical day when no bedtime is set), widened to
/// include any activity outside them. Minute offsets are relative to the logical day start.
/// </summary>
public sealed class DayTimeline
{
    private const int SlotMinutes = 5;

    public required DateTime DayStart { get; init; }
    public required int RangeStart { get; init; }
    public required int RangeEnd { get; init; }
    public required IReadOnlyList<TimelineRun> Runs { get; init; }
    /// <summary>False when no per-minute data exists (e.g. days recorded before this feature).</summary>
    public required bool HasData { get; init; }

    public DateTime At(int minute) => DayStart.AddMinutes(minute);

    public static DayTimeline Build(IReadOnlyDictionary<int, int> activeSecondsByMinute, Config cfg, string day, DateTime now)
    {
        var dayStart = DateTime.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddHours(cfg.ResetHour);
        int resetMinutes = cfg.ResetHour * 60;
        int Offset(TimeSpan timeOfDay) => (((int)timeOfDay.TotalMinutes - resetMinutes) % 1440 + 1440) % 1440;

        // Allowed window: from the end of bedtime to its start.
        int allowedStart = 0, allowedEnd = 1440;
        if (cfg.BedtimeStart is { } bedStart)
        {
            int s = Offset(cfg.BedtimeEnd ?? TimeSpan.FromHours(cfg.ResetHour));
            int e = Offset(bedStart);
            if (e == 0) e = 1440;
            if (e > s) (allowedStart, allowedEnd) = (s, e);
        }

        var used = activeSecondsByMinute.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
        int rangeStart = allowedStart, rangeEnd = allowedEnd;
        if (used.Count > 0)
        {
            rangeStart = Math.Min(rangeStart, used.Min() / 60 * 60);
            rangeEnd = Math.Max(rangeEnd, (used.Max() / 60 + 1) * 60);
        }
        rangeStart = Math.Clamp(rangeStart, 0, 1440);
        rangeEnd = Math.Clamp(rangeEnd, rangeStart + 60, 1440);

        int nowMinute = (int)Math.Floor((now - dayStart).TotalMinutes);

        var runs = new List<TimelineRun>();
        for (int slot = rangeStart; slot < rangeEnd; slot += SlotMinutes)
        {
            int len = Math.Min(SlotMinutes, rangeEnd - slot);
            SlotKind kind;
            if (slot >= nowMinute)
            {
                kind = SlotKind.Future;
            }
            else
            {
                int seconds = 0;
                for (int m = slot; m < slot + len; m++)
                    seconds += activeSecondsByMinute.TryGetValue(m, out var s) ? s : 0;
                bool active = seconds * 2 >= len * 60; // at least half the slot in use
                bool inside = slot >= allowedStart && slot < allowedEnd;
                kind = !active ? SlotKind.Inactive : inside ? SlotKind.Active : SlotKind.ActiveOutside;
            }

            if (runs.Count > 0 && runs[^1].Kind == kind)
                runs[^1] = runs[^1] with { Minutes = runs[^1].Minutes + len };
            else
                runs.Add(new TimelineRun(kind, slot, len));
        }

        return new DayTimeline
        {
            DayStart = dayStart,
            RangeStart = rangeStart,
            RangeEnd = rangeEnd,
            Runs = runs,
            HasData = used.Count > 0,
        };
    }

    /// <summary>Contiguous stretches of use, e.g. "15:30–16:10".</summary>
    public IEnumerable<(DateTime from, DateTime to)> ActivePeriods() =>
        Runs.Where(r => r.Kind is SlotKind.Active or SlotKind.ActiveOutside)
            .Aggregate(new List<(int s, int e)>(), (acc, r) =>
            {
                if (acc.Count > 0 && acc[^1].e == r.StartMinute)
                    acc[^1] = (acc[^1].s, r.StartMinute + r.Minutes);
                else
                    acc.Add((r.StartMinute, r.StartMinute + r.Minutes));
                return acc;
            })
            .Select(p => (At(p.s), At(p.e)));

    /// <summary>Axis ticks (minute offsets) at whole hours, about 4–7 of them across the range.</summary>
    public List<int> Ticks()
    {
        int spanHours = (RangeEnd - RangeStart + 59) / 60;
        int step = new[] { 1, 2, 3, 4, 6 }.FirstOrDefault(k => spanHours / k <= 6, 6);
        var ticks = new List<int>();
        int firstHour = (RangeStart + 59) / 60 * 60;
        for (int m = firstHour; m < RangeEnd; m += 60)
        {
            int wallHour = At(m).Hour;
            if (wallHour % step == 0)
                ticks.Add(m);
        }
        if (ticks.Count == 0 || ticks[0] - RangeStart >= step * 30)
            ticks.Insert(0, RangeStart);
        if (RangeEnd - ticks[^1] >= step * 30)
            ticks.Add(RangeEnd);
        else
            ticks[^1] = RangeEnd; // keep the end labelled; it is the last allowed time
        return ticks;
    }
}
