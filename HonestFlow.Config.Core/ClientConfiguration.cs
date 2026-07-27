namespace HonestFlow.Config.Core;

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
