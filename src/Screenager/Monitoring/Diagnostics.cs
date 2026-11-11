using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Screenager.Native;

namespace Screenager.Monitoring;

/// <summary>Point-in-time resource usage of a process, used to spot leaks and busy loops.</summary>
public sealed record ResourceSample(
    DateTime Time, long WorkingSet, long PrivateBytes, int Handles, int Threads, uint GdiObjects, uint UserObjects, TimeSpan Cpu)
{
    public override string ToString() =>
        $"{Time:HH:mm:ss} working set {Mb(WorkingSet)}, private {Mb(PrivateBytes)}, handles {Handles}, " +
        $"threads {Threads}, GDI objects {GdiObjects}, USER objects {UserObjects}, CPU {Cpu.TotalSeconds:F1}s";

    private static string Mb(long bytes) => $"{bytes / (1024.0 * 1024.0):F1} MB";
}

/// <summary>Helpers shared by the in-process crash reporter and the supervisor.</summary>
public static class Diagnostics
{
    /// <summary>
    /// Set SCREENAGER_DATA_DIR to run a fully isolated instance (its own database, dumps, and
    /// single-instance locks) alongside the real one — used for testing. Inherited by the worker.
    /// </summary>
    private static readonly string? DataDirOverride =
        Environment.GetEnvironmentVariable("SCREENAGER_DATA_DIR") is { Length: > 0 } d ? Path.GetFullPath(d) : null;

