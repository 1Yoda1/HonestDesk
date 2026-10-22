using System.Net;
using System.Text;
using System.Text.Json;
using HonestDesk.Models;

namespace HonestDesk.Services;

public sealed class RemoteClientConfigService
{
    private const string PublicKey = "https://disk.360.yandex.ru/d/sngNP8yBz9weWA";
    private const string DownloadApi = "https://cloud-api.yandex.net/v1/disk/public/resources/download";
    private const int MaxResponseBytes = 5 * 1024 * 1024;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private readonly AppLogger _logger;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private IReadOnlyList<ClientConfiguration>? _cachedClients;
    private DateTimeOffset _cachedAtUtc;

    public RemoteClientConfigService(AppLogger logger, HttpMessageHandler? handler = null)
    {
        _logger = logger;
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("HonestDesk/1.0");
    }

    public async Task<ClientPasswordResult> GetRuDesktopPasswordAsync(
        string clientName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientName))
            return ClientPasswordResult.Failed("В заявке не указано имя клиента.");

        try
        {
            ClientMatchResult match = await FindClientAsync("", clientName, "", cancellationToken);
            if (!match.Success)
                return ClientPasswordResult.Failed(match.Error);

            string password = match.Client!.RuDesktop.Password?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(password))
                return ClientPasswordResult.Failed($"Для клиента «{clientName}» не задан пароль RuDesktop.");

            return ClientPasswordResult.Succeeded(password);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Error("Timed out while reading remote client configuration");
            return ClientPasswordResult.Failed("Истекло время получения ips_encrypted.json.");
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to read RuDesktop password from remote client configuration", ex);
            return ClientPasswordResult.Failed("Не удалось получить настройки клиента: " + ex.Message);
        }
    }

    public async Task<ClientMatchResult> FindClientAsync(
        string? clientId,
        string? clientName,
        string? inn,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<ClientConfiguration> clients = await GetClientsAsync(cancellationToken);
            return ClientConfigurationReader.Match(clients, clientId, clientName, inn);
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to load client profile", ex);
            return new ClientMatchResult(null, "", "Не удалось загрузить карточку клиента: " + ex.Message);
        }
    }

    private async Task<IReadOnlyList<ClientConfiguration>> GetClientsAsync(CancellationToken cancellationToken)
    {
        if (_cachedClients is not null && DateTimeOffset.UtcNow - _cachedAtUtc < CacheLifetime)
            return _cachedClients;

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedClients is not null && DateTimeOffset.UtcNow - _cachedAtUtc < CacheLifetime)
                return _cachedClients;

            try
            {
                IReadOnlyList<ClientConfiguration> clients = await LoadRemoteAsync(cancellationToken);
                _cachedClients = clients;
                _cachedAtUtc = DateTimeOffset.UtcNow;
                _logger.Info($"Remote client configuration loaded. Clients: {clients.Count}.");
                return clients;
            }
            catch when (_cachedClients is not null)
            {
                _logger.Info("Remote client configuration refresh failed; using the in-memory copy.");
                return _cachedClients;
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task<IReadOnlyList<ClientConfiguration>> LoadRemoteAsync(CancellationToken cancellationToken)
    {
        string apiUrl = DownloadApi
            + "?public_key=" + Uri.EscapeDataString(PublicKey)
            + "&path=" + Uri.EscapeDataString("/ips_encrypted.json");

        using HttpResponseMessage linkResponse = await _httpClient.GetAsync(apiUrl, cancellationToken);
        linkResponse.EnsureSuccessStatusCode();
        string linkJson = await ReadLimitedTextAsync(linkResponse, cancellationToken);
        using JsonDocument linkDocument = JsonDocument.Parse(linkJson);
        if (!linkDocument.RootElement.TryGetProperty("href", out JsonElement hrefElement) ||
            string.IsNullOrWhiteSpace(hrefElement.GetString()))
        {
            throw new InvalidDataException("Яндекс.Диск не вернул ссылку на ips_encrypted.json.");
        }

        using HttpResponseMessage configResponse = await _httpClient.GetAsync(hrefElement.GetString(), cancellationToken);
        configResponse.EnsureSuccessStatusCode();
        string encrypted = await ReadLimitedTextAsync(configResponse, cancellationToken);
        return ClientConfigurationReader.ReadLegacyObfuscated(encrypted);
    }

    private static async Task<string> ReadLimitedTextAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long length && length > MaxResponseBytes)
            throw new InvalidDataException("Ответ Яндекс.Диска превышает допустимый размер.");

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
                break;
            total += read;
            if (total > MaxResponseBytes)
                throw new InvalidDataException("Ответ Яндекс.Диска превышает допустимый размер.");
            memory.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

}

public sealed class ClientPasswordResult
{
    private ClientPasswordResult(bool success, string password, string errorMessage)
    {
        Success = success;
        Password = password;
        ErrorMessage = errorMessage;
    }

    public bool Success { get; }
    public string Password { get; }
    public string ErrorMessage { get; }

    public static ClientPasswordResult Succeeded(string password) => new(true, password, "");
    public static ClientPasswordResult Failed(string errorMessage) => new(false, "", errorMessage);
}
