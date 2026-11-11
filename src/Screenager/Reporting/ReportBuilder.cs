using System.Net;
using System.Text;
using Screenager.Data;

namespace Screenager.Reporting;

/// <summary>
/// Assembles the daily HTML/text report from persisted usage, focus time, and browser history.
/// </summary>
public static class ReportBuilder
{
    private static readonly string[] VideoHosts =
    {
        "youtube.com/watch", "youtu.be/", "youtube.com/shorts", "vimeo.com/",
        "twitch.tv/", "netflix.com/watch", "tiktok.com/", "dailymotion.com/video",
        "hulu.com/watch", "disneyplus.com/video",
    };

    private const int CrashDetailLimit = 10;    // incidents shown with full details
    private const int CrashDetailChars = 8_000; // per incident

    public static (string subject, string html, string text) Build(
        Database db, Config cfg, string day, string machine,
        IReadOnlyList<CrashRecord>? crashes = null, int totalUnreportedCrashes = 0)
    {
        crashes ??= Array.Empty<CrashRecord>();
        var usage = db.GetUsage(day);
        var focus = db.GetFocus(day, 25);
        var visits = db.GetBrowserVisits(day, 500);

        var videos = visits.Where(v => VideoHosts.Any(h =>
            v.Url.Contains(h, StringComparison.OrdinalIgnoreCase))).Take(50).ToList();

        int budget = cfg.DailyMinutes * 60 + usage.BonusSeconds;
        string subject = $"Screenager report — {day} ({Fmt(usage.ActiveSeconds)} active)";
        if (crashes.Count > 0)
            subject += $" — {crashes.Count} stability issue{(crashes.Count == 1 ? "" : "s")}";

        // ---------- HTML ----------
        var h = new StringBuilder();
        h.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;max-width:720px\">");
        h.Append($"<h2>Screenager daily report</h2><p><b>Date:</b> {E(day)} &nbsp; <b>PC:</b> {E(machine)}</p>");

        h.Append("<h3>Screen time</h3><ul>");
        h.Append($"<li>Active time used: <b>{Fmt(usage.ActiveSeconds)}</b></li>");
        h.Append($"<li>Daily limit: {Fmt(cfg.DailyMinutes * 60)}{(usage.BonusSeconds > 0 ? $" (+{Fmt(usage.BonusSeconds)} bonus)" : "")}</li>");
        h.Append($"<li>Total budget: {Fmt(budget)} &nbsp;—&nbsp; {(usage.ActiveSeconds >= budget ? "limit reached" : Fmt(Math.Max(0, budget - usage.ActiveSeconds)) + " remaining")}</li>");
        h.Append("</ul>");

        h.Append("<h3>Most-used windows</h3>");
        if (focus.Count == 0) h.Append("<p><i>No focus data.</i></p>");
        else
        {
            h.Append("<table cellpadding=\"4\" style=\"border-collapse:collapse\"><tr><th align=\"left\">App</th><th align=\"left\">Window</th><th align=\"right\">Time</th></tr>");
            foreach (var f in focus)
                h.Append($"<tr><td>{E(f.Process)}</td><td>{E(Trunc(f.Title, 70))}</td><td align=\"right\">{Fmt(f.Seconds)}</td></tr>");
            h.Append("</table>");
        }

        h.Append("<h3>Videos watched</h3>");
        if (videos.Count == 0) h.Append("<p><i>None detected.</i></p>");
        else
        {
            h.Append("<ul>");
            foreach (var v in videos)
                h.Append($"<li>{E(Trunc(v.Title.Length > 0 ? v.Title : v.Url, 90))} <span style=\"color:#888\">({E(v.Browser)}, {v.LastVisit:HH:mm})</span></li>");
            h.Append("</ul>");
        }

        h.Append($"<h3>Sites visited ({visits.Count})</h3>");
        if (visits.Count == 0) h.Append("<p><i>No browser history collected.</i></p>");
        else
        {
            h.Append("<table cellpadding=\"4\" style=\"border-collapse:collapse\"><tr><th align=\"left\">Time</th><th align=\"left\">Site</th><th align=\"left\">Title</th></tr>");
            foreach (var v in visits.Take(150))
                h.Append($"<tr><td>{v.LastVisit:HH:mm}</td><td>{E(Host(v.Url))}</td><td>{E(Trunc(v.Title, 70))}</td></tr>");
            h.Append("</table>");
        }
        AppendStabilityHtml(h, crashes, totalUnreportedCrashes);
        h.Append("</div>");

