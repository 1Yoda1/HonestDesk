using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace HonestDesk.Services;

public sealed class AdminAccessService
{
    private const int Iterations = 310_000;
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public bool IsConfigured => File.Exists(AppPaths.AdminAccessPath);
    public bool CanBootstrap
    {
        get
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public AdminAccessResult Configure(string password)
    {
        if (!CanBootstrap)
            return AdminAccessResult.Fail("Первичную настройку может выполнить только администратор Windows.");
        if (password.Length < 10)
            return AdminAccessResult.Fail("Пароль администратора должен содержать минимум 10 символов.");

        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Hash(password, salt, Iterations);
        var record = new AdminAccessRecord
        {
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(hash),
            Iterations = Iterations
        };
        Save(record);
        Audit("Admin access configured");
        CryptographicOperations.ZeroMemory(hash);
        return AdminAccessResult.Ok();
    }

    public AdminAccessResult Verify(string password)
    {
        AdminAccessRecord record;
        try { record = Load(); }
        catch
        {
            Audit("Admin access record could not be read");
            return AdminAccessResult.Fail("Хранилище административного доступа повреждено или принадлежит другому пользователю Windows.");
        }

        if (record.LockedUntilUtc is { } lockedUntil && lockedUntil > DateTimeOffset.UtcNow)
        {
            Audit("Admin access denied: lockout active");
            return AdminAccessResult.Fail($"Доступ временно заблокирован до {lockedUntil.ToLocalTime():HH:mm}.");
        }

        byte[] expected;
        byte[] salt;
        try
        {
            expected = Convert.FromBase64String(record.Hash);
            salt = Convert.FromBase64String(record.Salt);
        }
        catch
        {
            return AdminAccessResult.Fail("Некорректное хранилище административного доступа.");
        }

        byte[] actual = Hash(password, salt, record.Iterations);
        bool success = CryptographicOperations.FixedTimeEquals(expected, actual);
        CryptographicOperations.ZeroMemory(actual);

        if (success)
        {
            record.FailedAttempts = 0;
            record.LockedUntilUtc = null;
            Save(record);
            Audit("Admin session unlocked");
            return AdminAccessResult.Ok();
        }

        record.FailedAttempts++;
        if (record.FailedAttempts >= MaxFailures)
        {
            record.FailedAttempts = 0;
            record.LockedUntilUtc = DateTimeOffset.UtcNow.Add(LockDuration);
        }
        Save(record);
        Audit("Admin access denied: invalid password");
        return AdminAccessResult.Fail("Неверный пароль администратора.");
    }

    public void Audit(string action)
    {
        try
        {
            AppPaths.EnsureDataDirectory();
            File.AppendAllText(AppPaths.AdminAuditPath,
                $"{DateTimeOffset.Now:O} user={Environment.UserName} action=\"{action.Replace("\"", "'")}\"{Environment.NewLine}");
        }
        catch { }
    }

    private static byte[] Hash(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    private static AdminAccessRecord Load()
    {
        string protectedJson = File.ReadAllText(AppPaths.AdminAccessPath);
        string json = DpapiProtector.Unprotect(protectedJson);
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidDataException();
        return JsonSerializer.Deserialize<AdminAccessRecord>(json) ?? throw new InvalidDataException();
    }

    private static void Save(AdminAccessRecord record)
    {
        AppPaths.EnsureDataDirectory();
        string json = JsonSerializer.Serialize(record, JsonOptions);
        string protectedJson = DpapiProtector.Protect(json);
        string temporary = AppPaths.AdminAccessPath + ".tmp";
        File.WriteAllText(temporary, protectedJson);
        File.Move(temporary, AppPaths.AdminAccessPath, true);
    }

    private sealed class AdminAccessRecord
    {
        public string Salt { get; set; } = "";
        public string Hash { get; set; } = "";
        public int Iterations { get; set; }
        public int FailedAttempts { get; set; }
        public DateTimeOffset? LockedUntilUtc { get; set; }
    }
}

public sealed record AdminAccessResult(bool Success, string Error)
{
    public static AdminAccessResult Ok() => new(true, "");
    public static AdminAccessResult Fail(string error) => new(false, error);
}
