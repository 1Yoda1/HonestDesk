namespace HonestDesk.Models;

public sealed class HelpRequest
{
    public string Uid { get; set; } = "";
    public string MessageId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string ClientName { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string InnMasked { get; set; } = "";
    public string Address { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string WindowsUser { get; set; } = "";
    public string RuDesktopId { get; set; } = "";
    public string RemoteAccessCredentialId { get; set; } = "";
    public string ServerStatus { get; set; } = "";
    public string HonestFlowVersion { get; set; } = "";
    public string ProblemType { get; set; } = "";
    public string Message { get; set; } = "";
    public string Contact { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public bool IsHandled { get; set; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(ClientName) ? Subject : ClientName;
    public string MessagePreview
    {
        get
        {
            string normalized = string.Join(" ", Message.Split(
                ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return normalized.Length <= 120 ? normalized : normalized[..117] + "…";
        }
    }
    public string StatusText => ServerStatus.ToLowerInvariant() switch
    {
        "accepted" or "open" => "Ожидает",
        "resolved" or "closed" => "Выполнено",
        "cancelled" => "Отменено",
        _ => IsHandled ? "Обработано" : "Ожидает"
    };
}
