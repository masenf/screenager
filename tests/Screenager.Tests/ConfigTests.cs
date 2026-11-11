namespace Screenager.Tests;

public class ConfigTests : IDisposable
{
    private readonly TempEnv _env = new();
    public void Dispose() => _env.Dispose();

    [Fact]
    public void MissingFile_UsesSafeDefaults()
    {
        var cfg = Config.Load(Path.Combine(_env.Dir, "does-not-exist.cfg"));
        Assert.Equal(120, cfg.DailyMinutes);
        Assert.Equal(4, cfg.ResetHour);
        Assert.Equal(60, cfg.IdleThresholdSeconds);
        Assert.True(cfg.AudioCountsAsActivity);
        Assert.Null(cfg.BedtimeStart);
        Assert.False(cfg.ReportEnabled);
        Assert.False(cfg.MailgunConfigured);
        Assert.Equal("https://api.mailgun.net", cfg.MailgunApiBase);
    }

    [Fact]
    public void ParsesSectionsAndStripsInlineComments()
    {
        var cfg = _env.Config("""
            ; full-line comment
            [limits]
            daily_minutes = 90          ; ninety minutes
            bedtime_start = 21:00       # hash comments too
            bedtime_end   = 6
            [override]
            pin = 12#34
            [mailgun]
            api_base = https://api.eu.mailgun.net/   ; EU region
            """);
        Assert.Equal(90, cfg.DailyMinutes);
        Assert.Equal(TimeSpan.FromHours(21), cfg.BedtimeStart);
        Assert.Equal(TimeSpan.FromHours(6), cfg.BedtimeEnd);
        Assert.Equal("12#34", cfg.OverridePin); // '#' not preceded by whitespace is data
        Assert.Equal("https://api.eu.mailgun.net", cfg.MailgunApiBase); // trailing slash trimmed
    }

    [Fact]
    public void ClampsOutOfRangeValues()
    {
        var cfg = _env.Config("""
            [limits]
            reset_hour = 30
            idle_threshold_seconds = 1
            warn_seconds = 0
            [report]
            send_hour = -2
            """);
        Assert.Equal(23, cfg.ResetHour);
        Assert.Equal(5, cfg.IdleThresholdSeconds);
        Assert.Equal(5, cfg.WarnSeconds);
        Assert.Equal(0, cfg.ReportSendHour);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("YES", true)]
    [InlineData("on", true)]
    [InlineData("1", true)]
    [InlineData("off", false)]
    [InlineData("0", false)]
    [InlineData("maybe", false)] // unrecognised → default (report.enabled defaults to false)
    public void ParsesBooleans(string value, bool expected)
    {
        var cfg = _env.Config($"[report]\nenabled = {value}\n");
        Assert.Equal(expected, cfg.ReportEnabled);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("25:00", null)]
    [InlineData("nonsense", null)]
    [InlineData("7:30", "07:30")]
    [InlineData("07:30", "07:30")]
    public void BedtimeParsingDisablesOnBlankOrInvalid(string value, string? expected)
    {
        var cfg = _env.Config($"[limits]\nbedtime_start = {value}\n");
        Assert.Equal(expected == null ? null : TimeSpan.Parse(expected), cfg.BedtimeStart);
    }

    [Fact]
    public void MailgunConfiguredNeedsAllFourSettings()
    {
        var partial = _env.Config("[mailgun]\ndomain = mg.example.com\napi_key = k\nfrom = a@b\n");
        Assert.False(partial.MailgunConfigured);
        var full = _env.Config("[mailgun]\ndomain = mg.example.com\napi_key = k\nfrom = a@b\nto = c@d\n");
        Assert.True(full.MailgunConfigured);
    }
}
