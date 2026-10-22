using HonestDesk.Infrastructure.Api;

namespace HonestDesk.Infrastructure.Licensing;

public sealed class LicenseRevocationService
{
    private readonly HonestLicenseAdminApiClient _api;

    public LicenseRevocationService(HonestLicenseAdminApiClient api) => _api = api;

    public async Task<IReadOnlyList<AdminLicense>> GetActiveClientLicensesAsync(
        string clientId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("ClientId is required.", nameof(clientId));

        return (await _api.GetLicensesAsync(clientId, token))
            .Where(x => string.Equals(x.ClientId, clientId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(x.Status, "Active", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Revision)
            .ThenByDescending(x => x.Id)
            .ToArray();
    }

    public Task RevokeAsync(AdminLicense license, CancellationToken token)
    {
        if (!string.Equals(license.Status, "Active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Отозвать можно только действующую лицензию.");
        return _api.RevokeLicenseAsync(license.Id, token);
    }
}
