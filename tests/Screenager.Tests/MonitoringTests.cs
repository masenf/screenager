using System.Diagnostics;
using System.Security.Principal;
using System.Xml.Linq;
using Screenager.Monitoring;
using Screenager.Startup;

namespace Screenager.Tests;

/// <summary>
/// The scheduled task is what brings Screenager back after it dies; these pin the settings
/// whose Task Scheduler defaults used to stop it (3-day limit, battery, no re-check).
/// </summary>
public class ScheduledTaskTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private const string Exe = @"C:\Program Files\R&D <Screenager>\screenager.exe";

    private static XElement Task() =>
        XDocument.Parse(StartupManager.BuildTaskXml(Exe, Path.GetDirectoryName(Exe)!)).Root!;

    private static string Get(XElement root, params string[] path) =>
        path.Aggregate(root, (e, name) => e.Element(Ns + name)!).Value;

    [Fact]
    public void StartsAtLogonAndRechecksEveryTenMinutesIndefinitely()
    {
        var root = Task();
        var trigger = root.Element(Ns + "Triggers")!.Element(Ns + "LogonTrigger")!;
        Assert.Equal("PT10M", Get(trigger, "Repetition", "Interval"));
        Assert.Null(trigger.Element(Ns + "Repetition")!.Element(Ns + "Duration")); // no end
        Assert.Equal(WindowsIdentity.GetCurrent().User!.Value, Get(trigger, "UserId"));
    }

    [Fact]
    public void IsNeverStoppedByTaskScheduler()
    {
        var root = Task();
        Assert.Equal("PT0S", Get(root, "Settings", "ExecutionTimeLimit"));
        Assert.Equal("false", Get(root, "Settings", "DisallowStartIfOnBatteries"));
        Assert.Equal("false", Get(root, "Settings", "StopIfGoingOnBatteries"));
        Assert.Equal("Parallel", Get(root, "Settings", "MultipleInstancesPolicy"));
        Assert.Equal("4", Get(root, "Settings", "Priority"));
    }

    [Fact]
    public void RunsAsTheInteractiveUserWithoutElevation()
    {
        var root = Task();
        Assert.Equal("InteractiveToken", Get(root, "Principals", "Principal", "LogonType"));
        Assert.Equal("LeastPrivilege", Get(root, "Principals", "Principal", "RunLevel"));
    }

    [Fact]
    public void ExePathIsQuotedAndXmlEscaped()
    {
        var root = Task();
        Assert.Equal($"\"{Exe}\"", Get(root, "Actions", "Exec", "Command"));
        Assert.Equal(Path.GetDirectoryName(Exe), Get(root, "Actions", "Exec", "WorkingDirectory"));
    }
}

public class DiagnosticsTests
{
    [Theory]
    [InlineData(unchecked((int)0xE0434352), "unhandled .NET exception")]
    [InlineData(unchecked((int)0xC0000005), "access violation")]
    [InlineData(unchecked((int)0xC00000FD), "stack overflow")]
    [InlineData(1, "Task Manager")]
    [InlineData(0, "exited normally")]
    [InlineData(1234, "unrecognised exit code")]
    public void DecodesExitCodes(int code, string expected)
    {
        var text = Diagnostics.DescribeExitCode(code);
        Assert.Contains(expected, text);
        Assert.Contains($"0x{unchecked((uint)code):X8}", text);
    }

    [Fact]
    public void SamplesProcessResources()
    {
        using var self = Process.GetCurrentProcess();
        var s = Diagnostics.Sample(self);
        Assert.NotNull(s);
        Assert.True(s!.WorkingSet > 0);
        Assert.True(s.Handles > 0);
        Assert.True(s.Threads > 0);
    }

    [Fact]
    public void VersionIncludesTheBuild()
    {
        Assert.False(string.IsNullOrWhiteSpace(Diagnostics.AppVersion));
    }
}