    public static string DataDir { get; } = DataDirOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "screenager");

    /// <summary>Appended to mutex names so an isolated instance doesn't collide with the real one.</summary>
    public static string InstanceSuffix { get; } = DataDirOverride == null ? "" :
        "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(DataDirOverride.ToLowerInvariant())))[..12];

    public static string DumpDir => Path.Combine(DataDir, "dumps");

    /// <summary>Assembly informational version (the SDK appends the git commit) plus the exe timestamp.</summary>
    public static string AppVersion { get; } = ComputeVersion();

    private static string ComputeVersion()
    {
        var asm = typeof(Diagnostics).Assembly;
        string v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? asm.GetName().Version?.ToString() ?? "unknown";
        try
        {
            if (Environment.ProcessPath is { } exe)
                v += $" (exe {File.GetLastWriteTime(exe):yyyy-MM-dd HH:mm})";
        }
        catch { /* best effort */ }
        return v;
    }

    public static string EnvironmentLine() =>
        $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), .NET {Environment.Version}, " +
        $"process {RuntimeInformation.ProcessArchitecture}, machine {Environment.MachineName}";

    public static ResourceSample? Sample(Process p)
    {
        try
        {
            p.Refresh();
            if (p.HasExited)
                return null;
            uint gdi = 0, user = 0;
            try
            {
                gdi = NativeMethods.GetGuiResources(p.Handle, NativeMethods.GR_GDIOBJECTS);
                user = NativeMethods.GetGuiResources(p.Handle, NativeMethods.GR_USEROBJECTS);
            }
            catch { /* no access */ }
            return new ResourceSample(DateTime.Now, p.WorkingSet64, p.PrivateMemorySize64, p.HandleCount,
                p.Threads.Count, gdi, user, p.TotalProcessorTime);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Per-thread state, to tell a deadlock (everything waiting) from a busy loop (one thread burning CPU).</summary>
    public static string DescribeThreads(Process p, int max = 40)
    {
        var sb = new StringBuilder();
        try
        {
            p.Refresh();
            var threads = p.Threads.Cast<ProcessThread>().ToList();
            sb.AppendLine($"{threads.Count} threads (id, state, wait reason, CPU):");
            foreach (var t in threads.Take(max))
            {
                try
                {
                    string wait = t.ThreadState == System.Diagnostics.ThreadState.Wait ? t.WaitReason.ToString() : "-";
                    sb.AppendLine($"  {t.Id,6}  {t.ThreadState,-12} {wait,-16} {t.TotalProcessorTime.TotalSeconds,8:F2}s");
                }
                catch { sb.AppendLine($"  {t.Id,6}  (exited)"); }
            }
            if (threads.Count > max)
                sb.AppendLine($"  … {threads.Count - max} more");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(thread list unavailable: {ex.Message})");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Sends WM_NULL to each of the process's top-level windows with a timeout. Returns false if
    /// any of them fails to answer (its thread is not pumping messages), true if all answer, and
    /// null if the process has no windows to probe.
    /// </summary>
    public static bool? ProbeResponsive(int pid, uint timeoutMs = 5000)
    {
        var windows = NativeMethods.GetTopLevelWindows(pid);
        if (windows.Count == 0)
            return null;
        foreach (var hWnd in windows.Take(16))
        {
            var ok = NativeMethods.SendMessageTimeoutW(hWnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero,
                NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_BLOCK, timeoutMs, out _);
            if (ok == IntPtr.Zero && Marshal.GetLastPInvokeError() != 1400 /* ERROR_INVALID_WINDOW_HANDLE: window went away */)
                return false;
        }
        return true;
    }

    public static bool SessionShuttingDown() => NativeMethods.GetSystemMetrics(NativeMethods.SM_SHUTTINGDOWN) != 0;

    /// <summary>Human-readable meaning of a process exit code (NTSTATUS values are the interesting ones).</summary>
    public static string DescribeExitCode(int code)
    {
        string meaning = unchecked((uint)code) switch
        {
            0 => "exited normally",
            1 => "terminated with code 1 (typically Task Manager 'End task', taskkill /F, or TerminateProcess)",
            0xE0434352 => "unhandled .NET exception",
            0x80131623 => "Environment.FailFast",
            0xC0000005 => "access violation (native crash)",
            0xC00000FD => "stack overflow (often unbounded recursion)",
            0xC0000409 => "fail-fast / stack buffer overrun (__fastfail)",
            0xC0000374 => "heap corruption",
            0xC0000017 or 0xC000012D => "out of memory / commit limit reached",
            0xC000013A => "terminated by Ctrl+C / console close",
            0x40010004 => "terminated by a debugger or during logoff (DBG_TERMINATE_PROCESS)",
            0xCFFFFFFF => "terminated by Windows after it stopped responding (End task on a hung window)",
            _ => "unrecognised exit code",
        };
        return $"{meaning} (exit code {code} / 0x{unchecked((uint)code):X8})";
    }

    /// <summary>
    /// Application-log events about screenager (Application Error, .NET Runtime, WER, Application
    /// Hang) since <paramref name="sinceLocal"/>. The .NET Runtime 1026 event carries the managed
    /// stack; Application Error 1000 carries the faulting module and offset for native crashes.
    /// </summary>
    public static string ReadEventLog(DateTime sinceLocal, int pid, int maxChars = 12_000)
    {
        var sb = new StringBuilder();
        try
        {
            // Only events since the process started (plus a little clock slack): anything earlier
            // belongs to a previous process and is already attached to its own record.
            long ms = (long)Math.Clamp((DateTime.Now - sinceLocal).TotalMilliseconds + 5_000, 10_000, 7L * 24 * 3600 * 1000);
            string xpath =
                "*[System[Provider[@Name='Application Error' or @Name='.NET Runtime' or " +
                "@Name='Windows Error Reporting' or @Name='Application Hang'] " +
                $"and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
            using var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, xpath));
            string pidHex = $"0x{pid:x}";
            string exeName = Path.GetFileName(Environment.ProcessPath) ?? "screenager.exe";
            for (var rec = reader.ReadEvent(); rec != null; rec = reader.ReadEvent())
            {
                using (rec)
                {
                    string msg;
                    try { msg = rec.FormatDescription() ?? ""; }
                    catch { msg = ""; }
                    if (!ContainsExeName(msg, exeName))
                        continue;
                    // Application Error names the faulting pid; skip faults of other screenager processes.
                    if (rec.ProviderName == "Application Error" && !msg.Contains(pidHex, StringComparison.OrdinalIgnoreCase))
                        continue;
                    sb.AppendLine($"[{rec.TimeCreated:yyyy-MM-dd HH:mm:ss}] {rec.ProviderName} (event {rec.Id})");
                    sb.AppendLine(msg.Trim());
                    sb.AppendLine();
                    if (sb.Length > maxChars)
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(event log unavailable: {ex.Message})");
        }
        return sb.Length > maxChars ? sb.ToString(0, maxChars) + "\n…(truncated)" : sb.ToString();
    }

    /// <summary>True if <paramref name="msg"/> names exactly this exe (not e.g. "OtherScreenager.exe").</summary>
    private static bool ContainsExeName(string msg, string exeName)
    {
        for (int i = msg.IndexOf(exeName, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = msg.IndexOf(exeName, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (i == 0 || !char.IsLetterOrDigit(msg[i - 1]))
                return true;
        }
        return false;
    }

    /// <summary>A crash dump Windows Error Reporting wrote for this pid (enabled by <c>--install</c>), if any.</summary>
    public static string? FindWerDump(int pid)
    {
        try
        {
            if (!Directory.Exists(DumpDir))
                return null;
            return Directory.EnumerateFiles(DumpDir, $"screenager.exe.{pid}.dmp").FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Write a minidump of a (hung) process so its stacks can be inspected later. Keeps the newest few.</summary>
    public static string? WriteDump(Process p, string label, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(DumpDir);
            Prune($"screenager-{label}-*.dmp", keep: 2); // plus the one about to be written
            string path = Path.Combine(DumpDir, $"screenager-{label}-{DateTime.Now:yyyyMMdd-HHmmss}-{p.Id}.dmp");
            using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            uint flags = NativeMethods.MiniDumpWithDataSegs | NativeMethods.MiniDumpWithHandleData |
                         NativeMethods.MiniDumpWithUnloadedModules | NativeMethods.MiniDumpWithPrivateReadWriteMemory |
                         NativeMethods.MiniDumpWithFullMemoryInfo | NativeMethods.MiniDumpWithThreadInfo;
            if (NativeMethods.MiniDumpWriteDump(p.Handle, (uint)p.Id, fs.SafeFileHandle, flags,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
                return path;
            error = $"MiniDumpWriteDump failed (Win32 error {Marshal.GetLastPInvokeError()})";
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        return null;
    }

    private static void Prune(string pattern, int keep)
    {
        try
        {
            foreach (var old in new DirectoryInfo(DumpDir).GetFiles(pattern)
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(keep))
                old.Delete();
        }
        catch { /* best effort */ }
    }

    public static string FormatDuration(TimeSpan ts)
    {
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
