using SayWhat.Core.LogIngest;
using SayWhat.Platform.Windows.LogIngest;

namespace SayWhat.Tests.LogIngest;

public sealed class LogLockProbeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("saywhat-probe-").FullName;
    private readonly WindowsLogLockProbe _probe = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string NewFile()
    {
        var path = Path.Combine(_dir, "Local_20260101_000000.txt");
        File.WriteAllText(path, "header\nline\n");
        return path;
    }

    [Theory]
    [InlineData(FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)]
    [InlineData(FileAccess.Write, FileShare.ReadWrite)]
    [InlineData(FileAccess.Write, FileShare.Read)]
    [InlineData(FileAccess.Write, FileShare.None)]
    [InlineData(FileAccess.ReadWrite, FileShare.ReadWrite)]
    public void HeldByWriter_IsHeld(FileAccess access, FileShare sharing)
    {
        var path = NewFile();
        using (new FileStream(path, FileMode.Open, access, sharing))
        {
            Assert.Equal(LogLockState.Held, _probe.Probe(path));
        }
    }

    [Fact]
    public void HeldByReaderAllowingSharing_IsNotHeld()
    {
        var path = NewFile();
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Assert.Equal(LogLockState.NotHeld, _probe.Probe(path));
    }

    [Fact]
    public void AfterHandleCloses_IsNotHeld()
    {
        var path = NewFile();
        using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.Equal(LogLockState.Held, _probe.Probe(path));
        }

        Assert.Equal(LogLockState.NotHeld, _probe.Probe(path));
    }

    [Fact]
    public void MissingFile_IsNeverNotHeld()
    {
        Assert.Equal(LogLockState.Unavailable, _probe.Probe(Path.Combine(_dir, "missing.txt")));
    }

    [Fact]
    public void MissingFolder_IsUnavailable()
    {
        Assert.Equal(LogLockState.Unavailable, _probe.Probe(Path.Combine(_dir, "nope", "x.txt")));
    }

    [Fact]
    public void Probe_LeavesFileUnchanged()
    {
        var path = NewFile();
        var oldTime = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, oldTime);
        var content = File.ReadAllBytes(path);

        _probe.Probe(path);

        Assert.Equal(content, File.ReadAllBytes(path));
        Assert.Equal(content.Length, new FileInfo(path).Length);
        Assert.Equal(oldTime, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Probe_ReleasesItsHandle()
    {
        var path = NewFile();
        _probe.Probe(path);

        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }
}
