namespace HonestDesk.Models;

public sealed class AppSettings
{
    public string ApiBaseUrl { get; set; } = "https://api.honestflow.ru/";
    public string ProtectedAdminApiKey { get; set; } = "";
    public string ImapServer { get; set; } = "imap.yandex.ru";
    public int ImapPort { get; set; } = 993;
    public bool UseSsl { get; set; } = true;
    public string Email { get; set; } = "";
    public string ProtectedAppPassword { get; set; } = "";
    public string MailFolder { get; set; } = "HonestFlow Help";
    public string RegistrationEmail { get; set; } = "";
    public string ProtectedRegistrationAppPassword { get; set; } = "";
    public int CheckIntervalSeconds { get; set; } = 30;
    public string RuDesktopPath { get; set; } = "";
    public bool StartWithWindows { get; set; }
}
