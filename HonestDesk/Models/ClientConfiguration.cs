using System.Text;
using System.Text.Json;

namespace HonestDesk.Models;

public sealed class ClientConfiguration
{
    public string ClientId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Password { get; set; } = "";
    public string Token { get; set; } = "";
    public string Inn { get; set; } = "";
    public string Architecture { get; set; } = "x64";
    public bool HasLmDatabaseBackup { get; set; }
    public RuDesktopConfiguration RuDesktop { get; set; } = new();
    public EngineerAccessConfiguration? EngineerAccess { get; set; }
    public List<string> Tags { get; set; } = [];
    public ComponentVersions Versions { get; set; } = new();

    public bool HasSensitiveData =>
        !string.IsNullOrWhiteSpace(Password) ||
        !string.IsNullOrWhiteSpace(Token) ||
        !string.IsNullOrWhiteSpace(RuDesktop.Password);
}

public sealed class RuDesktopConfiguration
{
    public bool Enabled { get; set; }
    public bool AutoOfferPasswordSetup { get; set; }
    public string Password { get; set; } = "";
}

public sealed class EngineerAccessConfiguration
{
    public string Algorithm { get; set; } = "";
    public int Iterations { get; set; }
    public string SaltBase64 { get; set; } = "";
    public string PasswordHashBase64 { get; set; } = "";
}

public sealed class ComponentVersions
{
    public string? LmModule { get; set; }
    public string? AtolDriver { get; set; }
    public string? ESM { get; set; }
    public string? Controller { get; set; }
    public string? HonestFlow { get; set; }
}

public static class ClientConfigurationReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static IReadOnlyList<ClientConfiguration> ReadLegacyObfuscated(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(content.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Файл конфигурации имеет некорректный формат.", ex);
        }

        for (var index = 0; index < bytes.Length; index++)
            bytes[index] ^= 0xAA;

        try
        {
            return JsonSerializer.Deserialize<List<ClientConfiguration>>(
                       Encoding.UTF8.GetString(bytes), JsonOptions)
                   ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Не удалось разобрать конфигурацию клиентов.", ex);
        }
        finally
        {
            Array.Clear(bytes, 0, bytes.Length);
        }
    }

    public static ClientMatchResult Match(
        IEnumerable<ClientConfiguration> clients,
        string? clientId,
        string? name,
        string? inn)
    {
        var source = clients.ToList();
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            var byId = source.Where(x => Equal(x.ClientId, clientId)).ToList();
            if (byId.Count == 1) return ClientMatchResult.Found(byId[0], "ClientId");
            if (byId.Count > 1) return ClientMatchResult.Ambiguous("ClientId");
        }

        if (!string.IsNullOrWhiteSpace(inn))
        {
            var normalizedInn = Digits(inn);
            if (normalizedInn.Length >= 8)
            {
                var byInn = source.Where(x => Digits(x.Inn) == normalizedInn).ToList();
                if (byInn.Count == 1) return ClientMatchResult.Found(byInn[0], "ИНН");
                if (byInn.Count > 1) return ClientMatchResult.Ambiguous("ИНН");
            }
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            var byName = source.Where(x => Equal(x.Name, name)).ToList();
            if (byName.Count == 1) return ClientMatchResult.Found(byName[0], "название");
            if (byName.Count > 1) return ClientMatchResult.Ambiguous("название");
        }

        return ClientMatchResult.NotFound();
    }

    private static bool Equal(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.CurrentCultureIgnoreCase);

    private static string Digits(string? value) =>
        new((value ?? "").Where(char.IsDigit).ToArray());
}

public sealed record ClientMatchResult(
    ClientConfiguration? Client,
    string MatchSource,
    string Error)
{
    public bool Success => Client is not null;
    public static ClientMatchResult Found(ClientConfiguration client, string source) => new(client, source, "");
    public static ClientMatchResult Ambiguous(string source) => new(null, source, $"Найдено несколько клиентов по полю «{source}».");
    public static ClientMatchResult NotFound() => new(null, "", "Клиент не найден в конфигурации.");
}
