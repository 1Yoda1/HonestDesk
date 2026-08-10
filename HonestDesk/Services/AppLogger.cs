namespace HonestDesk.Services;

public sealed class AppLogger
{
    private const long MaxLogBytes = 5 * 1024 * 1024;
    private readonly object _sync = new();

    public void Info(string message) => Write("INFO", message);
    public void Error(string message, Exception? exception = null) => Write("ERROR", exception is null ? message : $"{message}: {exception}");

    private void Write(string level, string message)
    {
        try
        {
            AppPaths.EnsureDataDirectory();
            lock (_sync)
            {
                RotateIfNeeded();
                File.AppendAllText(AppPaths.LogPath, $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the operator workflow.
        }
    }

    private static void RotateIfNeeded()
    {
        var current = new FileInfo(AppPaths.LogPath);
        if (!current.Exists || current.Length < MaxLogBytes) return;
        string previous = AppPaths.LogPath + ".1";
        if (File.Exists(previous)) File.Delete(previous);
        File.Move(AppPaths.LogPath, previous);
    }
}
