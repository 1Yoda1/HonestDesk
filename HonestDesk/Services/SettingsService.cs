using System.Text.Json;
using HonestDesk.Models;
using Microsoft.Win32;

namespace HonestDesk.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public Exception? LastLoadError { get; private set; }

    public AppSettings Load()
    {
        LastLoadError = null;
        AppPaths.EnsureDataDirectory();
        if (!File.Exists(AppPaths.SettingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(AppPaths.SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json)
                ?? throw new InvalidDataException("settings.json contains no settings object.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            LastLoadError = ex;
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        AppPaths.EnsureDataDirectory();
        AtomicFile.WriteAllText(AppPaths.SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        LastLoadError = null;
        ApplyStartup(settings.StartWithWindows);
    }

    private static void ApplyStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            key.SetValue("HonestDesk", Environment.ProcessPath ?? AppContext.BaseDirectory);
        }
        else
        {
            key.DeleteValue("HonestDesk", throwOnMissingValue: false);
        }
    }
}
