using System;
using System.IO;
using Newtonsoft.Json;

namespace HonestDesk.Infrastructure.Licensing
{
    public sealed class LicenseEditorSettings
    {
        public string PrivateKeyPath { get; set; }
        public string PublicKeyPath { get; set; }
        public string KeyId { get; set; } = "production-2026-01";
        public int ValidityDays { get; set; } = 365;
        private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HonestDesk", "license-editor-settings.json");
        public static LicenseEditorSettings Load() { try { return File.Exists(SettingsPath) ? JsonConvert.DeserializeObject<LicenseEditorSettings>(File.ReadAllText(SettingsPath)) ?? new() : new(); } catch { return new(); } }
        public void Save() { Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)); File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(this, Formatting.Indented)); }
    }
}
