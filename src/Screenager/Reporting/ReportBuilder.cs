using System.Globalization;
using System.Net;
using System.Text;
using Screenager.Data;

namespace Screenager.Reporting;

/// <summary>
/// Assembles the daily HTML/text report from persisted usage, focus time, and browser history.
/// The HTML is a single column (max 640px) built from tables with inline styles, so it reads
/// well on a phone and survives Apple Mail, Gmail and Outlook alike: every row is either
/// full-width or "text that wraps | time that doesn't", and bars are fixed-layout tables.
/// </summary>
public static class ReportBuilder
{
    private static readonly string[] VideoHosts =
    {
        "youtube.com/watch", "youtu.be/", "youtube.com/shorts", "vimeo.com/",
        "twitch.tv/", "netflix.com/watch", "tiktok.com/", "dailymotion.com/video",
        "hulu.com/watch", "disneyplus.com/video",
    };

    private const int WindowsPerApp = 5;
    private const int MaxSites = 150;
    private const int CrashDetailLimit = 10;    // incidents shown with full details
    private const int CrashDetailChars = 8_000; // per incident

    // Palette: plain, high-contrast, and distinguishable for common color-vision deficiencies.
    private const string Ink = "#1f2328";
    private const string Muted = "#6a737d";
    private const string Rule = "#e5e7eb";
    private const string Track = "#e3e6ea";
    private const string Blue = "#2f6fde";
    private const string Orange = "#e8912d";
    private const string Red = "#c2410c";
    private const string FutureFill = "#f4f5f7";
    private const string LinkColor = "#1a56db";

    public static (string subject, string html, string text) Build(
        Database db, Config cfg, string day, string machine,
        IReadOnlyList<CrashRecord>? crashes = null, int totalUnreportedCrashes = 0, DateTime? generatedAt = null)
    {
        crashes ??= Array.Empty<CrashRecord>();
        var now = generatedAt ?? DateTime.Now;
        var usage = db.GetUsage(day);
        var apps = FocusGrouping.Group(db.GetFocus(day, 1000));
        var visits = db.GetBrowserVisits(day, 500);
        var videos = visits.Where(v => VideoHosts.Any(h => v.Url.Contains(h, StringComparison.OrdinalIgnoreCase)))
                           .Take(50).ToList();
        var timeline = DayTimeline.Build(db.GetActivityMinutes(day), cfg, day, now);

        int budget = cfg.DailyMinutes * 60 + usage.BonusSeconds;
        string subject = $"Screenager report — {day} ({Fmt(usage.ActiveSeconds)} active)";
        if (crashes.Count > 0)
            subject += $" — {crashes.Count} stability issue{(crashes.Count == 1 ? "" : "s")}";

        string dateLine = DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture) : day;

        // ---------- HTML ----------
        var h = new StringBuilder();
        h.Append("""
            <!DOCTYPE html><html><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="color-scheme" content="light"><meta name="supported-color-schemes" content="light">
            <meta name="format-detection" content="telephone=no, date=no, address=no, email=no">
            <style>
              /* Apple Mail turns times and domains into links; keep them looking like plain text. */
              a[x-apple-data-detectors] { color: inherit !important; text-decoration: none !important;
                font: inherit !important; }
              a.lnk { color: #1a56db; text-decoration: none; }
              @media (max-width: 480px) { .wrap { padding: 14px 12px !important; } }
            </style></head>
            """);
        h.Append($"<body style=\"margin:0;padding:0;background:#ffffff\"><div class=\"wrap\" style=\"max-width:640px;margin:0 auto;padding:20px 16px;" +
                 $"font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Arial,sans-serif;font-size:15px;line-height:1.4;color:{Ink}\">");
        h.Append("<div style=\"font-size:22px;font-weight:700;margin:0 0 2px\">Screenager daily report</div>");
        h.Append($"<div style=\"color:{Muted};font-size:14px\">{E(dateLine)} · {E(machine)}</div>");

        AppendScreenTimeHtml(h, cfg, usage, budget, timeline, now);
        AppendAppsHtml(h, apps);
        AppendVideosHtml(h, videos);
        AppendSitesHtml(h, visits);
        AppendStabilityHtml(h, crashes, totalUnreportedCrashes);
        h.Append("</div></body></html>");

        // ---------- Plain text ----------
        var t = new StringBuilder();
        t.AppendLine($"Screenager daily report — {dateLine} ({machine})");
        AppendScreenTimeText(t, usage, budget, timeline);
        AppendAppsText(t, apps);
        AppendVideosText(t, videos);
        AppendSitesText(t, visits);
        AppendStabilityText(t, crashes, totalUnreportedCrashes);

