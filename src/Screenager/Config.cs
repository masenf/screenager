using System.Globalization;

namespace Screenager;

/// <summary>
/// Minimal INI-style configuration. Sections in [brackets], key = value lines,
/// and ';' or '#' starting a comment. No external dependency.
/// </summary>
public sealed class Config
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections =
        new(StringComparer.OrdinalIgnoreCase);

    // ----- [limits] -----
    public int DailyMinutes => GetInt("limits", "daily_minutes", 120);
    public int ResetHour => Math.Clamp(GetInt("limits", "reset_hour", 4), 0, 23);
    public int IdleThresholdSeconds => Math.Max(5, GetInt("limits", "idle_threshold_seconds", 60));
    public int WarnSeconds => Math.Max(5, GetInt("limits", "warn_seconds", 30));
    // Treat active audio output (e.g. a video playing) as activity, so it doesn't idle-pause.
    public bool AudioCountsAsActivity => GetBool("limits", "audio_counts_as_activity", true);
    public TimeSpan? BedtimeStart => GetTime("limits", "bedtime_start");
    public TimeSpan? BedtimeEnd => GetTime("limits", "bedtime_end");

    // ----- [override] -----
    public string OverridePin => Get("override", "pin", "").Trim();
    public string OverrideHotkey => Get("override", "hotkey", "Ctrl+Alt+Shift+S");
    // How long opening the override dialog suspends locking (capped so it can't be left open to dodge a lock).
    public int OverrideGraceSeconds => Math.Max(5, GetInt("override", "grace_seconds", 30));

    // ----- [display] -----
    // Count up (elapsed used time) instead of down (remaining time).
    public bool CountUp => GetBool("display", "count_up", false);

    // ----- [startup] -----
    public bool StartupHidden => GetBool("startup", "hidden", true);

    // ----- [report] -----
    public bool ReportEnabled => GetBool("report", "enabled", false);
    public int ReportSendHour => Math.Clamp(GetInt("report", "send_hour", 20), 0, 23);
    public bool CollectBrowserHistory => GetBool("report", "collect_browser_history", true);

    // ----- [mailgun] -----
    public string MailgunApiBase => Get("mailgun", "api_base", "https://api.mailgun.net").TrimEnd('/');
    public string MailgunDomain => Get("mailgun", "domain", "").Trim();
    public string MailgunApiKey => Get("mailgun", "api_key", "").Trim();
    public string MailFrom => Get("mailgun", "from", "").Trim();
    public string MailTo => Get("mailgun", "to", "").Trim();

    public bool MailgunConfigured =>
        MailgunDomain.Length > 0 && MailgunApiKey.Length > 0 &&
        MailFrom.Length > 0 && MailTo.Length > 0;

    public static Config Load(string path)
    {
        var cfg = new Config();
        if (!File.Exists(path))
            return cfg;

        string? section = null;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1].Trim();
                if (!cfg._sections.ContainsKey(section))
                    cfg._sections[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0 || section is null)
                continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            // Strip a trailing inline comment (only when preceded by whitespace, so URLs/#fragments survive).
            value = StripInlineComment(value);

            cfg._sections[section][key] = value;
        }

        return cfg;
    }

    private static string StripInlineComment(string value)
    {
        for (int i = 1; i < value.Length; i++)
        {
            if ((value[i] == ';' || value[i] == '#') && char.IsWhiteSpace(value[i - 1]))
                return value[..i].TrimEnd();
        }
        return value;
    }

    private string Get(string section, string key, string fallback)
        => _sections.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : fallback;

    private int GetInt(string section, string key, int fallback)
        => int.TryParse(Get(section, key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private bool GetBool(string section, string key, bool fallback)
    {
        var v = Get(section, key, "").Trim().ToLowerInvariant();
        return v switch
        {
            "true" or "yes" or "1" or "on" => true,
            "false" or "no" or "0" or "off" => false,
            _ => fallback,
        };
    }

    /// <summary>Parses "HH:mm" (or "H"). Returns null when blank/invalid (feature disabled).</summary>
    private TimeSpan? GetTime(string section, string key)
    {
        var v = Get(section, key, "").Trim();
        if (v.Length == 0)
            return null;
        if (TimeSpan.TryParseExact(v, new[] { @"hh\:mm", @"h\:mm" }, CultureInfo.InvariantCulture, out var ts))
            return ts;
        if (int.TryParse(v, out var hour) && hour is >= 0 and <= 23)
            return TimeSpan.FromHours(hour);
        return null;
    }
}
