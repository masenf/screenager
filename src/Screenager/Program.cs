using Screenager;
using Screenager.Reporting;
using Screenager.Startup;
using Screenager.Tracking;

internal static class Program
{
    private const string MutexName = "Screenager.SingleInstance.v1";

    [STAThread]
    private static int Main(string[] args)
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath!) ?? AppContext.BaseDirectory;
        var configPath = Path.Combine(exeDir, "screenager.cfg");

        var verb = args.Length > 0 ? args[0].TrimStart('-').ToLowerInvariant() : "run";

        switch (verb)
        {
            case "install":
                if (StartupManager.EnsureElevated(args)) return 0;
                StartupManager.Install();
                Info("Screenager installed: it will start hidden at logon, and a Defender exclusion was added.");
                return 0;

            case "uninstall":
                if (StartupManager.EnsureElevated(args)) return 0;
                StartupManager.Uninstall();
                Info("Screenager uninstalled (logon task + Defender exclusion removed).");
                return 0;

            case "report-now":
                return ReportNow(configPath);

            case "test-email":
                return TestEmail(configPath);

            case "run":
                return Run(configPath);

            default:
                Info($"Unknown option '{args[0]}'.\n\nUsage:\n  screenager            run the limiter\n  screenager --install      register hidden logon task (elevated)\n  screenager --uninstall    remove it (elevated)\n  screenager --test-email   send a quick test email to verify SMTP/Mailgun config\n  screenager --report-now   build and send the full daily report now");
                return 1;
        }
    }

    private static int Run(string configPath)
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
            return 0; // already running

        ApplicationConfiguration.Initialize();

        var cfg = Config.Load(configPath);
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "screenager", "screenager.db");

        using var context = new ScreenagerContext(cfg, dbPath);
        Application.Run(context);
        return 0;
    }

    private static int ReportNow(string configPath)
    {
        var cfg = Config.Load(configPath);
        if (!cfg.MailgunConfigured)
        {
            Info("Mailgun is not fully configured in screenager.cfg (need domain, api_key, from, to).");
            return 1;
        }

        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "screenager", "screenager.db");

        using var db = new Screenager.Data.Database(dbPath);
        var clock = new LogicalClock(cfg.ResetHour, cfg.BedtimeStart, cfg.BedtimeEnd);
        var service = new ReportService(db, cfg, clock);

        var (ok, message) = service.SendTodayAsync().GetAwaiter().GetResult();
        Info(ok ? "Report sent." : $"Report failed: {message}");
        return ok ? 0 : 2;
    }

    private static int TestEmail(string configPath)
    {
        var cfg = Config.Load(configPath);
        if (!cfg.MailgunConfigured)
        {
            Info("Mailgun is not fully configured in screenager.cfg (need domain, api_key, from, to).");
            return 1;
        }

        var now = DateTime.Now;
        const string subject = "Screenager test email";
        string text = $"Screenager test email from {Environment.MachineName} at {now}.\n\n"
                    + "If you received this, your [mailgun] settings are correct.";
        string html = $"<div style=\"font-family:Segoe UI,Arial,sans-serif\">"
                    + $"<h3>Screenager test email</h3>"
                    + $"<p>Sent from <b>{Environment.MachineName}</b> at {now}.</p>"
                    + $"<p>If you received this, your <code>[mailgun]</code> settings are correct.</p></div>";

        var (ok, message) = Mailer.SendAsync(cfg, subject, html, text).GetAwaiter().GetResult();
        Info(ok ? "Test email sent successfully." : $"Test email failed: {message}");
        return ok ? 0 : 2;
    }

    /// <summary>User feedback for CLI verbs (this is a windowed app, so use a message box).</summary>
    private static void Info(string message) =>
        MessageBox.Show(message, "Screenager", MessageBoxButtons.OK, MessageBoxIcon.Information);
}

/// <summary>
/// Keeps the message loop alive for the lifetime of the app (no MainForm; the only windows are
/// the always-on-top timer and transient warnings).
/// </summary>
internal sealed class ScreenagerContext : ApplicationContext
{
    private readonly AppController _controller;

    public ScreenagerContext(Config cfg, string dbPath)
    {
        _controller = new AppController(cfg, dbPath);
        _controller.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _controller.Dispose();
        base.Dispose(disposing);
    }
}
