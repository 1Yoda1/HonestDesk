using System.Security.Cryptography;

namespace HonestDesk.Infrastructure.Diagnostics;

/// <summary>
/// Verifies a diagnostic archive after it has been downloaded from the short-lived storage URL.
/// The expected metadata comes from HonestLicenseServer's authenticated archive catalogue.
/// </summary>
public static class DiagnosticArchiveFileVerifier
{
    public static async Task VerifyAsync(
        string path,
        long expectedSizeBytes,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        if (expectedSizeBytes <= 0)
            throw new InvalidDataException("Сервер не передал допустимый размер диагностического архива.");

        if (!IsSha256(expectedSha256))
            throw new InvalidDataException("Сервер не передал допустимую контрольную сумму диагностического архива.");

        long actualSizeBytes = new FileInfo(path).Length;
        if (actualSizeBytes != expectedSizeBytes)
        {
            throw new InvalidDataException(
                $"Размер скачанного архива не совпал: ожидалось {expectedSizeBytes} байт, получено {actualSizeBytes} байт.");
        }

        await using FileStream stream = File.OpenRead(path);
        string actualSha256 = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Контрольная сумма скачанного архива не совпала.");
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