        return (subject, h.ToString(), t.ToString());
    }

    // =====================================================================================
    // Screen time
    // =====================================================================================

    private static void AppendScreenTimeHtml(StringBuilder h, Config cfg, DailyUsage usage, int budget, DayTimeline tl, DateTime now)
    {
        Heading(h, "Screen time");

        double frac = budget > 0 ? (double)usage.ActiveSeconds / budget : 1;
        bool reached = usage.ActiveSeconds >= budget;
        string pct = budget > 0 ? $"{Math.Round(frac * 100):0}%" : "";

        h.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"border-collapse:collapse\"><tr>");
        h.Append($"<td style=\"vertical-align:bottom\"><span style=\"font-size:24px;font-weight:700\">{Fmt(usage.ActiveSeconds)}</span>" +
                 $"<span style=\"color:{Muted}\"> of {Fmt(budget)}</span></td>");
        h.Append($"<td align=\"right\" style=\"vertical-align:bottom;white-space:nowrap;font-weight:600;color:{(reached ? Red : Ink)}\">{pct}</td>");
        h.Append("</tr></table>");
        Bar(h, 14, (Math.Min(frac, 1), reached ? Red : Blue), (Math.Max(0, 1 - frac), Track));

        var notes = new List<string>
        {
            reached ? "Limit reached" : $"{Fmt(budget - usage.ActiveSeconds)} left",
            $"daily limit {Fmt(cfg.DailyMinutes * 60)}",
        };
        if (usage.BonusSeconds > 0)
            notes.Add($"+{Fmt(usage.BonusSeconds)} granted");
        h.Append($"<div style=\"color:{Muted};font-size:13px;margin-top:4px\">{E(string.Join(" · ", notes))}</div>");

        // --- day timeline ---
        h.Append($"<div style=\"margin-top:18px;font-weight:600\">During the day</div>");
        if (!tl.HasData)
        {
            h.Append($"<div style=\"color:{Muted};font-size:13px\">No hourly data recorded for this day.</div>");
            return;
        }

        var periods = tl.ActivePeriods().ToList();
        string periodText = periods.Count <= 6
            ? string.Join(", ", periods.Select(p => $"{p.from:HH:mm}–{p.to:HH:mm}"))
            : $"{periods.Count} sessions between {periods[0].from:HH:mm} and {periods[^1].to:HH:mm}";
        h.Append($"<div style=\"color:{Muted};font-size:13px;margin-bottom:6px\">Active {E(periodText)}</div>");

        int span = tl.RangeEnd - tl.RangeStart;
        Bar(h, 22, tl.Runs.Select(r => ((double)r.Minutes / span, KindColor(r.Kind))).ToArray());

        // Axis labels: first left-aligned, last right-aligned, the rest centred on their tick.
        var ticks = tl.Ticks();
        h.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"border-collapse:collapse;table-layout:fixed;margin-top:3px;font-size:11px;color:{Muted}\"><tr>");
        for (int i = 0; i < ticks.Count; i++)
        {
            int from = i == 0 ? tl.RangeStart : (ticks[i - 1] + ticks[i]) / 2;
            int to = i == ticks.Count - 1 ? tl.RangeEnd : (ticks[i] + ticks[i + 1]) / 2;
            if (to <= from) continue;
            string align = i == 0 ? "left" : i == ticks.Count - 1 ? "right" : "center";
            h.Append($"<td style=\"width:{Pct((double)(to - from) / span)};text-align:{align};white-space:nowrap;overflow:hidden\">{tl.At(ticks[i]):HH:mm}</td>");
        }
        h.Append("</tr></table>");

        // Legend: only the kinds that actually appear.
        var kinds = tl.Runs.Select(r => r.Kind).Distinct().OrderBy(k => k).ToList();
        h.Append($"<div style=\"font-size:12px;color:{Muted};margin-top:6px\">");
        foreach (var k in kinds)
        {
            h.Append($"<span style=\"white-space:nowrap;margin-right:12px\"><span style=\"display:inline-block;width:10px;height:10px;" +
                     $"background:{KindColor(k)};border:1px solid {Rule};vertical-align:-1px;margin-right:4px\"></span>{KindLabel(k)}</span> ");
        }
        h.Append("</div>");
    }

    private static string KindColor(SlotKind k) => k switch
    {
        SlotKind.Active => Blue,
        SlotKind.ActiveOutside => Orange,
        SlotKind.Inactive => Track,
        _ => FutureFill,
    };

    private static string KindLabel(SlotKind k) => k switch
    {
        SlotKind.Active => "Active",
        SlotKind.ActiveOutside => "Active outside allowed hours",
        SlotKind.Inactive => "Inactive",
        _ => "After this report",
    };

    private static void AppendScreenTimeText(StringBuilder t, DailyUsage usage, int budget, DayTimeline tl)
    {
        t.AppendLine();
        t.AppendLine("SCREEN TIME");
        double frac = budget > 0 ? Math.Min(1, (double)usage.ActiveSeconds / budget) : 1;
        int filled = (int)Math.Round(frac * 20);
        t.AppendLine($"  {Fmt(usage.ActiveSeconds)} of {Fmt(budget)}  [{new string('#', filled)}{new string('-', 20 - filled)}]  " +
                     (usage.ActiveSeconds >= budget ? "limit reached" : $"{Fmt(budget - usage.ActiveSeconds)} left"));
        if (tl.HasData)
            t.AppendLine("  Active " + string.Join(", ", tl.ActivePeriods().Select(p => $"{p.from:HH:mm}-{p.to:HH:mm}")));
    }

    // =====================================================================================
    // Apps
    // =====================================================================================

    private static void AppendAppsHtml(StringBuilder h, List<AppUsage> apps)
    {
        Heading(h, "Apps");
        if (apps.Count == 0)
        {
            Empty(h, "No app usage recorded.");
            return;
        }

        int total = Math.Max(1, apps.Sum(a => a.Seconds));
        foreach (var app in apps)
        {
            double share = (double)app.Seconds / total;
            h.Append("<div style=\"margin:0 0 14px\">");
            Row(h, $"<b>{E(app.Name)}</b>", $"{Fmt(app.Seconds)} <span style=\"color:{Muted};font-weight:400\">· {Share(share)}</span>", rightBold: true);
            Bar(h, 6, (share, Blue), (1 - share, Track));
            if (app.ShowWindows)
            {
                foreach (var w in app.Windows.Take(WindowsPerApp))
                    Row(h, E(Trunc(w.Title, 110)), Fmt(w.Seconds), small: true, indent: true);
                int more = app.Windows.Count - WindowsPerApp;
                if (more > 0)
                    Row(h, $"<span style=\"color:{Muted}\">{more} more</span>", Fmt(app.Windows.Skip(WindowsPerApp).Sum(w => w.Seconds)), small: true, indent: true);
            }
            h.Append("</div>");
        }
        h.Append($"<div style=\"color:{Muted};font-size:12px\">Percentages are of all foreground time recorded today.</div>");
    }

    private static void AppendAppsText(StringBuilder t, List<AppUsage> apps)
    {
        t.AppendLine();
        t.AppendLine("APPS");
        int total = Math.Max(1, apps.Sum(a => a.Seconds));
        foreach (var app in apps)
        {
            t.AppendLine($"  {app.Name} — {Fmt(app.Seconds)} ({Share((double)app.Seconds / total)})");
            if (!app.ShowWindows) continue;
            foreach (var w in app.Windows.Take(WindowsPerApp))
                t.AppendLine($"      {Fmt(w.Seconds),8}  {Trunc(w.Title, 80)}");
            if (app.Windows.Count > WindowsPerApp)
                t.AppendLine($"      {Fmt(app.Windows.Skip(WindowsPerApp).Sum(w => w.Seconds)),8}  ({app.Windows.Count - WindowsPerApp} more)");
        }
    }

    // =====================================================================================
    // Videos & sites
    // =====================================================================================

    private static void AppendVideosHtml(StringBuilder h, List<BrowserVisit> videos)
    {
        Heading(h, $"Videos watched ({videos.Count})");
        if (videos.Count == 0)
        {
            Empty(h, "None detected.");
            return;
        }
        foreach (var v in videos)
        {
            string title = FocusGrouping.CleanPageTitle(v.Title);
            if (title.Length == 0) title = v.Url;
            h.Append("<div style=\"margin:0 0 10px\">");
            h.Append($"<div>{Link(v.Url, Trunc(title, 140))}</div>");
            h.Append($"<div style=\"color:{Muted};font-size:12px\">{v.LastVisit:HH:mm} · {E(SiteName(v.Url))} · {E(v.Browser)}</div>");
            h.Append("</div>");
        }
    }

    private static void AppendVideosText(StringBuilder t, List<BrowserVisit> videos)
    {
        t.AppendLine();
        t.AppendLine($"VIDEOS WATCHED ({videos.Count})");
        foreach (var v in videos)
        {
            t.AppendLine($"  {v.LastVisit:HH:mm}  {Trunc(FocusGrouping.CleanPageTitle(v.Title), 90)}");
            t.AppendLine($"         {v.Url}");
        }
    }

    private static void AppendSitesHtml(StringBuilder h, List<BrowserVisit> visits)
    {
        Heading(h, $"Sites visited ({visits.Count})");
        if (visits.Count == 0)
        {
            Empty(h, "No browser history collected.");
            return;
        }
        h.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"border-collapse:collapse\">");
        foreach (var v in visits.Take(MaxSites))
        {
            string title = FocusGrouping.StripNotificationCount(v.Title) is { Length: > 0 } st ? st : v.Url;
            h.Append($"<tr><td style=\"width:44px;white-space:nowrap;vertical-align:top;padding:3px 10px 3px 0;color:{Muted};font-size:13px\">{v.LastVisit:HH:mm}</td>");
            h.Append($"<td style=\"vertical-align:top;padding:3px 0;word-break:break-word;overflow-wrap:anywhere\">{Link(v.Url, Trunc(title, 140))}</td></tr>");
        }
        h.Append("</table>");
        if (visits.Count > MaxSites)
            Empty(h, $"…and {visits.Count - MaxSites} more.");
    }

    private static void AppendSitesText(StringBuilder t, List<BrowserVisit> visits)
    {
        t.AppendLine();
        t.AppendLine($"SITES VISITED ({visits.Count})");
        foreach (var v in visits.Take(MaxSites))
        {
            t.AppendLine($"  {v.LastVisit:HH:mm}  {Trunc(FocusGrouping.StripNotificationCount(v.Title) is { Length: > 0 } st ? st : v.Url, 90)}");
            t.AppendLine($"         {v.Url}");
        }
    }

    private static string SiteName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "";
        string host = u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;
        return host switch
        {
            "youtube.com" or "m.youtube.com" or "youtu.be" => "YouTube",
            "twitch.tv" => "Twitch",
            "vimeo.com" => "Vimeo",
            "netflix.com" => "Netflix",
            "tiktok.com" => "TikTok",
            _ => host,
        };
    }

    // =====================================================================================
    // Stability (crashes, hangs, restarts since the last report)
    // =====================================================================================

    private static void AppendStabilityHtml(StringBuilder h, IReadOnlyList<CrashRecord> crashes, int total)
    {
        Heading(h, "Stability");
        if (crashes.Count == 0)
        {
            Empty(h, "No crashes, hangs, or restarts since the last report.");
            return;
        }

        h.Append($"<div style=\"color:{Red};margin-bottom:8px\"><b>{crashes.Count} issue{(crashes.Count == 1 ? "" : "s")} since the last report.</b> " +
                 "Screenager restarted itself automatically; the details are for fixing the cause.</div>");
        foreach (var c in crashes)
        {
            Row(h, $"<b>{E(KindLabel(c.Kind))}</b> <span style=\"color:{Muted}\">{c.Time:MMM d HH:mm}</span>",
                c.UptimeSeconds is { } u ? $"up {Fmt((int)Math.Min(u, int.MaxValue))}" : "", small: true);
            h.Append($"<div style=\"font-size:13px;margin:0 0 8px;word-break:break-word;overflow-wrap:anywhere\">{E(Trunc(c.Summary, 200))}</div>");
        }
        if (total > crashes.Count)
            Empty(h, $"{total - crashes.Count} more will be included in the next report.");

        foreach (var c in crashes.Take(CrashDetailLimit))
        {
            h.Append($"<div style=\"margin-top:14px;font-weight:600\">#{c.Id} · {c.Time:yyyy-MM-dd HH:mm:ss} · {E(KindLabel(c.Kind))}</div>");
            h.Append($"<div style=\"color:{Muted};font-size:12px;word-break:break-word;overflow-wrap:anywhere\">pid {c.Pid?.ToString() ?? "?"} · version {E(c.AppVersion ?? "?")}" +
                     (c.DumpPath != null ? $" · dump {E(c.DumpPath)}" : "") + "</div>");
            h.Append($"<pre style=\"white-space:pre-wrap;word-break:break-word;overflow-wrap:anywhere;font-size:11px;line-height:1.35;background:#f6f8fa;" +
                     $"padding:8px;border:1px solid {Rule};border-radius:4px;margin:6px 0 0\">{E(Trunc(c.Details, CrashDetailChars))}</pre>");
        }
        if (crashes.Count > CrashDetailLimit)
            Empty(h, $"Details omitted for {crashes.Count - CrashDetailLimit} further issue(s); they are in the crashes table of screenager.db.");
    }

    private static void AppendStabilityText(StringBuilder t, IReadOnlyList<CrashRecord> crashes, int total)
    {
        t.AppendLine();
        t.AppendLine("STABILITY");
        if (crashes.Count == 0)
        {
            t.AppendLine("  No crashes, hangs, or restarts since the last report.");
            return;
        }
        t.AppendLine($"  {crashes.Count} issue(s) since the last report (restarted automatically):");
        foreach (var c in crashes)
            t.AppendLine($"  {c.Time:yyyy-MM-dd HH:mm}  {KindLabel(c.Kind),-16} {Trunc(c.Summary, 120)}");
        if (total > crashes.Count)
            t.AppendLine($"  ({total - crashes.Count} more in the next report)");
        foreach (var c in crashes.Take(CrashDetailLimit))
        {
            t.AppendLine();
            t.AppendLine($"--- #{c.Id} {c.Time:yyyy-MM-dd HH:mm:ss} {KindLabel(c.Kind)} (pid {c.Pid?.ToString() ?? "?"}, version {c.AppVersion ?? "?"}) ---");
            t.AppendLine(c.Summary);
            if (c.DumpPath != null)
                t.AppendLine($"Dump: {c.DumpPath}");
            t.AppendLine(Trunc(c.Details, CrashDetailChars));
        }
    }

    private static string KindLabel(string kind) => kind switch
    {
        "exception" => "Crash",
        "exit" => "Unexpected exit",
        "hang" => "Hang",
        "supervisor-exception" => "Watchdog error",
        "not-running" => "Not running",
        _ => kind,
    };

    // =====================================================================================
    // Email-safe building blocks
    // =====================================================================================

    private static void Heading(StringBuilder h, string text) =>
        h.Append($"<div style=\"font-size:17px;font-weight:700;margin:26px 0 10px;padding-top:14px;border-top:1px solid {Rule}\">{E(text)}</div>");

    private static void Empty(StringBuilder h, string text) =>
        h.Append($"<div style=\"color:{Muted};font-size:13px;font-style:italic\">{E(text)}</div>");

    /// <summary>A two-cell row: wrapping text on the left, a time that never wraps on the right.</summary>
    private static void Row(StringBuilder h, string leftHtml, string rightHtml, bool small = false, bool indent = false, bool rightBold = false)
    {
        string size = small ? "font-size:13px;" : "";
        h.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"border-collapse:collapse\"><tr>");
        h.Append($"<td style=\"{size}padding:2px 8px 2px {(indent ? "12px" : "0")};vertical-align:top;word-break:break-word;overflow-wrap:anywhere;{(small ? $"color:#3d444d;" : "")}\">{leftHtml}</td>");
        h.Append($"<td align=\"right\" style=\"{size}padding:2px 0;vertical-align:top;white-space:nowrap;{(rightBold ? "font-weight:600;" : $"color:{Muted};")}\">{rightHtml}</td>");
        h.Append("</tr></table>");
    }

    /// <summary>A full-width horizontal bar of proportional segments (fractions of 1).</summary>
    private static void Bar(StringBuilder h, int heightPx, params (double fraction, string color)[] parts)
    {
        var visible = parts.Where(p => p.fraction > 0.0005).ToList();
        if (visible.Count == 0)
            visible.Add((1, Track));
        double sum = visible.Sum(p => p.fraction);
        h.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"border-collapse:collapse;table-layout:fixed;" +
                 $"border-radius:{heightPx / 2}px;overflow:hidden;margin-top:4px\"><tr>");
        foreach (var (fraction, color) in visible)
            h.Append($"<td style=\"width:{Pct(fraction / sum)};height:{heightPx}px;background:{color};font-size:0;line-height:0\">&#8203;</td>");
        h.Append("</tr></table>");
    }

    private static string Share(double fraction) => fraction > 0 && fraction < 0.005 ? "<1%" : $"{fraction * 100:0}%";

    private static string Pct(double fraction) =>
        (fraction * 100).ToString("0.###", CultureInfo.InvariantCulture) + "%";

    /// <summary>A link for http(s) URLs only; anything else is shown as plain text.</summary>
    private static string Link(string url, string text) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? $"<a class=\"lnk\" href=\"{E(url)}\" style=\"color:{LinkColor};text-decoration:none\">{E(text)}</a>"
            : E(text);

    private static string Fmt(int seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string E(string s) => WebUtility.HtmlEncode(s);
}
