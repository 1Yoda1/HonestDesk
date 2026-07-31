using System.Collections.Generic;

namespace HonestDesk.Models
{
    public class ConfigData
    {
        public int ConfigVersion { get; set; } = 1;
        public string LastUpdated { get; set; }
        public VersionsData Versions { get; set; }
        public List<IPData> Ips { get; set; }
    }

    public class VersionsData
    {
        public string LmModule { get; set; }
        public string AtolDriver { get; set; }
        public string ESM { get; set; }
        public string Controller { get; set; }
        public string HonestFlow { get; set; }
    }
}
