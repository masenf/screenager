using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Win32;
using Screenager.Data;

namespace Screenager.Monitoring;

/// <summary>
/// Watchdog process. <c>screenager.exe</c> (no arguments) runs this; it launches the timer app as
/// a child (<c>screenager.exe --worker</c>) and:
/// <list type="bullet">
/// <item>restarts it whenever it exits outside logoff/shutdown, with a short backoff;</item>
/// <item>kills and restarts it if its UI thread stops pumping messages (a hang), after writing a minidump;</item>
/// <item>records every incident in the <c>crashes</c> table with the exit code, recent console
///   output (the runtime prints unhandled exceptions to stderr), Windows event-log entries,
///   resource usage, and any crash dump, so the daily report can email it.</item>
/// </list>
/// It also keeps a heartbeat so that, if the watchdog itself is killed, the next start (the
/// scheduled task re-checks every 10 minutes) records how long nothing was supervising.
/// </summary>
public sealed class Supervisor : ApplicationContext
{
    public static readonly string WorkerMutexName = "Screenager.SingleInstance.v1" + Diagnostics.InstanceSuffix;
    public static readonly string SupervisorMutexName = "Screenager.Supervisor.v1" + Diagnostics.InstanceSuffix;
    public const int ExitAlreadyRunning = 3;
    public const string WorkerPidKey = "worker_pid";
    private const string HeartbeatKey = "supervisor_heartbeat";

    private const int TickMs = 2_000;              // exit polling / restart scheduling
    private const int SlowTickEvery = 15;          // every 30s: resource sample, hang probe, heartbeat
    private const int HangStrikesBeforeKill = 4;   // 4 failed probes 30s apart ≈ 1.5–2 min unresponsive
    private static readonly TimeSpan HealthyUptime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SessionEndingGrace = TimeSpan.FromSeconds(90);

    private readonly Database _db;
    private readonly string _exe;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly OutputTail _output = new(capacity: 300);
    private readonly DateTime _bootUtc = BootTimeUtc();
    private int _tickCount;

    private Process? _worker;
    private bool _workerOwned;          // started by us (so we have its console output)
    private DateTime _workerStart;
    private ResourceSample? _firstSample, _lastSample;
    private int _hangStrikes;
    private DateTime _hangSince;
    private TimeSpan _hangCpuStart;
    private bool _killedForHang;

    private DateTime? _exitSeenAt;      // worker exit observed; decide once we know it's not a logoff
    private DateTime? _restartAt;
    private bool _waitingForForeignWorker; // a worker we can't identify holds the mutex
    private int _quickFailures;

    private DateTime? _sessionEndingAt;
    private bool _sessionEnded;

    public Supervisor(string dbPath)
    {
        _db = new Database(dbPath);
        _exe = Environment.ProcessPath!;

        SystemEvents.SessionEnding += OnSessionEnding;
        SystemEvents.SessionEnded += OnSessionEnded;

        RecordIfPreviousSupervisorDied();
        WriteHeartbeat();

        if (!TryAdoptRunningWorker())
            StartWorker();

        _timer = new System.Windows.Forms.Timer { Interval = TickMs };
        _timer.Tick += (_, _) => SafeTick();
        _timer.Start();
    }

    // ------------------------------------------------------------------ lifecycle

