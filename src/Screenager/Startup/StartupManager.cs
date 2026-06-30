using System.Diagnostics;
using System.Security.Principal;

namespace Screenager.Startup;

/// <summary>
/// Installs/removes the hidden logon scheduled task and the Microsoft Defender folder exclusion.
/// Both require elevation; <see cref="EnsureElevated"/> re-launches with a UAC prompt when needed.
/// </summary>
public static class StartupManager
{
    private const string TaskName = "Screenager";

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

    public static void Install()
    {
        var exe = Environment.ProcessPath!;
        var dir = Path.GetDirectoryName(exe)!;

        // Hidden logon task running as the interactive (logged-on) user, standard privileges.
        Run("schtasks", $"/Create /F /TN \"{TaskName}\" /TR \"\\\"{exe}\\\"\" /SC ONLOGON /IT /RL LIMITED");

        // Defender exclusion so behavior heuristics don't quarantine the app.
        RunPowerShell($"Add-MpPreference -ExclusionPath '{dir}'");

        Console.WriteLine("Installed: logon task + Defender exclusion for");
        Console.WriteLine("  " + dir);
    }

    public static void Uninstall()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath!)!;
        Run("schtasks", $"/Delete /F /TN \"{TaskName}\"");
        RunPowerShell($"Remove-MpPreference -ExclusionPath '{dir}'");
        Console.WriteLine("Uninstalled logon task + Defender exclusion.");
    }

    private static void Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            p?.WaitForExit();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ! {file} failed: {ex.Message}");
        }
    }

    private static void RunPowerShell(string command)
        => Run("powershell", $"-NoProfile -NonInteractive -Command \"{command}\"");
}
