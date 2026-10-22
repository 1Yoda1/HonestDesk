using System;
using System.Collections.Generic;
using System.Linq;

namespace HonestDesk.Infrastructure.Licensing
{
    public sealed class DeviceRegistrationBatchPlan
    {
        public IReadOnlyCollection<DeviceRegistrationBatchItem> Items { get; set; } =
            Array.Empty<DeviceRegistrationBatchItem>();

        public int NewDeviceCount => Items.Count(item =>
            item.Status == DeviceRegistrationBatchStatus.NewDevice);

        public int AddressUpdateCount => Items.Count(item =>
            item.Status == DeviceRegistrationBatchStatus.AddressUpdate);

        public int ChangeCount => NewDeviceCount + AddressUpdateCount;

        public IReadOnlyCollection<uint> ProcessedMessageUids => Items
            .Where(item =>
                item.Status == DeviceRegistrationBatchStatus.NewDevice ||
                item.Status == DeviceRegistrationBatchStatus.AddressUpdate ||
                item.Status == DeviceRegistrationBatchStatus.DuplicateRequest ||
                item.Status == DeviceRegistrationBatchStatus.AlreadyRegistered)
            .Select(item => item.InboxItem.MessageUid)
            .Distinct()
            .ToArray();
    }
}
