using System;

namespace HonestDesk.Models.Licensing
{
    public sealed class DeviceRegistrationRequest
    {
        public int SchemaVersion { get; set; }
        public string ClientId { get; set; }
        public string DeviceId { get; set; }
        public string DeviceName { get; set; }
        public string Address { get; set; }
        public string HonestFlowVersion { get; set; }
        public DateTimeOffset RequestedAtUtc { get; set; }
    }
}
