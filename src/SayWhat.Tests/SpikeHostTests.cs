using System.Diagnostics;
using SayWhat.Spike;

namespace SayWhat.Tests;

public class SpikeHostTests
{
    [Fact]
    public void Run_ReturnsZero()
    {
        Assert.Equal(0, SpikeHost.Run([]));
    }

    [Fact]
    public void Process_RunsHeadlessAndExitsZero()
    {
        var assembly = typeof(SpikeHost).Assembly.Location;
        var startInfo = new ProcessStartInfo("dotnet", $"\"{assembly}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(startInfo)!;
        var exited = process.WaitForExit(TimeSpan.FromSeconds(30));

        Assert.True(exited);
        Assert.Equal(0, process.ExitCode);
    }
}
