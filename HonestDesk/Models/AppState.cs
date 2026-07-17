using System.Text.Json.Serialization;

namespace HonestDesk.Models;

public sealed class AppState
{
    public List<string> KnownUids { get; set; } = [];
    public List<HelpRequest> Requests { get; set; } = [];

    [JsonIgnore]
    public HashSet<string> KnownUidSet => KnownUids.ToHashSet(StringComparer.OrdinalIgnoreCase);
}
