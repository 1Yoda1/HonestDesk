namespace HonestDesk.Infrastructure.Licensing
{
    public sealed class DeviceRegistrationBatchItem
    {
        public DeviceRegistrationInboxItem InboxItem { get; set; } = new();
        public DeviceRegistrationBatchStatus Status { get; set; }
        public string ExistingOwnerClientId { get; set; } = string.Empty;
    }
}
