using System.Collections.Generic;

namespace HonestDesk.Models
{
    public class IPData
    {
        public string ClientId { get; set; }
        public string Name { get; set; }
        public string Password { get; set; }
        public string Token { get; set; }
        public string Inn { get; set; }
        public string Architecture { get; set; } = "x64";
        public bool HasLmDatabaseBackup { get; set; }
        public RuDesktopSettings RuDesktop { get; set; } = new RuDesktopSettings();
        public EngineerAccessSettings EngineerAccess { get; set; }

        public List<string> Tags { get; set; } = new List<string>();

        public VersionsData Versions { get; set; } = new VersionsData();
    }

    public class RuDesktopSettings
    {
        public bool Enabled { get; set; }
        public bool AutoOfferPasswordSetup { get; set; }
        public string Password { get; set; } = "";
    }

    public sealed class EngineerAccessSettings
    {
        public string Algorithm { get; set; } = "PBKDF2-SHA256";
        public int Iterations { get; set; }
        public string SaltBase64 { get; set; }
        public string PasswordHashBase64 { get; set; }
    }
}
