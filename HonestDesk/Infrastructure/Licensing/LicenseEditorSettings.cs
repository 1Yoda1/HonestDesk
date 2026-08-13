using System;
using System.IO;
using Newtonsoft.Json;

namespace HonestDesk.Infrastructure.Licensing
{
    public sealed class LicenseEditorSettings
    {
        public string PrivateKeyPath { get; set; } = string.Empty;
        public string PublicKeyPath { get; set; } = string.Empty;
        public string KeyId { get; set; } = "primary-2026";
        public int ValidityDays { get; set; } = 365;
        private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HonestDesk", "license-editor-settings.json");
        public static LicenseEditorSettings Load()
        {
            try
            {
                LicenseEditorSettings settings = File.Exists(SettingsPath)
                    ? JsonConvert.DeserializeObject<LicenseEditorSettings>(File.ReadAllText(SettingsPath)) ?? new()
                    : new();
                if (string.Equals(settings.KeyId, "production-2026-01", StringComparison.OrdinalIgnoreCase))
                {
                    settings.KeyId = "primary-2026";
                    settings.Save();
                }
                return settings;
            }
            catch { return new(); }
        }
        public void Save()
        {
            string? directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }
}
