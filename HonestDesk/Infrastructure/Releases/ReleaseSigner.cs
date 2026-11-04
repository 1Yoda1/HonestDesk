using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HonestDesk.Infrastructure.Releases;

public sealed record SignedReleaseEnvelope(string KeyId, string ManifestBase64, string SignatureBase64);

public static class ReleaseSigner
{
    private static readonly byte[] KeyFileMagic = Encoding.ASCII.GetBytes("HFRK1");
    private const int SaltLength = 16;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int KdfIterations = 250_000;

    public static SignedReleaseEnvelope Sign(
        byte[] encryptedPrivateKey, ReadOnlySpan<char> password, string filePath,
        string component, string version, string architecture)
    {
        if (string.IsNullOrWhiteSpace(component) || component.Length > 100)
            throw new ArgumentException("Некорректное имя компонента.");
        if (!IsValidReleaseVersion(component, version))
            throw new ArgumentException("Некорректная версия дистрибутива.");
        if (architecture is not ("x86" or "x64" or "arm64" or "any"))
            throw new ArgumentException("Некорректная архитектура дистрибутива.");

        FileInfo file = new(filePath);
        if (!file.Exists || file.Length <= 0)
            throw new FileNotFoundException("Файл дистрибутива не найден или пуст.", filePath);
        if (string.Equals(component, "HonestFlow", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(file.Name, "HonestFlow.exe", StringComparison.Ordinal))
            throw new ArgumentException("Компонент HonestFlow должен публиковаться как HonestFlow.exe.");

        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] rawPrivateKey = DecryptPrivateKey(encryptedPrivateKey, password);
        try
        {
            key.ImportECPrivateKey(rawPrivateKey, out int bytesRead);
            if (bytesRead != rawPrivateKey.Length)
                throw new CryptographicException("Некорректный закрытый ключ выпуска.");
        }
        finally { CryptographicOperations.ZeroMemory(rawPrivateKey); }

        if (key.KeySize != 256 ||
            key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
            throw new CryptographicException("Ключ выпуска должен быть ECDSA P-256.");

        byte[] publicKey = key.ExportSubjectPublicKeyInfo();
        using FileStream input = new(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
        string sha256 = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            component,
            version,
            fileName = file.Name,
            architecture,
            sizeBytes = file.Length,
            sha256
        });
        byte[] signature = key.SignData(
            manifestBytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return new SignedReleaseEnvelope(
            GetKeyId(publicKey), Convert.ToBase64String(manifestBytes), Convert.ToBase64String(signature));
    }

    private static string GetKeyId(byte[] publicKey) =>
        "release_" + Convert.ToHexString(SHA256.HashData(publicKey).AsSpan(0, 12)).ToLowerInvariant();

    private static bool IsValidReleaseVersion(string component, string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 100 ||
            !char.IsAsciiLetterOrDigit(version[0]) || !char.IsAsciiLetterOrDigit(version[^1]) ||
            version.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            return false;

        return !string.Equals(component, "HonestFlow", StringComparison.OrdinalIgnoreCase) ||
               Version.TryParse(version, out _);
    }

    private static byte[] DecryptPrivateKey(byte[] encryptedPrivateKey, ReadOnlySpan<char> password)
    {
        int headerLength = KeyFileMagic.Length + SaltLength + NonceLength + TagLength;
        if (encryptedPrivateKey is null || encryptedPrivateKey.Length <= headerLength ||
            !encryptedPrivateKey.AsSpan(0, KeyFileMagic.Length).SequenceEqual(KeyFileMagic))
            throw new CryptographicException("Некорректный файл ключа выпуска.");

        ReadOnlySpan<byte> salt = encryptedPrivateKey.AsSpan(KeyFileMagic.Length, SaltLength);
        ReadOnlySpan<byte> nonce = encryptedPrivateKey.AsSpan(KeyFileMagic.Length + SaltLength, NonceLength);
        ReadOnlySpan<byte> tag = encryptedPrivateKey.AsSpan(KeyFileMagic.Length + SaltLength + NonceLength, TagLength);
        ReadOnlySpan<byte> ciphertext = encryptedPrivateKey.AsSpan(headerLength);
        byte[] rawPrivateKey = new byte[ciphertext.Length];
        byte[] derivedKey = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, KdfIterations, HashAlgorithmName.SHA256, 32);
        try
        {
            using AesGcm cipher = new(derivedKey, TagLength);
            cipher.Decrypt(nonce, ciphertext, tag, rawPrivateKey, KeyFileMagic);
            return rawPrivateKey;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(rawPrivateKey);
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(derivedKey); }
    }
}
