using System.Collections.Generic;

namespace HonestDesk.Models
{
    public class ConfigData
    {
        public int ConfigVersion { get; set; } = 1;
        public string LastUpdated { get; set; } = string.Empty;
        public VersionsData Versions { get; set; } = new();
        public List<IPData> Ips { get; set; } = new();
    }

    public class VersionsData
    {
        // A missing value means that the client has no component-version override.
        public string? LmModule { get; set; }
        public string? AtolDriver { get; set; }
        public string? ESM { get; set; }
        public string? Controller { get; set; }
        public string? HonestFlow { get; set; }
    }
}
