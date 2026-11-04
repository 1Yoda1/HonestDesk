namespace HonestDesk.Infrastructure.Api;

public sealed class AdminConnectionRequest
{
    public int Id { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string ContactName { get; set; } = "";
    public string? Company { get; set; }
    public string Phone { get; set; } = "";
    public string? Email { get; set; }
    public string? City { get; set; }
    public int WorkplaceCount { get; set; }
    public string? InventorySystem { get; set; }
    public string? Comment { get; set; }
    public string? Source { get; set; }
    public string Status { get; set; } = "";
    public DateTimeOffset CreatedAtLocal => CreatedAtUtc.ToLocalTime();
    public string StatusText => Status switch
    {
        "New" => "Новая",
        "InProgress" => "В работе",
        "Completed" => "Завершена",
        "Rejected" => "Отклонена",
        _ => Status
    };
}
