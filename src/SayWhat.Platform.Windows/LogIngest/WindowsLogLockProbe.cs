using SayWhat.Core.LogIngest;

namespace SayWhat.Platform.Windows.LogIngest;

public sealed class WindowsLogLockProbe : ILogLockProbe
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    public LogLockState Probe(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return LogLockState.NotHeld;
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return LogLockState.Held;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return LogLockState.Unavailable;
        }
    }

    private static bool IsSharingViolation(IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        return code is ErrorSharingViolation or ErrorLockViolation;
    }
}
