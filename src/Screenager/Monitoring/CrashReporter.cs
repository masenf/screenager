using System.Diagnostics;
using System.Text;
using Screenager.Data;

namespace Screenager.Monitoring;

/// <summary>
/// In-process last-chance handler: records the managed exception (full stack, inner exceptions,
/// thread, resource usage) to the crashes table just before the process dies. The supervisor
/// later attaches the exit code, console output and event-log entries to the same record.
/// </summary>
public static class CrashReporter
{
    private static string _dbPath = "";
    private static string _kind = "exception";
    private static string _role = "worker";
    private static int _recorded;

    /// <param name="role">"worker" (the timer app) or "supervisor" (the watchdog).</param>
    public static void Install(string dbPath, string role)
    {
        _dbPath = dbPath;
        _role = role;
        _kind = role == "supervisor" ? "supervisor-exception" : "exception";
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (Interlocked.Exchange(ref _recorded, 1) != 0)
            return;
        try
        {
            var ex = e.ExceptionObject as Exception;
            var thread = Thread.CurrentThread;
            using var self = Process.GetCurrentProcess();
            var uptime = DateTime.Now - self.StartTime;

            var sb = new StringBuilder();
            sb.AppendLine($"Unhandled exception in the {_role} process (pid {Environment.ProcessId}), terminating={e.IsTerminating}");
            sb.AppendLine($"Thread: '{thread.Name ?? "(unnamed)"}', managed id {thread.ManagedThreadId}, " +
                          $"{(thread.IsThreadPoolThread ? "thread pool" : "dedicated")}, apartment {thread.GetApartmentState()}");
            sb.AppendLine($"Version: {Diagnostics.AppVersion}");
            sb.AppendLine($"Uptime: {Diagnostics.FormatDuration(uptime)} (started {self.StartTime:yyyy-MM-dd HH:mm:ss})");
            sb.AppendLine($"Resources: {Diagnostics.Sample(self)}");
            sb.AppendLine($"Environment: {Diagnostics.EnvironmentLine()}");
            sb.AppendLine();
            sb.AppendLine("Exception:");
            sb.AppendLine(ex?.ToString() ?? e.ExceptionObject?.ToString() ?? "(null)");
            if (ex?.Data is { Count: > 0 } data)
            {
                sb.AppendLine("Exception.Data:");
                foreach (System.Collections.DictionaryEntry kv in data)
                    sb.AppendLine($"  {kv.Key} = {kv.Value}");
            }

            using var db = new Database(_dbPath);
            db.InsertCrash(new CrashRecord
            {
                Time = DateTime.Now,
                Kind = _kind,
                Pid = Environment.ProcessId,
                AppVersion = Diagnostics.AppVersion,
                UptimeSeconds = (long)uptime.TotalSeconds,
                Summary = Summarize(ex, e.ExceptionObject),
                Details = sb.ToString(),
            });
        }
        catch
        {
            // Never throw from the last-chance handler; the supervisor still records the exit.
        }
    }

    private static string Summarize(Exception? ex, object? raw)
    {
        if (ex == null)
            return $"Unhandled non-exception object: {raw}";
        var root = ex.GetBaseException();
        string s = $"{ex.GetType().Name}: {Diagnostics.Truncate(ex.Message, 200)}";
        if (!ReferenceEquals(root, ex))
            s += $" → {root.GetType().Name}: {Diagnostics.Truncate(root.Message, 200)}";
        var site = root.TargetSite ?? ex.TargetSite;
        if (site != null)
            s += $" (in {site.DeclaringType?.Name}.{site.Name})";
        return s;
    }
}
