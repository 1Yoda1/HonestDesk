using System.Security.Cryptography;
using System.Text;
using HonestDesk.Infrastructure.Api;
using HonestDesk.Infrastructure.Licensing;
using HonestDesk.Models;

namespace HonestDesk.Services;

public sealed class RemoteAccessKeyService(SettingsService settingsService, AppSettings settings)
{
    public async Task EnsurePublishedAsync(HonestLicenseAdminApiClient api, CancellationToken token)
    {
        AdminRemoteAccessKey? server = await api.GetRemoteAccessKeyAsync(token);
        if (server is not null)
        {
            if (server.KeyId != settings.RemoteAccessKeyId ||
                string.IsNullOrWhiteSpace(settings.ProtectedRemoteAccessPrivateKey))
                throw new InvalidOperationException(
                    "Ключ удалённого доступа сервера не принадлежит этому HonestDesk. Нужен защищённый перенос ключа администратора.");
            using var local = LoadPrivateKey();
            if (server.PublicKeyBase64 != Convert.ToBase64String(local.ExportSubjectPublicKeyInfo()))
                throw new InvalidOperationException("Публичный ключ сервера не совпадает с ключом этого HonestDesk.");
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.ProtectedRemoteAccessPrivateKey))
        {
            using var generated = RSA.Create(3072);
            settings.RemoteAccessKeyId = Guid.NewGuid().ToString("D");
            settings.ProtectedRemoteAccessPrivateKey = DpapiProtector.Protect(
                Convert.ToBase64String(generated.ExportPkcs8PrivateKey()));
            settingsService.Save(settings);
        }

        LicenseEditorSettings signer = LicenseEditorSettings.Load();
        if (string.IsNullOrWhiteSpace(signer.PrivateKeyPath) || !File.Exists(signer.PrivateKeyPath))
            throw new InvalidOperationException("Для публикации ключа удалённого доступа настройте ключ подписи лицензий.");

        using var rsa = LoadPrivateKey();
        string publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        byte[] signedBytes = Encoding.UTF8.GetBytes(
            $"HonestFlow.RemoteAccessKey.v1\n{settings.RemoteAccessKeyId}\n{publicKey}");
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(File.ReadAllText(signer.PrivateKeyPath));
        string signature = Convert.ToBase64String(ecdsa.SignData(signedBytes,
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        await api.PublishRemoteAccessKeyAsync(new AdminRemoteAccessKey
        {
            KeyId = settings.RemoteAccessKeyId, PublicKeyBase64 = publicKey,
            SignatureBase64 = signature, SigningKeyId = signer.KeyId
        }, token);
    }

    public string Decrypt(AdminRemoteAccessCredential credential)
    {
        if (credential.KeyId != settings.RemoteAccessKeyId)
            throw new InvalidOperationException("Пароль зашифрован для другого ключа HonestDesk.");
        using var rsa = LoadPrivateKey();
        byte[] plain = rsa.Decrypt(Convert.FromBase64String(credential.CiphertextBase64),
            RSAEncryptionPadding.OaepSHA256);
        return Encoding.UTF8.GetString(plain);
    }

    private RSA LoadPrivateKey()
    {
        string encoded = DpapiProtector.Unprotect(settings.ProtectedRemoteAccessPrivateKey);
        if (string.IsNullOrWhiteSpace(encoded))
            throw new InvalidOperationException("Закрытый ключ удалённого доступа недоступен в этом профиле Windows.");
        var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(encoded), out _);
        return rsa;
    }
}
