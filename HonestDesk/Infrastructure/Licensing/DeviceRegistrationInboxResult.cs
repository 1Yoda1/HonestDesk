using System;
using System.Collections.Generic;

namespace HonestDesk.Infrastructure.Licensing
{
    public sealed class DeviceRegistrationInboxResult
    {
        public IReadOnlyCollection<DeviceRegistrationInboxItem> Items { get; set; } =
            Array.Empty<DeviceRegistrationInboxItem>();
        public int InvalidMessageCount { get; set; }
    }
}
