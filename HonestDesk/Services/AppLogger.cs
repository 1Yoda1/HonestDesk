namespace HonestDesk.Services;

public sealed class AppLogger
{
    private const long MaxLogBytes = 5 * 1024 * 1024;
    private static readonly object Sync = new();

    public void Info(string message) => Write("INFO", message);
    public void Error(string context, Exception? exception = null) =>
        Write("ERROR", exception is null ? context : FormatException(context, exception));
    public void Critical(string context, Exception exception) =>
        Write("FATAL", FormatException(context, exception));

    private void Write(string level, string message)
    {
        try
        {
            AppPaths.EnsureDataDirectory();
            lock (Sync)
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

    private static string FormatException(string context, Exception exception) =>
        $"Context: {context}{Environment.NewLine}" +
        $"ExceptionType: {exception.GetType().FullName}{Environment.NewLine}" +
        $"Message: {exception.Message}{Environment.NewLine}" +
        $"Exception:{Environment.NewLine}{exception}";

    private static void RotateIfNeeded()
    {
        var current = new FileInfo(AppPaths.LogPath);
        if (!current.Exists || current.Length < MaxLogBytes) return;
        string previous = AppPaths.LogPath + ".1";
        if (File.Exists(previous)) File.Delete(previous);
        File.Move(AppPaths.LogPath, previous);
    }
}
