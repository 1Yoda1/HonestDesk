using System.Text.Json;
using HonestDesk.Models;

namespace HonestDesk.Services;

public sealed class MaxInboxImportService(AppLogger logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static string InboxDirectory => Path.Combine(AppPaths.DataDirectory, "max-inbox");
    private static string ProcessedDirectory => Path.Combine(InboxDirectory, "processed");

    public IReadOnlyList<HelpRequest> Import(AppState state)
    {
        Directory.CreateDirectory(InboxDirectory);
        Directory.CreateDirectory(ProcessedDirectory);

        var imported = new List<HelpRequest>();
        var knownUids = state.KnownUidSet;

        foreach (var path in Directory.EnumerateFiles(InboxDirectory, "*.json"))
        {
            try
            {
                var request = JsonSerializer.Deserialize<HelpRequest>(File.ReadAllText(path), JsonOptions);
                if (request is null || string.IsNullOrWhiteSpace(request.Uid))
                {
                    logger.Error($"Invalid MAX request file: {path}");
                    continue;
                }

                if (!knownUids.Contains(request.Uid))
                {
                    state.Requests.Add(request);
                    state.KnownUids.Add(request.Uid);
                    knownUids.Add(request.Uid);
                    imported.Add(request);
                    logger.Info($"MAX request imported: {request.Uid}");
                }

                var destination = Path.Combine(ProcessedDirectory, Path.GetFileName(path));
                File.Move(path, destination, overwrite: true);
            }
            catch (Exception exception)
            {
                logger.Error($"Cannot import MAX request {path}: {exception.Message}");
            }
        }

        return imported;
    }
}
