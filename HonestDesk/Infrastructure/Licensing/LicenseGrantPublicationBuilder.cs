using System.Security.Cryptography;
using System.Text;
using HonestDesk.Models.Licensing;
using Newtonsoft.Json;

namespace HonestDesk.Infrastructure.Licensing;

public sealed class LicenseGrantPublicationBuilder
{
    public IReadOnlyList<LicenseGrantPublication> Build(LicenseManifest manifest, string keyId, string privateKeyPath)
    {
        LicenseDocumentService.Validate(manifest);
        if (string.IsNullOrWhiteSpace(keyId)) throw new InvalidOperationException("KeyId не задан.");
        if (string.IsNullOrWhiteSpace(privateKeyPath) || !File.Exists(privateKeyPath))
            throw new InvalidOperationException("Закрытый ключ не настроен или недоступен.");

        string pem = File.ReadAllText(privateKeyPath);
        var result = new List<LicenseGrantPublication>();
        foreach (ClientLicense client in manifest.Clients ?? [])
        {
            foreach (LicensedDevice device in client.Devices ?? [])
            {
                if (device is null || string.IsNullOrWhiteSpace(device.DeviceId)) continue;
                result.Add(BuildOne(new LicenseGrant
                {
                    SchemaVersion = manifest.SchemaVersion, Revision = manifest.Revision,
                    IssuedAtUtc = manifest.IssuedAtUtc, ValidUntilUtc = manifest.ValidUntilUtc,
                    ClientId = client.ClientId, DeviceId = device.DeviceId,
                    ClientEnabled = client.Enabled, DeviceEnabled = device.Enabled,
                    MinHonestFlowVersion = client.MinHonestFlowVersion,
                    OfflineGraceHours = client.OfflineGraceHours,
                    Features = client.Features?.Distinct().ToList() ?? [], PointAddress = device.Address
                }, keyId, pem));
            }
            foreach (OperatorDevice device in manifest.OperatorDevices ?? [])
            {
                if (device is null || string.IsNullOrWhiteSpace(device.DeviceId)) continue;
                result.Add(BuildOne(new LicenseGrant
                {
                    SchemaVersion = manifest.SchemaVersion, Revision = manifest.Revision,
                    IssuedAtUtc = manifest.IssuedAtUtc, ValidUntilUtc = manifest.ValidUntilUtc,
                    ClientId = client.ClientId, DeviceId = device.DeviceId,
                    ClientEnabled = true, DeviceEnabled = device.Enabled, OperatorDevice = true,
                    MinHonestFlowVersion = "0.0.0", OfflineGraceHours = client.OfflineGraceHours, Features = []
                }, keyId, pem));
            }
        }
        return result;
    }

    private static LicenseGrantPublication BuildOne(LicenseGrant grant, string keyId, string pem)
    {
        ValidateGrant(grant);
        byte[] grantBytes = Utf8(JsonConvert.SerializeObject(grant, Formatting.Indented));
        byte[] signatureBytes = Sign(grantBytes, keyId, pem);
        string opaqueId = Convert.ToHexString(SHA256.HashData(Utf8(grant.ClientId + "\n" + grant.DeviceId))).ToLowerInvariant();
        string root = "/licenses/grants/" + opaqueId;
        string version = root + "/versions/revision-" + grant.Revision.ToString("D20");
        byte[] pointer = Utf8(JsonConvert.SerializeObject(new LicenseGrantPublicationPointer
        {
            Revision = grant.Revision, VersionPath = version, GrantSha256 = Hash(grantBytes),
            SignatureSha256 = Hash(signatureBytes), PublishedAtUtc = DateTimeOffset.UtcNow
        }, Formatting.Indented));
        return new(root, version, grantBytes, signatureBytes, pointer);
    }

    private static byte[] Sign(byte[] payload, string keyId, string pem)
    {
        using ECDsa key = ECDsa.Create(); key.ImportFromPem(pem);
        if (key.KeySize != 256) throw new CryptographicException("Требуется ECDSA P-256 key.");
        byte[] signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return Utf8(JsonConvert.SerializeObject(new LicenseSignatureEnvelope
        { KeyId = keyId, Algorithm = LicenseSignatureEnvelope.AlgorithmName, Signature = Convert.ToBase64String(signature) }, Formatting.Indented));
    }
    private static byte[] Utf8(string value) => new UTF8Encoding(false).GetBytes(value);
    private static string Hash(byte[] bytes) => Convert.ToBase64String(SHA256.HashData(bytes));

    private static void ValidateGrant(LicenseGrant grant)
    {
        if (grant.SchemaVersion <= 0) throw new ArgumentException("SchemaVersion must be positive.", nameof(grant));
        if (grant.Revision < 0) throw new ArgumentException("Revision cannot be negative.", nameof(grant));
        if (string.IsNullOrWhiteSpace(grant.ClientId)) throw new ArgumentException("ClientId is required.", nameof(grant));
        if (string.IsNullOrWhiteSpace(grant.DeviceId)) throw new ArgumentException("DeviceId is required.", nameof(grant));
        if (grant.ValidUntilUtc < grant.IssuedAtUtc) throw new ArgumentException("ValidUntilUtc cannot be earlier than IssuedAtUtc.", nameof(grant));
        if (grant.OfflineGraceHours < 0) throw new ArgumentException("OfflineGraceHours cannot be negative.", nameof(grant));
        if (grant.PointAddress?.Trim().Length > 500) throw new ArgumentException("PointAddress must not exceed 500 characters.", nameof(grant));
    }
}

public sealed record LicenseGrantPublication(string GrantRoot, string VersionPath,
    byte[] GrantBytes, byte[] SignatureBytes, byte[] PointerBytes);
