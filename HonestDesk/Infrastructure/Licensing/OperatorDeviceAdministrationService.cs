using HonestDesk.Infrastructure.Api;
using HonestDesk.Models.Licensing;
using Newtonsoft.Json;

namespace HonestDesk.Infrastructure.Licensing;

public sealed class PublishedOperatorDevice
{
    public int LicenseId { get; init; }
    public int DeviceDatabaseId { get; init; }
    public string ClientId { get; init; } = "";
    public string ClientName { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public string? Address { get; init; }
    public string? Comment { get; init; }
    public string DeviceStatus { get; init; } = "";
    public bool DeviceIdReleased { get; init; }
    public bool GrantDeviceEnabled { get; init; }
    public string LicenseStatus { get; init; } = "";
    public long Revision { get; init; }
    public DateTimeOffset RegisteredAtUtc { get; init; }
    public DateTimeOffset IssuedAtUtc { get; init; }
    public DateTimeOffset ValidUntilUtc { get; init; }

    public bool CanRevoke => string.Equals(LicenseStatus, "Active", StringComparison.OrdinalIgnoreCase);
    public bool CanDisable => CanRevoke && !DeviceIdReleased &&
                              string.Equals(DeviceStatus, "Active", StringComparison.OrdinalIgnoreCase);
    public bool CanEnable => CanRevoke && !DeviceIdReleased &&
                             string.Equals(DeviceStatus, "Disabled", StringComparison.OrdinalIgnoreCase);
}

public sealed class OperatorDeviceAdministrationService
{
    private readonly HonestLicenseAdminApiClient _api;

    public OperatorDeviceAdministrationService(HonestLicenseAdminApiClient api) => _api = api;

    public async Task<IReadOnlyList<PublishedOperatorDevice>> GetAsync(CancellationToken token)
    {
        Task<IReadOnlyList<AdminLicense>> licensesTask = _api.GetLicensesAsync(null, token);
        Task<IReadOnlyList<AdminDevice>> devicesTask = _api.GetDevicesAsync(null, token, includeDeleted: true);
        await Task.WhenAll(licensesTask, devicesTask);

        IReadOnlyList<AdminLicense> licenses = await licensesTask;
        IReadOnlyList<AdminDevice> devices = await devicesTask;
        var result = new List<PublishedOperatorDevice>();
        foreach (AdminLicense license in licenses.OrderByDescending(x => x.Revision).ThenByDescending(x => x.Id))
        {
            AdminLicenseDetail detail = await _api.GetLicenseAsync(license.Id, token);
            if (string.IsNullOrWhiteSpace(detail.GrantJson)) continue;
            LicenseGrant? grant = JsonConvert.DeserializeObject<LicenseGrant>(detail.GrantJson);
            if (grant?.OperatorDevice != true) continue;

            AdminDevice? device = FindDevice(devices, license.ClientId, license.DeviceId);
            result.Add(new PublishedOperatorDevice
            {
                LicenseId = license.Id,
                DeviceDatabaseId = device?.Id ?? 0,
                ClientId = license.ClientId ?? grant.ClientId ?? "",
                ClientName = license.ClientName ?? device?.ClientName ?? license.ClientId ?? "",
                DeviceId = license.DeviceId ?? grant.DeviceId ?? "",
                DeviceName = device?.Name ?? license.DeviceId ?? grant.DeviceId ?? "",
                Address = device?.Address,
                Comment = device?.Comment,
                DeviceStatus = device?.Status ?? "Не найдено",
                DeviceIdReleased = device?.DeviceIdReleased == true,
                GrantDeviceEnabled = grant.DeviceEnabled,
                LicenseStatus = license.Status ?? detail.Status ?? "",
                Revision = license.Revision,
                RegisteredAtUtc = device?.RegisteredAtUtc ?? default,
                IssuedAtUtc = license.IssuedAtUtc,
                ValidUntilUtc = license.ValidUntilUtc
            });
        }
        return result;
    }

    public Task RevokeAsync(PublishedOperatorDevice item, CancellationToken token)
    {
        if (!item.CanRevoke)
            throw new InvalidOperationException("Отозвать можно только действующую операторскую лицензию.");
        return _api.RevokeLicenseAsync(item.LicenseId, token);
    }

    public Task SetDeviceEnabledAsync(PublishedOperatorDevice item, bool enabled, CancellationToken token)
    {
        if (item.DeviceDatabaseId <= 0 || item.DeviceIdReleased ||
            (enabled ? !item.CanEnable : !item.CanDisable))
            throw new InvalidOperationException("Выбранное операторское устройство нельзя перевести в это состояние.");

        return _api.UpdateDeviceAsync(item.DeviceDatabaseId, new
        {
            name = item.DeviceName,
            address = item.Address,
            comment = item.Comment,
            status = enabled ? "Active" : "Disabled"
        }, token);
    }

    private static AdminDevice? FindDevice(
        IEnumerable<AdminDevice> devices, string? clientId, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(deviceId)) return null;
        AdminDevice? current = AdminLifecycleVisibility.FindCurrentBoundDevice(devices, clientId, deviceId);
        return current ?? devices
            .Where(x => x.DeviceIdReleased &&
                        string.Equals(x.ClientId, clientId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(x.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Id)
            .FirstOrDefault();
    }
}