        // ---------- Plain text ----------
        var t = new StringBuilder();
        t.AppendLine($"Screenager daily report — {day}  (PC: {machine})");
        t.AppendLine();
        t.AppendLine($"Active time used: {Fmt(usage.ActiveSeconds)}");
        t.AppendLine($"Budget: {Fmt(budget)}");
        t.AppendLine();
        t.AppendLine("Most-used windows:");
        foreach (var f in focus)
            t.AppendLine($"  {Fmt(f.Seconds),8}  {f.Process} — {Trunc(f.Title, 60)}");
        t.AppendLine();
        t.AppendLine("Videos watched:");
        foreach (var v in videos)
            t.AppendLine($"  {v.LastVisit:HH:mm}  {Trunc(v.Title.Length > 0 ? v.Title : v.Url, 80)}");
        t.AppendLine();
        t.AppendLine($"Sites visited ({visits.Count}):");
        foreach (var v in visits.Take(150))
            t.AppendLine($"  {v.LastVisit:HH:mm}  {Host(v.Url)}  {Trunc(v.Title, 60)}");
        AppendStabilityText(t, crashes, totalUnreportedCrashes);

        return (subject, h.ToString(), t.ToString());
    }

    // ---------- Stability (crashes, hangs, restarts since the last report) ----------
    private static void AppendStabilityHtml(StringBuilder h, IReadOnlyList<CrashRecord> crashes, int total)
    {
        h.Append("<h3>Stability</h3>");
        if (crashes.Count == 0)
        {
            h.Append("<p><i>No crashes, hangs, or restarts since the last report.</i></p>");
            return;
        }

        h.Append($"<p style=\"color:#b00020\"><b>{crashes.Count} issue{(crashes.Count == 1 ? "" : "s")} since the last report.</b> "
               + "Screenager restarted itself automatically; details below are for fixing the cause.</p>");
        h.Append("<table cellpadding=\"4\" style=\"border-collapse:collapse\"><tr><th align=\"left\">When</th><th align=\"left\">What</th>"
               + "<th align=\"left\">Summary</th><th align=\"right\">Uptime</th></tr>");
        foreach (var c in crashes)
            h.Append($"<tr><td style=\"white-space:nowrap\">{c.Time:yyyy-MM-dd HH:mm}</td><td>{E(KindLabel(c.Kind))}</td>"
                   + $"<td>{E(Trunc(c.Summary, 160))}</td><td align=\"right\">{(c.UptimeSeconds is { } u ? Fmt((int)Math.Min(u, int.MaxValue)) : "")}</td></tr>");
        h.Append("</table>");
        if (total > crashes.Count)
            h.Append($"<p><i>{total - crashes.Count} more will be included in the next report.</i></p>");

        foreach (var c in crashes.Take(CrashDetailLimit))
        {
            h.Append($"<h4 style=\"margin-bottom:4px\">#{c.Id} · {c.Time:yyyy-MM-dd HH:mm:ss} · {E(KindLabel(c.Kind))}</h4>");
            h.Append($"<p style=\"margin:0 0 4px 0\">{E(c.Summary)}<br><span style=\"color:#666\">pid {c.Pid?.ToString() ?? "?"} · version {E(c.AppVersion ?? "?")}"
                   + (c.DumpPath != null ? $" · dump {E(c.DumpPath)}" : "") + "</span></p>");
            h.Append("<pre style=\"white-space:pre-wrap;font-size:11px;background:#f6f6f6;padding:6px;border:1px solid #ddd\">");
            h.Append(E(Trunc(c.Details, CrashDetailChars)));
            h.Append("</pre>");
        }
        if (crashes.Count > CrashDetailLimit)
            h.Append($"<p><i>Details omitted for {crashes.Count - CrashDetailLimit} further issue(s); they are in the crashes table of screenager.db.</i></p>");
    }

    private static void AppendStabilityText(StringBuilder t, IReadOnlyList<CrashRecord> crashes, int total)
    {
        t.AppendLine();
        t.AppendLine("Stability:");
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

    private static string Fmt(int seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }

    private static string Host(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : Trunc(url, 40);
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string E(string s) => WebUtility.HtmlEncode(s);
}
