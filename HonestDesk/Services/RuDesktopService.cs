using System.Diagnostics;
using Microsoft.Win32;

namespace HonestDesk.Services;

public sealed class RuDesktopService
{
    private readonly AppLogger _logger;

    public RuDesktopService(AppLogger logger)
    {
        _logger = logger;
    }

    public string FindExecutable(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        var candidates = new[]
        {
            @"C:\Program Files\RuDesktop\rudesktop.exe",
            @"C:\Program Files (x86)\RuDesktop\rudesktop.exe"
        };

        var path = candidates.FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return FindFromUninstallRegistry() ?? "";
    }

    public bool Open(string configuredPath)
    {
        var exe = FindExecutable(configuredPath);
        if (string.IsNullOrWhiteSpace(exe))
        {
            _logger.Error("RuDesktop executable was not found");
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            _logger.Info($"RuDesktop started: {exe}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("RuDesktop start failed", ex);
            return false;
        }
    }

    public bool ConnectOrCopy(string configuredPath, string ruDesktopId)
    {
        if (!string.IsNullOrWhiteSpace(ruDesktopId))
        {
            Clipboard.SetText(ruDesktopId);
            _logger.Info($"RuDesktop ID copied: {ruDesktopId}");
        }

        return Open(configuredPath);
    }

    private static string? FindFromUninstallRegistry()
    {
        var roots = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var root in roots)
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key is null)
            {
                continue;
            }

            foreach (var name in key.GetSubKeyNames())
            {
                using var appKey = key.OpenSubKey(name);
                var displayName = appKey?.GetValue("DisplayName")?.ToString() ?? "";
                if (!displayName.Contains("RuDesktop", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var installLocation = appKey?.GetValue("InstallLocation")?.ToString() ?? "";
                var exe = Path.Combine(installLocation, "rudesktop.exe");
                if (File.Exists(exe))
                {
                    return exe;
                }

                var displayIcon = (appKey?.GetValue("DisplayIcon")?.ToString() ?? "").Trim('"');
                if (File.Exists(displayIcon))
                {
                    return displayIcon;
                }
            }
        }

        return null;
    }
}
