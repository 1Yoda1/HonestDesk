using System.Text.Json;
using HonestDesk.Models;
using Microsoft.Win32;

namespace HonestDesk.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AppSettings Load()
    {
        AppPaths.EnsureDataDirectory();
        if (!File.Exists(AppPaths.SettingsPath))
        {
            return new AppSettings();
        }

        var json = File.ReadAllText(AppPaths.SettingsPath);
        return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        AppPaths.EnsureDataDirectory();
        File.WriteAllText(AppPaths.SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
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
