namespace HonestDesk.Infrastructure.Api;

public static class AdminLifecycleVisibility
{
    public static bool ClientIsVisible(AdminClient client, bool showInactive) =>
        showInactive || client.IsActive;

    public static bool DeviceIsVisible(AdminDevice device, bool showDeleted) =>
        showDeleted || !string.Equals(device.Status, "Deleted", StringComparison.OrdinalIgnoreCase);

    public static AdminDevice? FindCurrentBoundDevice(
        IEnumerable<AdminDevice> devices, string clientId, string externalDeviceId)
    {
        AdminDevice[] matches = devices.Where(x =>
            !x.DeviceIdReleased &&
            string.Equals(x.ClientId, clientId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.DeviceId, externalDeviceId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1)
        {
            throw new InvalidDataException(
                $"HonestLicenseServer returned multiple current Device rows for ClientId={clientId}, " +
                $"DeviceId={externalDeviceId}. Delete was cancelled; check the server invariant.");
        }
        return matches.Length == 0 ? null : matches[0];
    }
}
