using System;

namespace HonestDesk.Models.Licensing
{
    public sealed class DeviceRegistrationRequest
    {
        public int SchemaVersion { get; set; }
        public string ClientId { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string HonestFlowVersion { get; set; } = string.Empty;
        public DateTimeOffset RequestedAtUtc { get; set; }
    }
}
