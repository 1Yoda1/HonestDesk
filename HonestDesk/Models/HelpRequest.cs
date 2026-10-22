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
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public bool IsHandled { get; set; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(ClientName) ? Subject : ClientName;
}
