using HonestDesk.Models;

namespace HonestDesk.Services;

public sealed class MailPollResult
{
    public bool Success { get; init; }
    public string Status { get; init; } = "";
    public List<HelpRequest> NewRequests { get; init; } = [];
}
