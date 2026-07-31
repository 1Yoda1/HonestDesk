using System;
using System.Collections.Generic;
using System.Linq;
using HonestDesk.Models.Licensing;

namespace HonestDesk.Infrastructure.Licensing
{
    public sealed class ClientLicenseProvisioningService
    {
        public const string DefaultMinHonestFlowVersion = "2.4.2.0";
        public const int DefaultOfflineGraceHours = 72;

        public int AddMissingClients(
            LicenseManifest manifest,
            IEnumerable<string> clientIds,
            string minHonestFlowVersion = DefaultMinHonestFlowVersion,
            int offlineGraceHours = DefaultOfflineGraceHours)
        {
            if (manifest == null)
                throw new ArgumentNullException(nameof(manifest));
            if (!Version.TryParse(minHonestFlowVersion, out _))
                throw new ArgumentException("Некорректная минимальная версия HonestFlow.", nameof(minHonestFlowVersion));
            if (offlineGraceHours < 0)
                throw new ArgumentOutOfRangeException(nameof(offlineGraceHours));

            manifest.Clients ??= new List<ClientLicense>();
            var existingClientIds = manifest.Clients
                .Where(client => !string.IsNullOrWhiteSpace(client.ClientId))
                .Select(client => client.ClientId.Trim())
                .ToList();

            if (existingClientIds.Distinct(StringComparer.Ordinal).Count() != existingClientIds.Count)
                throw new InvalidOperationException("В manifest уже есть повторяющиеся ClientId.");

            var existing = new HashSet<string>(existingClientIds, StringComparer.Ordinal);
            var missing = (clientIds ?? Array.Empty<string>())
                .Where(clientId => !string.IsNullOrWhiteSpace(clientId))
                .Select(clientId => clientId.Trim())
                .Distinct(StringComparer.Ordinal)
                .Where(clientId => !existing.Contains(clientId))
                .ToList();

            foreach (string clientId in missing)
            {
                manifest.Clients.Add(new ClientLicense
                {
                    ClientId = clientId,
                    Enabled = true,
                    MinHonestFlowVersion = minHonestFlowVersion,
                    OfflineGraceHours = offlineGraceHours,
                    Features = LicenseFeatureCatalog.ConfigurableFeatures.ToList(),
                    Devices = new List<LicensedDevice>()
                });
            }

            return missing.Count;
        }
    }
}
