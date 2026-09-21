using System.ComponentModel;
using System.Diagnostics;
using OneHub.Diagnostics.HeapView;
using Xunit;

public sealed class SpotlightDsymLocatorTests
{
    [Fact]
    public void UsesOneQueryArgumentAndPreservesNullDelimitedPaths()
    {
        var uuid = Guid.Parse("097e3e76-29d9-3dd2-ad54-a1cb5c15682c");
        var paths = SpotlightDsymLocator.Find(uuid, TextWriter.Null, startInfo =>
        {
            Assert.Equal("/usr/bin/mdfind", startInfo.FileName);
            Assert.False(startInfo.UseShellExecute);
            Assert.True(startInfo.RedirectStandardOutput);
            Assert.True(startInfo.RedirectStandardError);
            Assert.Equal(["-0", "com_apple_xcode_dsym_uuids == 097E3E76-29D9-3DD2-AD54-A1CB5C15682C"], startInfo.ArgumentList);
            return new(0, "/archive with spaces/app.dSYM\0/archive\nwith newline/app.dSYM\0/archive with spaces/app.dSYM\0", "");
        });
        Assert.Equal(["/archive with spaces/app.dSYM", "/archive\nwith newline/app.dSYM"], paths);
    }

    [Fact]
    public void EmptyUuidDoesNotRunCommand()
    {
        Assert.Empty(SpotlightDsymLocator.Find(Guid.Empty, TextWriter.Null,
            _ => throw new Exception("Unexpected Spotlight query")));
    }

    [Fact]
    public void FailedCommandDoesNotReturnPartialResults()
    {
        var log = new StringWriter();
        Assert.Empty(SpotlightDsymLocator.Find(Guid.NewGuid(), log,
            _ => new(1, "/partial/result.dSYM\0", "index unavailable")));
        Assert.Contains("index unavailable", log.ToString());
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("start")]
    [InlineData("io")]
    public void SearchErrorsAreNonfatal(string failure)
    {
        var log = new StringWriter();
        Assert.Empty(SpotlightDsymLocator.Find(Guid.NewGuid(), log, _ => throw (failure switch
        {
            "timeout" => new TimeoutException("timed out"),
            "start" => new Win32Exception("cannot start"),
            _ => new IOException("cannot read")
        })));
        Assert.Contains("lookup unavailable", log.ToString());
    }

    [UnixFact]
    public async Task CommandRunnerCapturesBothPipesAndExitCode()
    {
        var startInfo = Shell("printf 'bundle\\0'; printf 'diagnostic' >&2; exit 7");
        var result = await SpotlightDsymLocator.RunAsync(startInfo, TimeSpan.FromSeconds(5));
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("bundle\0", result.Output);
        Assert.Equal("diagnostic", result.Error);
    }

    [UnixFact]
    public async Task CommandRunnerTimesOut()
    {
        // exec makes sleep the process being killed; no shell child is left running.
        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            SpotlightDsymLocator.RunAsync(Shell("exec sleep 30"), TimeSpan.FromMilliseconds(100))
                .WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.StartsWith("mdfind did not complete", error.Message);
    }

    private static ProcessStartInfo Shell(string command)
    {
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);
        return startInfo;
    }

    public sealed class UnixFactAttribute : FactAttribute
    {
        public UnixFactAttribute()
        {
            if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
                Skip = "Requires a Unix shell to test process execution.";
        }
    }
}
