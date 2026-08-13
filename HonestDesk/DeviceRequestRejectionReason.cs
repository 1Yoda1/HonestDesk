namespace HonestDesk;

public static class DeviceRequestRejectionReason
{
    public const string Fallback = "Заявка отклонена оператором.";

    public static string Normalize(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? Fallback : reason.Trim();
}
