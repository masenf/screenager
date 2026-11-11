using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Screenager.Startup;

/// <summary>
/// Installs/removes the hidden scheduled task, the Microsoft Defender folder exclusion, and the
/// Windows Error Reporting crash-dump setting. All require elevation; <see cref="EnsureElevated"/>
/// re-launches with a UAC prompt when needed.
/// </summary>
public static class StartupManager
{
    private const string TaskName = "Screenager";
    private const string WerLocalDumpsKey = @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\screenager.exe";

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>If not elevated, re-launch self elevated with the same args and return true (caller should exit).</summary>
    public static bool EnsureElevated(string[] args)
    {
        if (IsElevated())
            return false;

        var psi = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            Arguments = string.Join(' ', args),
            UseShellExecute = true,
            Verb = "runas",
        };
        try { Process.Start(psi); }
        catch { /* user declined UAC */ }
        return true;
    }

    /// <summary>Returns null on success, or an error message.</summary>
    public static string? Install()
    {
        var exe = Environment.ProcessPath!;
        var dir = Path.GetDirectoryName(exe)!;

        // Hidden task running as the interactive (logged-on) user, standard privileges. It starts
        // at logon and re-runs every 10 minutes; a run while Screenager is already up exits at
        // once (single-instance), so the repeat only matters after the watchdog has died.
        string xmlPath = Path.Combine(Path.GetTempPath(), $"screenager-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(xmlPath, BuildTaskXml(exe, dir), Encoding.Unicode);
            int rc = Run("schtasks", $"/Create /F /TN \"{TaskName}\" /XML \"{xmlPath}\"");
            if (rc != 0)
                return $"Creating the scheduled task failed (schtasks exit code {rc}).";
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* ignore */ }
        }

        // Defender exclusion so behavior heuristics don't quarantine the app.
        RunPowerShell($"Add-MpPreference -ExclusionPath '{dir}'");

        // Have Windows Error Reporting keep a minidump when screenager crashes; the watchdog
        // links it from the crash record. %LOCALAPPDATA% expands for the crashing user.
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(WerLocalDumpsKey);
            key.SetValue("DumpFolder", @"%LOCALAPPDATA%\screenager\dumps", RegistryValueKind.ExpandString);
            key.SetValue("DumpCount", 5, RegistryValueKind.DWord);
            key.SetValue("DumpType", 1, RegistryValueKind.DWord); // minidump
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ! crash-dump setting failed: {ex.Message}");
        }

        // Start it now (as the user, not elevated) so the new watchdog runs without a re-logon.
        Run("schtasks", $"/Run /TN \"{TaskName}\"");

        Console.WriteLine("Installed: logon + 10-minute task, Defender exclusion, crash dumps for");
        Console.WriteLine("  " + dir);
        return null;
    }

    /// <summary>
    /// Task definition. Notable differences from schtasks' defaults, each of which can silently
    /// stop Screenager: no 3-day execution time limit (PT0S), keeps running on battery, normal
    /// priority (the default 7 is below-normal and would be inherited by the timer app).
    /// </summary>
    internal static string BuildTaskXml(string exe, string dir)
    {
        string user = WindowsIdentity.GetCurrent().User!.Value; // SID of the installing (monitored) user
        static string X(string s) => SecurityElement.Escape(s)!;
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Screenager screen-time limiter. Starts at logon and re-checks every 10 minutes that it is running.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <Repetition>
                    <Interval>PT10M</Interval>
                    <StopAtDurationEnd>false</StopAtDurationEnd>
                  </Repetition>
                  <UserId>{X(user)}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{X(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>"{X(exe)}"</Command>
                  <WorkingDirectory>{X(dir)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    public static void Uninstall()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath!)!;
        Run("schtasks", $"/Delete /F /TN \"{TaskName}\"");
        RunPowerShell($"Remove-MpPreference -ExclusionPath '{dir}'");
        try { Registry.LocalMachine.DeleteSubKeyTree(WerLocalDumpsKey, throwOnMissingSubKey: false); }
        catch { /* ignore */ }
        Console.WriteLine("Uninstalled logon task + Defender exclusion + crash-dump setting.");
    }

    private static int Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p == null)
                return -1;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ! {file} failed: {ex.Message}");
            return -1;
        }
    }

    private static void RunPowerShell(string command)
        => Run("powershell", $"-NoProfile -NonInteractive -Command \"{command}\"");
}
