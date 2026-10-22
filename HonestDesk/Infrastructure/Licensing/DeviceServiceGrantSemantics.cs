using HonestDesk.Models.Licensing;

namespace HonestDesk.Infrastructure.Licensing;

public static class DeviceServiceGrantSemantics
{
    public static List<LicenseFeature> CreateDeviceFeatures(
        IEnumerable<LicenseFeature>? legacyFeatures,
        bool serviceEnabled)
    {
        var result = (legacyFeatures ?? [])
            .Where(feature => feature != LicenseFeature.Service)
            .Distinct()
            .ToList();
        if (serviceEnabled)
            result.Add(LicenseFeature.Service);
        return result;
    }

    public static DateTimeOffset ResolveValidUntilUtc(
        DateTimeOffset legacyValidUntilUtc,
        bool serviceEnabled,
        DateTimeOffset? serviceValidUntilUtc,
        DateTimeOffset nowUtc)
    {
        if (!serviceEnabled)
            return legacyValidUntilUtc;

        if (serviceValidUntilUtc is not { } validUntil || validUntil <= nowUtc)
            throw new InvalidDataException(
                "Для HonestFlow Service укажите будущую дату окончания доступа.");

        return validUntil.ToUniversalTime();
    }

    public static void RestorePublishedState(LicensedDevice device, LicenseGrant grant)
    {
        device.ServiceEnabled = !grant.OperatorDevice &&
            (grant.Features ?? []).Contains(LicenseFeature.Service);
        device.ServiceValidUntilUtc = device.ServiceEnabled ? grant.ValidUntilUtc : null;
    }
}