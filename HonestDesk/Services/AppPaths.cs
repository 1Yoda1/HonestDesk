namespace HonestDesk.Services;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HonestDesk");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public static string StatePath => Path.Combine(DataDirectory, "state.json");
    public static string LogPath => Path.Combine(DataDirectory, "honestdesk.log");
    public static string AdminAccessPath => Path.Combine(DataDirectory, "admin-access.dat");
    public static string AdminAuditPath => Path.Combine(DataDirectory, "admin-audit.log");
    public static string AdminDraftPath => Path.Combine(DataDirectory, "admin-draft.dat");
    public static string LicenseDirectory => Path.Combine(DataDirectory, "licenses");
    public static string LicenseManifestPath => Path.Combine(LicenseDirectory, "licenses.json");

    public static void EnsureDataDirectory()
    {
        Directory.CreateDirectory(DataDirectory);
    }
}
