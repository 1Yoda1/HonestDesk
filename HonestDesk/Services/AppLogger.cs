namespace HonestDesk.Services;

public sealed class AppLogger
{
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
                File.AppendAllText(AppPaths.LogPath, $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the operator workflow.
        }
    }
}
