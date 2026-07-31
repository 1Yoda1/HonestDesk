using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HonestDesk.Models;

namespace HonestDesk.Infrastructure.Licensing
{
    public interface IDeviceRegistrationInbox
    {
        Task<DeviceRegistrationInboxResult> ReadPendingAsync(
            SupportMailSettings settings,
            CancellationToken cancellationToken);

        Task MarkProcessedAsync(
            SupportMailSettings settings,
            IReadOnlyCollection<uint> messageUids,
            CancellationToken cancellationToken);
    }
}
