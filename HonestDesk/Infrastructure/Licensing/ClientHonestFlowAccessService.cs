using HonestDesk.Infrastructure.Api;

namespace HonestDesk.Infrastructure.Licensing;

public sealed class ClientHonestFlowAccessService(HonestLicenseAdminApiClient api)
{
    public Task<AdminClientLicensePolicy> GetAsync(string clientId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("ClientId is required.", nameof(clientId));
        return api.GetClientLicensePolicyAsync(clientId, token);
    }

    public Task<AdminClientLicensePolicy> SetAsync(
        string clientId, bool isEnabled, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("ClientId is required.", nameof(clientId));
        return api.PutClientLicensePolicyAsync(clientId, isEnabled, token);
    }
}

public static class ClientLicensePolicyGrantSemantics
{
    public static bool ToClientEnabled(bool? policyEnabled) => policyEnabled != false;

    public static bool ToClientEnabled(
        IReadOnlyDictionary<string, bool?> policies,
        string clientId)
    {
        if (policies is null)
            throw new ArgumentNullException(nameof(policies));
        return policies.TryGetValue(clientId, out bool? enabled)
            ? ToClientEnabled(enabled)
            : ToClientEnabled(null);
    }
}
