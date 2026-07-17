using System.Text.Json;
using HonestDesk.Models;

namespace HonestDesk.Services;

public sealed class StateService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AppState Load()
    {
        AppPaths.EnsureDataDirectory();
        if (!File.Exists(AppPaths.StatePath))
        {
            return new AppState();
        }

        var json = File.ReadAllText(AppPaths.StatePath);
        return JsonSerializer.Deserialize<AppState>(json) ?? new AppState();
    }

    public void Save(AppState state)
    {
        AppPaths.EnsureDataDirectory();
        state.KnownUids = state.KnownUids.Distinct(StringComparer.OrdinalIgnoreCase).TakeLast(1000).ToList();
        state.Requests = state.Requests
            .OrderByDescending(x => x.CreatedAt)
            .Take(200)
            .ToList();
        File.WriteAllText(AppPaths.StatePath, JsonSerializer.Serialize(state, JsonOptions));
    }
}
