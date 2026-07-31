using System.Collections.Generic;

namespace HonestDesk.Models
{
    public class AvailableVersionsData
    {
        public List<string> LmModule { get; set; } = new List<string>();
        public List<string> AtolDriver { get; set; } = new List<string>();
        public List<string> ESM { get; set; } = new List<string>();
        public List<string> Controller { get; set; } = new List<string>();
        public List<string> HonestFlow { get; set; } = new List<string>();
    }
}
