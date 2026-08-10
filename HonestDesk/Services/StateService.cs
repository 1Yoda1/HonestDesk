using System.Text.Json;
using HonestDesk.Models;

namespace HonestDesk.Services;

public sealed class StateService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public Exception? LastLoadError { get; private set; }

    public AppState Load()
    {
        LastLoadError = null;
        AppPaths.EnsureDataDirectory();
        if (!File.Exists(AppPaths.StatePath))
        {
            return new AppState();
        }

        try
        {
            var json = File.ReadAllText(AppPaths.StatePath);
            AppState state = JsonSerializer.Deserialize<AppState>(json)
                ?? throw new InvalidDataException("state.json contains no state object.");
            state.KnownUids ??= [];
            state.Requests = (state.Requests ?? [])
                .OfType<HelpRequest>()
                .Where(x => !string.IsNullOrWhiteSpace(x.Uid))
                .GroupBy(x => x.Uid, StringComparer.Ordinal)
                .Select(x => x.OrderByDescending(value => value.CreatedAt).First())
                .ToList();
            if (state.SelectedSection is not ("Support" or "Clients" or "Licenses" or "Settings" or "Journal"))
                state.SelectedSection = "Support";
            return state;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            LastLoadError = ex;
            return new AppState();
        }
    }

    public void Save(AppState state)
    {
        AppPaths.EnsureDataDirectory();
        state.KnownUids = state.KnownUids.Distinct(StringComparer.OrdinalIgnoreCase).TakeLast(1000).ToList();
        state.Requests = state.Requests
            .OrderByDescending(x => x.CreatedAt)
            .Take(200)
            .ToList();
        AtomicFile.WriteAllText(AppPaths.StatePath, JsonSerializer.Serialize(state, JsonOptions));
        LastLoadError = null;
    }
}
