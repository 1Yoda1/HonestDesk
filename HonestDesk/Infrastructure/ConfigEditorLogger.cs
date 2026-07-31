using System;
using System.IO;
using System.Text;

namespace HonestDesk.Infrastructure
{
    public static class ConfigEditorLogger
    {
        private static readonly object LockObject = new object();

        public static string LogDirectory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "HonestDesk",
                "logs");

        public static string LogPath => Path.Combine(LogDirectory, "honestdesk-admin.log");

        private static string FallbackLogDirectory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "HonestDesk",
                "logs");

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Error(string message, Exception exception = null)
        {
            var text = exception == null
                ? message
                : message + Environment.NewLine + exception;

            Write("ERROR", text);
        }

        public static void Separator(string title)
        {
            Write("INFO", new string('-', 80) + Environment.NewLine + title);
        }

        private static void Write(string level, string message)
        {
            lock (LockObject)
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
                if (TryAppend(LogPath, line))
                    return;

                TryAppend(Path.Combine(FallbackLogDirectory, "honestdesk-admin.log"), line);
            }
        }

        private static bool TryAppend(string path, string line)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, line, Encoding.UTF8);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
