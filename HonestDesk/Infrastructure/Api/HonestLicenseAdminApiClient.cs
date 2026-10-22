using System.Net;
using System.Text;
using Newtonsoft.Json;

namespace HonestDesk.Infrastructure.Api;

public sealed class HonestLicenseAdminApiClient
{
    private readonly HttpClient _http;
    private readonly Func<string> _adminKey;

    public HonestLicenseAdminApiClient(string baseUrl, Func<string> adminKey, HttpMessageHandler? handler = null)
    {
        _adminKey = adminKey ?? throw new ArgumentNullException(nameof(adminKey));
        _http = handler is null ? new HttpClient() : new HttpClient(handler, false);
        _http.BaseAddress = new Uri((string.IsNullOrWhiteSpace(baseUrl) ? "https://api.honestflow.ru/" : baseUrl).TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public Task<IReadOnlyList<AdminSupportRequest>> GetSupportRequestsAsync(string? status, CancellationToken token) => GetListAsync<AdminSupportRequest>("api/admin/support-requests" + Query("status", status), token);
    public Task<IReadOnlyList<AdminDiagnosticArchive>> GetDiagnosticArchivesAsync(CancellationToken token) =>
        GetListAsync<AdminDiagnosticArchive>("api/admin/diagnostics", token);
    public async Task<AdminDiagnosticDownload> CreateDiagnosticDownloadAsync(int id, CancellationToken token)
    {
        using HttpResponseMessage response = await SendCoreAsync(
            HttpMethod.Post, $"api/admin/diagnostics/{id}/download", null, token);
        return JsonConvert.DeserializeObject<AdminDiagnosticDownload>(await response.Content.ReadAsStringAsync(token))
            ?? throw new InvalidDataException("HonestLicenseServer returned an empty diagnostic download response.");
    }
    public Task DeleteDiagnosticArchiveAsync(int id, CancellationToken token) =>
        SendAsync(HttpMethod.Delete, $"api/admin/diagnostics/{id}", null, token);
    public async Task<AdminRemoteAccessKey?> GetRemoteAccessKeyAsync(CancellationToken token)
    {
        try
        {
            using HttpResponseMessage response = await SendCoreAsync(HttpMethod.Get, "api/admin/remote-access/key", null, token);
            return JsonConvert.DeserializeObject<AdminRemoteAccessKey>(await response.Content.ReadAsStringAsync(token));
        }
        catch (HonestLicenseAdminApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
    }
    public Task PublishRemoteAccessKeyAsync(AdminRemoteAccessKey key, CancellationToken token) =>
        SendAsync(HttpMethod.Put, "api/admin/remote-access/key", key, token);
    public async Task<AdminRemoteAccessCredential> GetRemoteAccessCredentialAsync(int requestId, CancellationToken token)
    {
        using HttpResponseMessage response = await SendCoreAsync(HttpMethod.Get,
            $"api/admin/support-requests/{requestId}/remote-access", null, token);
        return JsonConvert.DeserializeObject<AdminRemoteAccessCredential>(await response.Content.ReadAsStringAsync(token))
            ?? throw new InvalidDataException("Remote access credential response is empty.");
    }
    public Task<IReadOnlyList<AdminDeviceRequest>> GetDeviceRequestsAsync(string? status, CancellationToken token) => GetListAsync<AdminDeviceRequest>("api/admin/device-requests" + Query("status", status), token);
    public Task<IReadOnlyList<AdminDeviceRequest>> GetAllDeviceRequestsAsync(CancellationToken token) =>
        GetListAsync<AdminDeviceRequest>("api/admin/device-requests?status=", token);
    public async Task<IReadOnlyList<AdminClient>> GetClientsAsync(
        CancellationToken token,
        bool includeInactive = false)
    {
        IReadOnlyList<AdminClient> clients = await GetListAsync<AdminClient>("api/admin/clients", token);
        return includeInactive ? clients : clients.Where(x => x.IsActive).ToList();
    }
    public async Task<IReadOnlyList<AdminDevice>> GetDevicesAsync(
        string? clientId,
        CancellationToken token,
        bool includeDeleted = false)
    {
        IReadOnlyList<AdminDevice> devices = await GetListAsync<AdminDevice>(
            "api/admin/devices" + Query("clientId", clientId), token);
        return includeDeleted
            ? devices
            : devices.Where(x => !string.Equals(x.Status, "Deleted", StringComparison.OrdinalIgnoreCase)).ToList();
    }
    public Task<IReadOnlyList<AdminLicense>> GetLicensesAsync(string? clientId, CancellationToken token) => GetListAsync<AdminLicense>("api/admin/licenses" + Query("clientId", clientId), token);
    public async Task<AdminLicenseDetail> GetLicenseAsync(int id, CancellationToken token)
    {
        using HttpResponseMessage response = await SendCoreAsync(HttpMethod.Get, $"api/admin/licenses/{id}", null, token);
        return JsonConvert.DeserializeObject<AdminLicenseDetail>(await response.Content.ReadAsStringAsync(token))
            ?? throw new InvalidDataException("HonestLicenseServer returned an empty license.");
    }
    public Task<IReadOnlyList<AdminVersion>> GetVersionsAsync(CancellationToken token) => GetListAsync<AdminVersion>("api/admin/versions", token);
    public Task<IReadOnlyList<AdminAsset>> GetAssetsAsync(string? component, CancellationToken token) =>
        GetListAsync<AdminAsset>("api/admin/assets" + Query("component", component), token);
    public Task<IReadOnlyList<AdminComponentOverride>> GetComponentOverridesAsync(string clientId, CancellationToken token) =>
        GetListAsync<AdminComponentOverride>($"api/admin/clients/{Uri.EscapeDataString(clientId)}/component-versions", token);
    public async Task<AdminClientIntegrationSettings> GetClientIntegrationSettingsAsync(string clientId, CancellationToken token)
    {
        using HttpResponseMessage response = await SendCoreAsync(HttpMethod.Get,
            $"api/admin/clients/{Uri.EscapeDataString(clientId)}/integration-settings", null, token);
        return JsonConvert.DeserializeObject<AdminClientIntegrationSettings>(await response.Content.ReadAsStringAsync(token)) ?? new();
    }
    public async Task<AdminClientLicensePolicy> GetClientLicensePolicyAsync(string clientId, CancellationToken token)
    {
        using HttpResponseMessage response = await SendCoreAsync(HttpMethod.Get,
            $"api/admin/clients/{Uri.EscapeDataString(clientId)}/license-policy", null, token);
        return JsonConvert.DeserializeObject<AdminClientLicensePolicy>(await response.Content.ReadAsStringAsync(token))
            ?? throw new InvalidDataException("HonestLicenseServer returned an empty client license policy.");
    }
    public async Task<AdminServiceInstallAccessSettings> GetServiceInstallAccessAsync(CancellationToken token)
    {
        using HttpResponseMessage response = await SendCoreAsync(
            HttpMethod.Get, "api/admin/service-install-access", null, token);
        return JsonConvert.DeserializeObject<AdminServiceInstallAccessSettings>(
                   await response.Content.ReadAsStringAsync(token))
               ?? throw new InvalidDataException("HonestLicenseServer returned empty service installation settings.");
    }
    public async Task<AdminServiceInstallAccessSettings> PutServiceInstallAccessAsync(
        bool isEnabled, string? newPassword, CancellationToken token)
    {
        using HttpResponseMessage response = await SendCoreAsync(
            HttpMethod.Put, "api/admin/service-install-access",
            new { isEnabled, newPassword = string.IsNullOrWhiteSpace(newPassword) ? null : newPassword }, token);
        return JsonConvert.DeserializeObject<AdminServiceInstallAccessSettings>(
                   await response.Content.ReadAsStringAsync(token))
               ?? throw new InvalidDataException("HonestLicenseServer returned empty service installation settings.");
    }
    public Task ApproveDeviceRequestAsync(int id, string? name, string? address, string? comment, CancellationToken token) => SendAsync(HttpMethod.Put, $"api/admin/device-requests/{id}/approve", new { name, address, comment }, token);
    public Task RejectDeviceRequestAsync(int id, string? comment, CancellationToken token) => SendAsync(HttpMethod.Put, $"api/admin/device-requests/{id}/reject", new { comment }, token);
    public Task ResolveSupportRequestAsync(int id, CancellationToken token) => SendAsync(HttpMethod.Put, $"api/admin/support-requests/{id}/resolve", null, token);
    public Task UpdateClientAsync(string clientId, object request, CancellationToken token) => SendAsync(HttpMethod.Put, $"api/admin/clients/{Uri.EscapeDataString(clientId)}", request, token);
    public Task CreateClientAsync(object request, CancellationToken token) => SendAsync(HttpMethod.Post, "api/admin/clients", request, token);
    public Task UpdateDeviceAsync(int id, object request, CancellationToken token) => SendAsync(HttpMethod.Put, $"api/admin/devices/{id}", request, token);
    public Task RestoreDeviceAsync(int id, CancellationToken token) =>
        SendAsync(HttpMethod.Put, $"api/admin/devices/{id}/restore", null, token);
    public Task ReleaseDeviceIdAsync(int id, CancellationToken token) =>
        SendAsync(HttpMethod.Put, $"api/admin/devices/{id}/release", null, token);
    public Task PutVersionAsync(string application, string version, CancellationToken token) => SendAsync(HttpMethod.Put, $"api/admin/versions/{Uri.EscapeDataString(application)}", new { currentVersion = version }, token);
    public Task PutComponentOverrideAsync(string clientId, string component, string? version, CancellationToken token) => SendAsync(HttpMethod.Put, $"api/admin/clients/{Uri.EscapeDataString(clientId)}/component-versions/{Uri.EscapeDataString(component)}", new { requiredVersion = version }, token);
    public Task PutClientIntegrationSettingsAsync(
        string clientId,
        string? identificationCode,
        string? chzToken,
        bool ruDesktopEnabled,
        bool ruDesktopAutoOfferPasswordSetup,
        CancellationToken token) =>
        SendAsync(HttpMethod.Put, $"api/admin/clients/{Uri.EscapeDataString(clientId)}/integration-settings",
            new { identificationCode, chzToken, ruDesktopEnabled, ruDesktopAutoOfferPasswordSetup }, token);
    public async Task<AdminClientLicensePolicy> PutClientLicensePolicyAsync(
        string clientId, bool isEnabled, CancellationToken token)
    {
        using HttpResponseMessage response = await SendCoreAsync(HttpMethod.Put,
            $"api/admin/clients/{Uri.EscapeDataString(clientId)}/license-policy",
            new { isEnabled }, token);
        return JsonConvert.DeserializeObject<AdminClientLicensePolicy>(await response.Content.ReadAsStringAsync(token))
            ?? throw new InvalidDataException("HonestLicenseServer returned an empty client license policy.");
    }
    public Task RevokeLicenseAsync(int id, CancellationToken token) => SendAsync(HttpMethod.Put, $"api/admin/licenses/{id}/revoke", null, token);
    public Task PublishLicenseAsync(string grantBase64, string signatureBase64, string keyId, CancellationToken token) => SendAsync(HttpMethod.Post, "api/admin/licenses", new { grantBase64, signatureBase64, keyId }, token);

    private async Task<IReadOnlyList<T>> GetListAsync<T>(string path, CancellationToken token)
    {
        using HttpResponseMessage response = await SendCoreAsync(HttpMethod.Get, path, null, token);
        return JsonConvert.DeserializeObject<List<T>>(await response.Content.ReadAsStringAsync(token)) ?? [];
    }

    private async Task SendAsync(HttpMethod method, string path, object? body, CancellationToken token)
    { using HttpResponseMessage response = await SendCoreAsync(method, path, body, token); }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string path, object? body, CancellationToken token)
    {
        string key = _adminKey();
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Admin API key не настроен.");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("X-Admin-Key", key);
        if (body != null) request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
        HttpResponseMessage response = await _http.SendAsync(request, token);
        if (response.IsSuccessStatusCode) return response;
        string detail = await response.Content.ReadAsStringAsync(token);
        HttpStatusCode status = response.StatusCode;
        response.Dispose();
        throw new HonestLicenseAdminApiException(status, detail);
    }

    private static string Query(string name, string? value) => string.IsNullOrWhiteSpace(value) ? "" : "?" + name + "=" + Uri.EscapeDataString(value);
}

public sealed class HonestLicenseAdminApiException(HttpStatusCode statusCode, string responseBody) : Exception(
    $"HonestLicenseServer returned HTTP {(int)statusCode}" +
    (string.IsNullOrWhiteSpace(responseBody) ? "." : ": " + responseBody.Trim()))
{ public HttpStatusCode StatusCode { get; } = statusCode; public string ResponseBody { get; } = responseBody; }

public sealed class AdminSupportRequest { public int Id { get; set; } public string? ClientId { get; set; } public string? ExternalDeviceId { get; set; } public string? Subject { get; set; } public string? Message { get; set; } public string? Contact { get; set; } public string? HonestFlowVersion { get; set; } public string? Status { get; set; } public string? RemoteAccessCredentialId { get; set; } public string? RuDesktopId { get; set; } public DateTimeOffset CreatedAtUtc { get; set; } }
public sealed class AdminDiagnosticArchive
{
    public int Id { get; set; }
    public string? ClientId { get; set; }
    public string? ClientName { get; set; }
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string? Address { get; set; }
    public string? FileName { get; set; }
    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public string? HonestFlowVersion { get; set; }
    public string? Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? UploadedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? LastDownloadedAtUtc { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }
    public string DisplaySize => SizeBytes > 0 ? $"{SizeBytes / 1024d / 1024d:N1} МБ" : "—";
    public string DisplayStatus => Status switch
    {
        "Pending" => "Загрузка",
        "Available" => "Доступен",
        "Downloaded" => "Скачан",
        "Deleted" => "Удалён",
        "Expired" => "Удалён через 48 ч",
        _ => Status ?? "—"
    };
    public bool CanDownload => Status is "Available" or "Downloaded";
}
public sealed class AdminDiagnosticDownload
{
    public string DownloadUrl { get; set; } = "";
    public DateTimeOffset ExpiresAtUtc { get; set; }
}
public sealed class AdminRemoteAccessKey { public string KeyId { get; set; } = ""; public string PublicKeyBase64 { get; set; } = ""; public string SignatureBase64 { get; set; } = ""; public string SigningKeyId { get; set; } = ""; }
public sealed class AdminRemoteAccessCredential { public string CredentialId { get; set; } = ""; public string KeyId { get; set; } = ""; public string RuDesktopId { get; set; } = ""; public string CiphertextBase64 { get; set; } = ""; }
public sealed class AdminDeviceRequest
{
    public int Id { get; set; }
    public string? ClientId { get; set; }
    public string? ClientName { get; set; }
    [JsonProperty("clientInn")]
    public string? ClientInn { get; set; }
    public string? DeviceId { get; set; }
    public string? RequestedName { get; set; }
    [JsonProperty("requestedAddress")]
    public string? RequestedAddress { get; set; }
    [JsonProperty("honestFlowVersion")]
    public string? HonestFlowVersion { get; set; }
    public string? Status { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; }
    public DateTimeOffset? ResolvedAtUtc { get; set; }
    public string? Comment { get; set; }
}
public sealed class AdminClient { public string? ClientId { get; set; } public string? Name { get; set; } public string? Inn { get; set; } public string? Architecture { get; set; } public bool IsActive { get; set; } public bool HasLmDatabaseBackup { get; set; } public int DeviceCount { get; set; } public int ActiveDeviceCount { get; set; } public int LicenseCount { get; set; } public bool CredentialConfigured { get; set; } }
public sealed class AdminDevice { public int Id { get; set; } public string? ClientId { get; set; } public string? ClientName { get; set; } public string? DeviceId { get; set; } public string? Name { get; set; } public string? Address { get; set; } public string? Comment { get; set; } public string? Status { get; set; } public bool DeviceIdReleased { get; set; } public DateTimeOffset RegisteredAtUtc { get; set; } }
public sealed class AdminLicense { public int Id { get; set; } public string? ClientId { get; set; } public string? ClientName { get; set; } public string? DeviceId { get; set; } public long Revision { get; set; } public string? KeyId { get; set; } public string? Status { get; set; } public DateTimeOffset IssuedAtUtc { get; set; } public DateTimeOffset ValidUntilUtc { get; set; } }
public sealed class AdminLicenseDetail { public int Id { get; set; } public string? ClientId { get; set; } public string? DeviceId { get; set; } public long Revision { get; set; } public string? GrantJson { get; set; } public string? SignatureBase64 { get; set; } public string? KeyId { get; set; } public string? Status { get; set; } public DateTimeOffset IssuedAtUtc { get; set; } public DateTimeOffset ValidUntilUtc { get; set; } }
public sealed class AdminVersion { public string? Application { get; set; } public string? CurrentVersion { get; set; } public DateTimeOffset ImportedAtUtc { get; set; } }
public sealed class AdminAsset
{
    public int Id { get; set; }
    public string? Component { get; set; }
    public string? Version { get; set; }
    public string? Architecture { get; set; }
    public string? FileName { get; set; }
    public string? DownloadUrl { get; set; }
    public string? YandexPublicKey { get; set; }
    public string? YandexPath { get; set; }
    public string? Sha256 { get; set; }
    public long? SizeBytes { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string EffectiveUrl => !string.IsNullOrWhiteSpace(DownloadUrl) ? DownloadUrl : YandexPublicKey ?? "";
    public bool HasLink => !string.IsNullOrWhiteSpace(EffectiveUrl);
    public string LinkCaption => !string.IsNullOrWhiteSpace(DownloadUrl) ? "Скачать" : "Папка";
    public string DisplaySize => SizeBytes is > 0 ? $"{SizeBytes.Value / 1024d / 1024d:N1} МБ" : "—";
}
public sealed class AdminComponentOverride { public string? Component { get; set; } public string? RequiredVersion { get; set; } }
public sealed class AdminClientIntegrationSettings { public string? ClientId { get; set; } public string? IdentificationCode { get; set; } public string? ChzToken { get; set; } public bool RuDesktopEnabled { get; set; } public bool RuDesktopAutoOfferPasswordSetup { get; set; } public bool IsConfigured { get; set; } }
public sealed class AdminClientLicensePolicy { public string? ClientId { get; set; } public bool? IsEnabled { get; set; } }
public sealed class AdminServiceInstallAccessSettings
{
    public bool IsEnabled { get; set; }
    public bool HasPassword { get; set; }
    public DateTimeOffset? UpdatedAtUtc { get; set; }
}
