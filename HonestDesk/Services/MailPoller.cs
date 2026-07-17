using HonestDesk.Models;
using HonestDesk.Parsing;

namespace HonestDesk.Services;

public sealed class MailPoller
{
    private const string HelpSubjectPrefix = "HF_HELP";
    private const string InboxFolder = "INBOX";
    private const int RecentMailboxUidLimit = 100;
    private const int AlwaysRecheckRecentUidLimit = 20;
    private readonly AppLogger _logger;

    public MailPoller(AppLogger logger)
    {
        _logger = logger;
    }

    public async Task<MailPollResult> CheckAsync(AppSettings settings, AppState state, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Email))
        {
            return new MailPollResult { Success = false, Status = "Не указан email в настройках." };
        }

        var password = DpapiProtector.Unprotect(settings.ProtectedAppPassword);
        if (string.IsNullOrWhiteSpace(password))
        {
            return new MailPollResult { Success = false, Status = "Не указан пароль приложения Яндекс." };
        }

        try
        {
            using var client = new SimpleImapClient(settings.ImapServer, settings.ImapPort, settings.UseSsl);
            await client.ConnectAsync(cancellationToken);
            await client.LoginAsync(settings.Email, password, cancellationToken);
            await client.NoOpAsync(cancellationToken);
            var availableFolders = await client.ListFoldersAsync(cancellationToken);
            var folders = BuildFoldersToCheck(settings.MailFolder, availableFolders);
            var changedRequests = new List<HelpRequest>();
            var statusParts = new List<string>();

            foreach (var folder in folders)
            {
                if (!await TrySelectFolderAsync(client, folder, cancellationToken))
                {
                    statusParts.Add($"папка '{folder}' недоступна");
                    continue;
                }

                await client.NoOpAsync(cancellationToken);
                var folderChanged = await CheckSelectedFolderAsync(client, folder, state, cancellationToken);
                changedRequests.AddRange(folderChanged);
                statusParts.Add($"{folder}: {folderChanged.Count}");
            }

            await client.LogoutAsync(cancellationToken);
            _logger.Info($"Mail check successful. Changed requests: {changedRequests.Count}.");

            return new MailPollResult
            {
                Success = true,
                Status = $"Проверено: {string.Join("; ", statusParts)}. Новых/исправленных заявок: {changedRequests.Count}.",
                NewRequests = changedRequests
            };
        }
        catch (Exception ex)
        {
            _logger.Error("Mail check failed", ex);
            return new MailPollResult { Success = false, Status = $"Ошибка проверки почты: {ex.Message}" };
        }
    }

    private async Task<List<HelpRequest>> CheckSelectedFolderAsync(SimpleImapClient client, string folder, AppState state, CancellationToken cancellationToken)
    {
        var known = state.KnownUidSet;
        var firstSearch = await client.SearchAllUidsAsync(cancellationToken);
        await client.NoOpAsync(cancellationToken);
        var secondSearch = await client.SearchAllUidsAsync(cancellationToken);
        var uids = firstSearch
            .Concat(secondSearch)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var recentUids = uids
            .Select(uid => new MailUid(uid, ParseUidNumber(uid)))
            .OrderByDescending(uid => uid.Number)
            .Take(RecentMailboxUidLimit)
            .ToList();

        var alwaysRecheck = recentUids
            .Take(AlwaysRecheckRecentUidLimit)
            .Select(uid => uid.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidateUids = recentUids
            .OrderBy(uid => uid.Number)
            .Where(uid => alwaysRecheck.Contains(uid.Value) || ShouldFetch(state, known, BuildKnownUid(folder, uid.Value), uid.Value))
            .ToList();

        var changedRequests = new List<HelpRequest>();

        foreach (var uid in candidateUids)
        {
            var requestUid = BuildKnownUid(folder, uid.Value);
            var headers = await client.FetchHeadersAsync(uid.Value, cancellationToken);
            if (!headers.Subject.StartsWith(HelpSubjectPrefix, StringComparison.OrdinalIgnoreCase))
            {
                AddKnownUid(state, requestUid);
                continue;
            }

            var raw = await client.FetchMessageAsync(uid.Value, cancellationToken);
            var request = HelpRequestParser.Parse(requestUid, raw.MessageId, raw.Subject, raw.Body);
            if (UpsertRequest(state, request))
            {
                changedRequests.Add(request);
            }

            AddKnownUid(state, request.Uid);
        }

        return changedRequests;
    }

    private async Task<bool> TrySelectFolderAsync(SimpleImapClient client, string folder, CancellationToken cancellationToken)
    {
        try
        {
            await client.SelectFolderAsync(folder, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Folder '{folder}' is not available", ex);
            return false;
        }
    }

    private static List<string> BuildFoldersToCheck(string configuredFolder, IReadOnlyList<string> availableFolders)
    {
        var folders = new List<string> { InboxFolder };
        var folder = string.IsNullOrWhiteSpace(configuredFolder) ? InboxFolder : configuredFolder.Trim();
        AddFolder(folders, folder);

        foreach (var availableFolder in availableFolders)
        {
            if (availableFolder.Contains("Honest", StringComparison.OrdinalIgnoreCase)
                || availableFolder.Contains("Flow", StringComparison.OrdinalIgnoreCase)
                || availableFolder.Contains("Help", StringComparison.OrdinalIgnoreCase))
            {
                AddFolder(folders, availableFolder);
            }
        }

        return folders;
    }

    private static void AddFolder(List<string> folders, string folder)
    {
        if (!string.IsNullOrWhiteSpace(folder) && !folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            folders.Add(folder);
        }
    }

    private static bool ShouldFetch(AppState state, HashSet<string> known, string scopedUid, string legacyUid)
    {
        var existing = state.Requests.FirstOrDefault(x =>
            x.Uid.Equals(scopedUid, StringComparison.OrdinalIgnoreCase) ||
            x.Uid.Equals(legacyUid, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            return IsIncomplete(existing);
        }

        if (known.Contains(scopedUid) || known.Contains(legacyUid))
        {
            return state.Requests.Count == 0;
        }

        return true;
    }

    private static bool IsIncomplete(HelpRequest request)
    {
        return string.IsNullOrWhiteSpace(request.ClientName)
            || string.IsNullOrWhiteSpace(request.RuDesktopId)
            || string.IsNullOrWhiteSpace(request.ProblemType);
    }

    private static bool UpsertRequest(AppState state, HelpRequest request)
    {
        var existingIndex = state.Requests.FindIndex(x =>
            x.Uid.Equals(request.Uid, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(request.MessageId) && x.MessageId.Equals(request.MessageId, StringComparison.OrdinalIgnoreCase)));

        if (existingIndex >= 0)
        {
            var existing = state.Requests[existingIndex];
            var hasNewAddress = string.IsNullOrWhiteSpace(existing.Address)
                && !string.IsNullOrWhiteSpace(request.Address);
            if (!IsIncomplete(existing) && !hasNewAddress)
            {
                return false;
            }

            request.IsHandled = existing.IsHandled;
            state.Requests[existingIndex] = request;
            return true;
        }

        state.Requests.Insert(0, request);
        return true;
    }

    private static void AddKnownUid(AppState state, string uid)
    {
        if (!state.KnownUids.Contains(uid, StringComparer.OrdinalIgnoreCase))
        {
            state.KnownUids.Add(uid);
        }
    }

    private static long ParseUidNumber(string uid)
    {
        return long.TryParse(uid, out var value) ? value : 0;
    }

    private static string BuildKnownUid(string folder, string uid) => $"{folder}:{uid}";

    private sealed record MailUid(string Value, long Number);
}