    private void StartWorker()
    {
        _waitingForForeignWorker = false;
        ResetWorkerState();

        var psi = new ProcessStartInfo(_exe, "--worker")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_exe)!,
        };
        var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) _output.Add("out", e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) _output.Add("err", e.Data); };
        try
        {
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _worker = p;
            _workerOwned = true;
            _workerStart = DateTime.Now;
        }
        catch (Exception ex)
        {
            p.Dispose();
            _worker = null;
            Insert("exit", null, null, null,
                $"Failed to start the timer app: {ex.Message}",
                $"Could not launch \"{_exe}\" --worker:\n{ex}", null);
            _restartAt = DateTime.Now.AddSeconds(60);
        }
    }

    /// <summary>
    /// A worker already holds the single-instance mutex (e.g. the previous watchdog was killed but
    /// its child kept running, or an older version is running). Adopt it if we can identify it,
    /// otherwise wait for it to exit before starting our own.
    /// </summary>
    private bool TryAdoptRunningWorker()
    {
        if (!Mutex.TryOpenExisting(WorkerMutexName, out var m))
            return false;
        m.Dispose();

        ResetWorkerState();
        var meta = _db.GetMeta(WorkerPidKey)?.Split('|');
        if (meta is { Length: 2 } && int.TryParse(meta[0], out int pid) &&
            long.TryParse(meta[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long startTicks))
        {
            try
            {
                var p = Process.GetProcessById(pid);
                if (Math.Abs((p.StartTime.ToUniversalTime() - new DateTime(startTicks, DateTimeKind.Utc)).TotalSeconds) < 2)
                {
                    _ = p.Handle; // hold a handle so the exit code is still readable after it dies
                    _worker = p;
                    _workerOwned = false;
                    _workerStart = p.StartTime;
                    _waitingForForeignWorker = false;
                    return true;
                }
                p.Dispose();
            }
            catch { /* not running / no access */ }
        }

        _worker = null;
        _waitingForForeignWorker = true;
        return true;
    }

    private void ResetWorkerState()
    {
        _restartAt = null;
        _exitSeenAt = null;
        _worker?.Dispose();
        _worker = null;
        _output.Clear();
        _firstSample = _lastSample = null;
        _hangStrikes = 0;
        _killedForHang = false;
    }

    // ------------------------------------------------------------------ periodic work

    private void SafeTick()
    {
        try { Tick(); }
        catch (Exception ex)
        {
            // Keep supervising even if a diagnostic step misbehaves.
            try
            {
                Insert("supervisor-exception", Environment.ProcessId, null, null,
                    $"Watchdog tick error: {ex.GetType().Name}: {ex.Message}", ex.ToString(), null);
            }
            catch { /* ignore */ }
        }
    }

    private void Tick()
    {
        _tickCount++;
        bool slow = _tickCount % SlowTickEvery == 0;
        if (slow)
            WriteHeartbeat();

        if (_sessionEnded)
            return;

        if (_waitingForForeignWorker)
        {
            if (!Mutex.TryOpenExisting(WorkerMutexName, out var m))
                StartWorker();
            else
                m.Dispose();
            return;
        }

        if (_restartAt is { } at)
        {
            if (DateTime.Now >= at && !LogoffInProgress())
            {
                if (!TryAdoptRunningWorker())
                    StartWorker();
            }
            return;
        }

        if (_worker == null)
            return;

        if (_exitSeenAt == null)
        {
            if (!_worker.HasExited)
            {
                if (slow)
                    MonitorRunningWorker(_worker);
                return;
            }
            // Give a logoff/shutdown a moment to announce itself before treating this as a crash.
            _exitSeenAt = DateTime.Now;
            return;
        }

        if (DateTime.Now - _exitSeenAt.Value < TimeSpan.FromSeconds(3))
            return;
        if (LogoffInProgress())
            return; // re-evaluated next tick; if the logoff is cancelled we resume after the grace period

        HandleWorkerExit(_worker);
    }

    private bool LogoffInProgress() =>
        _sessionEnded || Diagnostics.SessionShuttingDown() ||
        (_sessionEndingAt is { } t && DateTime.Now - t < SessionEndingGrace);

    private void MonitorRunningWorker(Process p)
    {
        var sample = Diagnostics.Sample(p);
        if (sample != null)
        {
            _firstSample ??= sample;
            _lastSample = sample;
        }

        bool? responsive = Diagnostics.ProbeResponsive(p.Id);
        if (responsive != false)
        {
            _hangStrikes = 0;
            return;
        }

        if (_hangStrikes++ == 0)
        {
            _hangSince = DateTime.Now;
            _hangCpuStart = sample?.Cpu ?? TimeSpan.Zero;
        }
        if (_hangStrikes >= HangStrikesBeforeKill)
            HandleHang(p, sample);
    }

    // ------------------------------------------------------------------ incidents

    private void HandleHang(Process p, ResourceSample? sample)
    {
        var hungFor = DateTime.Now - _hangSince;
        var cpu = (sample?.Cpu ?? _hangCpuStart) - _hangCpuStart;
        string cpuHint = cpu.TotalSeconds > hungFor.TotalSeconds * 0.5
            ? "high CPU while unresponsive → likely a busy loop on the UI thread"
            : "little CPU while unresponsive → likely blocked (deadlock, synchronous I/O, or a blocking wait on the UI thread)";

        var d = new StringBuilder();
        d.AppendLine($"The timer app's windows stopped answering messages (WM_NULL, 5s timeout) for {Diagnostics.FormatDuration(hungFor)} " +
                     $"({_hangStrikes} consecutive probes, 30s apart). It was killed and restarted.");
        d.AppendLine($"CPU used while hung: {cpu.TotalSeconds:F1}s over {hungFor.TotalSeconds:F0}s — {cpuHint}.");
        d.AppendLine($"Version: {Diagnostics.AppVersion}");
        d.AppendLine($"Started: {_workerStart:yyyy-MM-dd HH:mm:ss}");
        AppendResources(d, sample);
        d.AppendLine();
        d.AppendLine(Diagnostics.DescribeThreads(p));

        string? dump = Diagnostics.WriteDump(p, "hang", out string? dumpError);
        d.AppendLine(dump != null
            ? $"Minidump: {dump}\n  Open with `dotnet-dump analyze <file>` then `clrstack -all`, or WinDbg + SOS (`!clrstack`, `~*e !clrstack`)."
            : $"Minidump not written: {dumpError}");
        AppendConsoleTail(d);

        Insert("hang", p.Id, (long)(DateTime.Now - _workerStart).TotalSeconds, null,
            $"Timer app hung (not responding for {Diagnostics.FormatDuration(hungFor)}); killed and restarted",
            d.ToString(), dump);

        _killedForHang = true;
        _hangStrikes = 0;
        try { p.Kill(); }
        catch { /* already gone */ }
    }

    private void HandleWorkerExit(Process p)
    {
        int pid = p.Id;
        int? code = null;
        try
        {
            if (_workerOwned)
                p.WaitForExit(2000); // let the async console readers drain
            code = p.ExitCode;
        }
        catch { /* adopted process: exit code may be unavailable */ }

        var uptime = DateTime.Now - _workerStart;
        _quickFailures = uptime < HealthyUptime ? _quickFailures + 1 : 0;
        int delay = _quickFailures switch { 0 => 2, 1 => 5, 2 => 15, 3 => 30, _ => 60 };

        if (code == ExitAlreadyRunning)
        {
            // Another worker grabbed the mutex first; adopt it or wait for it rather than recording a crash.
            _quickFailures = 0;
            _restartAt = DateTime.Now.AddSeconds(5);
            return;
        }

        if (!_killedForHang)
            RecordExit(pid, code, uptime);

        _restartAt = DateTime.Now.AddSeconds(_killedForHang ? 1 : delay);
        _exitSeenAt = null;
    }

    private void RecordExit(int pid, int? code, TimeSpan uptime)
    {
        string exitText = code is { } c ? Diagnostics.DescribeExitCode(c) : "exit code unavailable (process was adopted, not started by this watchdog)";
        string? dump = Diagnostics.FindWerDump(pid);

        var d = new StringBuilder();
        d.AppendLine("--- Watchdog report ---");
        d.AppendLine($"Exit: {exitText}");
        d.AppendLine($"Uptime: {Diagnostics.FormatDuration(uptime)} (started {_workerStart:yyyy-MM-dd HH:mm:ss}, exit noticed {DateTime.Now:HH:mm:ss})");
        d.AppendLine($"Version: {Diagnostics.AppVersion}");
        d.AppendLine($"Environment: {Diagnostics.EnvironmentLine()}");
        AppendResources(d, null);
        if (dump != null)
            d.AppendLine($"Crash dump (Windows Error Reporting): {dump}");
        AppendConsoleTail(d);
        string events = Diagnostics.ReadEventLog(_workerStart, pid);
        d.AppendLine();
        d.AppendLine("Windows event log (Application):");
        d.AppendLine(events.Length > 0 ? events : "(no matching entries)");
        if (_quickFailures > 1)
            d.AppendLine($"\nNote: {_quickFailures} consecutive exits within {HealthyUptime.TotalMinutes:F0} minutes of starting (crash loop); restart delay is backing off.");

        // If the worker recorded its own managed exception just before dying, extend that record.
        if (code is { } ec && _db.CompleteExceptionCrash(pid, _workerStart.AddSeconds(-5), ec, d.ToString(), dump))
            return;

        Insert("exit", pid, (long)uptime.TotalSeconds, code,
            $"Timer app exited unexpectedly: {exitText}, after {Diagnostics.FormatDuration(uptime)}",
            d.ToString(), dump);
    }

    private void AppendResources(StringBuilder d, ResourceSample? current)
    {
        if (_firstSample != null)
            d.AppendLine($"Resources, first sample: {_firstSample}");
        if (_lastSample != null && !ReferenceEquals(_lastSample, _firstSample))
            d.AppendLine($"Resources, last sample:  {_lastSample}");
        if (current != null && !ReferenceEquals(current, _lastSample))
            d.AppendLine($"Resources, now:          {current}");
    }

    private void AppendConsoleTail(StringBuilder d)
    {
        if (!_workerOwned)
            return;
        var lines = _output.Snapshot();
        d.AppendLine();
        d.AppendLine($"Recent console output ({lines.Count} lines; unhandled exceptions are printed here by the runtime):");
        if (lines.Count == 0)
            d.AppendLine("(none)");
        foreach (var l in lines)
            d.AppendLine(l);
    }

    private void Insert(string kind, int? pid, long? uptimeSeconds, int? exitCode, string summary, string details, string? dump)
    {
        _db.InsertCrash(new CrashRecord
        {
            Time = DateTime.Now,
            Kind = kind,
            Pid = pid,
            AppVersion = Diagnostics.AppVersion,
            UptimeSeconds = uptimeSeconds,
            ExitCode = exitCode,
            Summary = summary,
            Details = details,
            DumpPath = dump,
        });
    }

    // ------------------------------------------------------------------ heartbeat

    private static DateTime BootTimeUtc() => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    private void WriteHeartbeat()
    {
        try
        {
            _db.SetMeta(HeartbeatKey, string.Join('|',
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture),
                _bootUtc.Ticks.ToString(CultureInfo.InvariantCulture)));
        }
        catch { /* DB busy; next tick */ }
    }

    /// <summary>
    /// A heartbeat left over from this same boot means the previous watchdog died without a
    /// logoff (killed, crashed, or stopped by Task Scheduler) and the 10-minute scheduled check
    /// (or a manual start) brought us back. Record how long that gap was.
    /// </summary>
    private void RecordIfPreviousSupervisorDied()
    {
        var parts = _db.GetMeta(HeartbeatKey)?.Split('|');
        if (parts is not { Length: 3 } ||
            !int.TryParse(parts[0], out int oldPid) ||
            !long.TryParse(parts[1], out long lastTicks) ||
            !long.TryParse(parts[2], out long bootTicks))
            return;

        var oldBoot = new DateTime(bootTicks, DateTimeKind.Utc);
        if (Math.Abs((oldBoot - _bootUtc).TotalMinutes) > 2)
            return; // previous run ended with a reboot/power loss — not interesting

        var last = new DateTime(lastTicks, DateTimeKind.Utc).ToLocalTime();
        var gap = DateTime.Now - last;
        bool workerAlive = Mutex.TryOpenExisting(WorkerMutexName, out var m);
        m?.Dispose();

        string impact = workerAlive
            ? "The timer app itself was still running, so time limits stayed enforced, but nothing would have restarted it."
            : "The timer app was not running either, so time limits were NOT enforced during this gap.";
        Insert("not-running", oldPid, null, null,
            $"Watchdog (pid {oldPid}) stopped without a logoff; restarted after up to {Diagnostics.FormatDuration(gap)}",
            $"The previous watchdog process (pid {oldPid}) last checked in at {last:yyyy-MM-dd HH:mm:ss} and did not shut down cleanly.\n" +
            $"It was found missing at {DateTime.Now:yyyy-MM-dd HH:mm:ss} (gap ≤ {Diagnostics.FormatDuration(gap)}).\n{impact}\n" +
            "Likely causes: killed via Task Manager/taskkill, stopped by Task Scheduler (e.g. an older task definition's 3-day " +
            "time limit or battery settings — re-run --install), or a watchdog crash (look for a 'supervisor-exception' record).",
            null);
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) => _sessionEndingAt = DateTime.Now;

    private void OnSessionEnded(object? sender, SessionEndedEventArgs e)
    {
        _sessionEnded = true;
        try { _db.DeleteMeta(HeartbeatKey); }
        catch { /* ignore */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            SystemEvents.SessionEnding -= OnSessionEnding;
            SystemEvents.SessionEnded -= OnSessionEnded;
            try { _db.DeleteMeta(HeartbeatKey); }
            catch { /* ignore */ }
            _worker?.Dispose();
            _db.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Thread-safe ring buffer of the worker's most recent stdout/stderr lines.</summary>
    private sealed class OutputTail(int capacity)
    {
        private readonly Queue<string> _lines = new();

        public void Add(string stream, string line)
        {
            lock (_lines)
            {
                _lines.Enqueue($"[{DateTime.Now:HH:mm:ss} {stream}] {Diagnostics.Truncate(line, 2000)}");
                while (_lines.Count > capacity)
                    _lines.Dequeue();
            }
        }

        public List<string> Snapshot()
        {
            lock (_lines) return _lines.ToList();
        }

        public void Clear()
        {
            lock (_lines) _lines.Clear();
        }
    }
}
