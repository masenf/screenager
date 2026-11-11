using System.Text.RegularExpressions;
using Screenager.Data;

namespace Screenager.Reporting;

public sealed record WindowUsage(string Title, int Seconds);

/// <summary>Foreground time for one app, with its busiest windows.</summary>
public sealed record AppUsage(string Name, int Seconds, IReadOnlyList<WindowUsage> Windows)
{
    /// <summary>A breakdown adds nothing when the only window is just the app's own name.</summary>
    public bool ShowWindows => Windows.Count > 1 ||
        (Windows.Count == 1 && !Windows[0].Title.Equals(Name, StringComparison.OrdinalIgnoreCase) && Windows[0].Title != NoTitle);

    public const string NoTitle = "(no window title)";
}

/// <summary>
/// Turns raw (process, window title) focus rows into per-app groups with readable names and
/// de-noised titles, so e.g. eight "chrome" rows become one "Google Chrome" entry.
/// </summary>
public static partial class FocusGrouping
{
    private const int MinAppSecondsToList = 60;  // smaller apps are folded into "Other apps"…
    private const int AlwaysListTopApps = 6;     // …unless they are among the top few

    public static List<AppUsage> Group(IEnumerable<FocusRow> rows)
    {
        var apps = rows
            .Where(r => r.Seconds > 0)
            // UWP apps all run under ApplicationFrameHost; the window title is the real app name.
            .GroupBy(r => IsFrameHost(r.Process) ? "afh:" + r.Title : r.Process.ToLowerInvariant())
            .Select(g =>
            {
                var windows = g
                    .GroupBy(r => CleanTitle(r.Title))
                    .Select(w => new WindowUsage(w.Key, w.Sum(r => r.Seconds)))
                    .OrderByDescending(w => w.Seconds)
                    .ToList();
                var first = g.First();
                string name = IsFrameHost(first.Process)
                    ? (first.Title.Length > 0 ? first.Title : "Windows app")
                    : FriendlyName(first.Process, windows);
                // "Minecraft" with a single "Minecraft Launcher" window reads better as just that.
                if (windows.Count == 1 && windows[0].Title.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                    name = windows[0].Title;
                return new AppUsage(name, windows.Sum(w => w.Seconds), windows);
            })
            .OrderByDescending(a => a.Seconds)
            .ToList();

        var listed = apps.Where((a, i) => i < AlwaysListTopApps || a.Seconds >= MinAppSecondsToList).ToList();
        var rest = apps.Skip(listed.Count).ToList();
        if (rest.Count > 1)
        {
            listed.Add(new AppUsage($"Other apps ({rest.Count})", rest.Sum(a => a.Seconds),
                rest.Select(a => new WindowUsage(a.Name, a.Seconds)).ToList()));
        }
        else
        {
            listed.AddRange(rest);
        }
        return listed;
    }

    private static bool IsFrameHost(string process) =>
        process.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase);

    private static string FriendlyName(string process, IReadOnlyList<WindowUsage> windows) => process.ToLowerInvariant() switch
    {
        "chrome" => "Google Chrome",
        "msedge" => "Microsoft Edge",
        "firefox" => "Firefox",
        "brave" => "Brave",
        "opera" or "opera_gx" => "Opera",
        "explorer" => "File Explorer",
        "gamingservicesui" => "Xbox Gaming Services",
        "javaw" or "java" => windows.Any(w => w.Title.StartsWith("Minecraft", StringComparison.OrdinalIgnoreCase))
            ? "Minecraft (Java)" : "Java",
        _ => process,
    };

    /// <summary>
    /// Strips browser chrome from window titles: the "- Google Chrome" / "- Profile - Microsoft Edge"
    /// suffix, Edge's "and 14 more pages" tab-group count, and the "(1) " unread-notification
    /// prefix YouTube and others add — so the same page doesn't appear as several rows.
    /// </summary>
    public static string CleanTitle(string title)
    {
        var t = title.Replace("​", "").Trim();
        t = BrowserSuffix().Replace(t, "");
        t = MorePages().Replace(t, "");
        t = NotificationCount().Replace(t, "");
        t = t.Trim();
        return t.Length == 0 ? AppUsage.NoTitle : t;
    }

    /// <summary>Page title without the "(1) " unread-notification prefix.</summary>
    public static string StripNotificationCount(string title) => NotificationCount().Replace(title.Trim(), "").Trim();

    /// <summary>Video/page title without the "(1) " prefix and the " - YouTube" style site suffix.</summary>
    public static string CleanPageTitle(string title)
    {
        var t = NotificationCount().Replace(title.Trim(), "");
        t = SiteSuffix().Replace(t, "");
        return t.Trim();
    }

    [GeneratedRegex(@"\s+[-–—]\s+(?:Google Chrome|Mozilla Firefox|Brave|Opera|(?:[^-–—]{1,40}\s+[-–—]\s+)?Microsoft\s*Edge)$", RegexOptions.IgnoreCase)]
    private static partial Regex BrowserSuffix();

    [GeneratedRegex(@"\s+and\s+\d+\s+more\s+pages?$", RegexOptions.IgnoreCase)]
    private static partial Regex MorePages();

    [GeneratedRegex(@"^\(\d+\+?\)\s+")]
    private static partial Regex NotificationCount();

    [GeneratedRegex(@"\s+[-–—|]\s+(?:YouTube|Vimeo|Twitch|Netflix|TikTok|Dailymotion|Hulu|Disney\+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SiteSuffix();
}
