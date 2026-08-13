using HonestDesk.Models.Licensing;

namespace HonestDesk.Infrastructure.Licensing
{
    public sealed class DeviceRegistrationInboxItem
    {
        public uint MessageUid { get; set; }
        public DeviceRegistrationRequest Request { get; set; } = new();
    }
}
