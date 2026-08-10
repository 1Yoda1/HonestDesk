using System.Text;
using HonestDesk.Infrastructure.Api;
using HonestDesk.Models.Licensing;
using Newtonsoft.Json;

namespace HonestDesk.Infrastructure.Licensing;

public sealed record DeviceLicensePolicy(
    bool ViewAndRepair,
    bool InstallAndMaintenance,
    string MinimumHonestFlowVersion,
    int OfflineGraceHours,
    int ValidityDays);

public sealed class LicensePolicyRequiredException : Exception
{
    public LicensePolicyRequiredException() : base("У клиента ещё нет лицензии; параметры нужно задать вручную.") { }
}

public sealed class ServerDeviceLicenseService
{
    private readonly HonestLicenseAdminApiClient _api;
    private readonly LicenseGrantPublicationBuilder _builder = new();

    public ServerDeviceLicenseService(HonestLicenseAdminApiClient api) => _api = api;

    public async Task<long> IssueAsync(string clientId, string deviceId, DeviceLicensePolicy? fallback, CancellationToken token)
    {
        LicenseEditorSettings signing = LicenseEditorSettings.Load();
        if (string.IsNullOrWhiteSpace(signing.PrivateKeyPath) || !File.Exists(signing.PrivateKeyPath))
            throw new InvalidOperationException("В разделе лицензий не настроен закрытый ключ подписи.");

        IReadOnlyList<AdminLicense> allLicenses = await _api.GetLicensesAsync(null, token);
        AdminLicense[] clientLicenses = allLicenses
            .Where(x => string.Equals(x.ClientId, clientId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(x.Status, "Active", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Revision).ThenByDescending(x => x.Id).ToArray();

        LicenseGrant? template = null;
        foreach (AdminLicense item in clientLicenses)
        {
            AdminLicenseDetail detail = await _api.GetLicenseAsync(item.Id, token);
            if (string.IsNullOrWhiteSpace(detail.GrantJson)) continue;
            LicenseGrant? candidate = JsonConvert.DeserializeObject<LicenseGrant>(detail.GrantJson);
            if (candidate is not null && !candidate.OperatorDevice) { template = candidate; break; }
        }
        if (template is null && fallback is null) throw new LicensePolicyRequiredException();

        IReadOnlyList<AdminDevice> devices = await _api.GetDevicesAsync(clientId, token);
        AdminDevice device = devices.FirstOrDefault(x => string.Equals(x.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Одобренное устройство не найдено на сервере.");
        long revision = allLicenses.Count == 0 ? 1 : checked(allLicenses.Max(x => x.Revision) + 1);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<LicenseFeature> features = template?.Features?.Distinct().ToList() ?? [];
        if (template is null && fallback is not null)
        {
            if (fallback.ViewAndRepair) features.Add(LicenseFeature.ViewAndRepair);
            if (fallback.InstallAndMaintenance) features.Add(LicenseFeature.InstallAndMaintenance);
        }
        var manifest = new LicenseManifest
        {
            Revision = revision,
            IssuedAtUtc = now,
            ValidUntilUtc = template is not null && template.ValidUntilUtc > now
                ? template.ValidUntilUtc : now.AddDays(fallback?.ValidityDays ?? signing.ValidityDays),
            Clients =
            [
                new ClientLicense
                {
                    ClientId = clientId,
                    Enabled = template?.ClientEnabled ?? true,
                    MinHonestFlowVersion = template?.MinHonestFlowVersion ?? fallback!.MinimumHonestFlowVersion,
                    OfflineGraceHours = template?.OfflineGraceHours ?? fallback!.OfflineGraceHours,
                    Features = features,
                    Devices = [new LicensedDevice { DeviceId = deviceId, Name = device.Name ?? deviceId, Address = device.Address ?? "", Comment = device.Comment ?? "", Enabled = true }]
                }
            ]
        };
        LicenseGrantPublication publication = _builder.Build(manifest, signing.KeyId, signing.PrivateKeyPath).Single();
        LicenseSignatureEnvelope envelope = JsonConvert.DeserializeObject<LicenseSignatureEnvelope>(Encoding.UTF8.GetString(publication.SignatureBytes))
            ?? throw new InvalidDataException("Не удалось прочитать подпись лицензии.");
        await _api.PublishLicenseAsync(Convert.ToBase64String(publication.GrantBytes), envelope.Signature, envelope.KeyId, token);
        return revision;
    }
}
