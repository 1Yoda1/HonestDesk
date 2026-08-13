using System.Security.Cryptography;
using System.Text;
using HonestDesk.Models.Licensing;
using Newtonsoft.Json;

namespace HonestDesk.Infrastructure.Licensing;

public sealed class LicenseGrantPublicationIndex
{
    public int SchemaVersion { get; set; } = 1;
    public long LegacyManifestRevision { get; set; }
    public string KeyId { get; set; } = "";
    public DateTimeOffset PublishedAtUtc { get; set; }
    public bool CoverageComplete { get; set; }
    public Dictionary<string, LicenseGrantPublicationIndexEntry> Entries { get; set; } =
        new(StringComparer.Ordinal);
}

public sealed class LicenseGrantPublicationIndexEntry
{
    public string ClientId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string SemanticSha256 { get; set; } = "";
    public long GrantRevision { get; set; }
    public string VersionPath { get; set; } = "";
    public string GrantSha256 { get; set; } = "";
    public string SignatureSha256 { get; set; } = "";
    public DateTimeOffset IssuedAtUtc { get; set; }
    public DateTimeOffset ValidUntilUtc { get; set; }
    public bool Revoked { get; set; }
}

public sealed class LicenseGrantIndexPublicationPointer
{
    public int SchemaVersion { get; set; } = 1;
    public long LegacyManifestRevision { get; set; }
    public string VersionPath { get; set; } = "";
    public string IndexSha256 { get; set; } = "";
    public string SignatureSha256 { get; set; } = "";
    public DateTimeOffset PublishedAtUtc { get; set; }
}

public sealed record LicenseGrantTarget(
    string TargetHash,
    string GrantRoot,
    string SemanticSha256,
    LicenseGrant Grant);

public sealed record IncrementalLicenseGrantPublication(
    LicenseGrantTarget Target,
    LicenseGrantPublication Publication,
    long Revision,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ValidUntilUtc,
    bool Revoked);

public sealed class IncrementalLicenseGrantPublicationBuilder
{
    private readonly string _keyId;
    private readonly string _privateKeyPem;

    public IncrementalLicenseGrantPublicationBuilder(string keyId, string privateKeyPath)
    {
        if (string.IsNullOrWhiteSpace(keyId))
            throw new InvalidOperationException("KeyId is required.");
        if (string.IsNullOrWhiteSpace(privateKeyPath) || !File.Exists(privateKeyPath))
            throw new InvalidOperationException("The private signing key is not configured or unavailable.");

        _keyId = keyId;
        _privateKeyPem = File.ReadAllText(privateKeyPath);
    }

    public IReadOnlyList<LicenseGrantTarget> BuildTargets(
        LicenseManifest manifest,
        IReadOnlyDictionary<string, bool?> clientPolicies)
    {
        LicenseDocumentService.Validate(manifest);
        var operatorDeviceIds = new HashSet<string>(
            (manifest.OperatorDevices ?? [])
                .Where(device => device is not null && !string.IsNullOrWhiteSpace(device.DeviceId))
                .Select(device => device.DeviceId),
            StringComparer.OrdinalIgnoreCase);
        var targets = new List<LicenseGrantTarget>();

        foreach (ClientLicense client in manifest.Clients ?? [])
        {
            bool clientEnabled = ClientLicensePolicyGrantSemantics.ToClientEnabled(
                clientPolicies, client.ClientId);
            foreach (LicensedDevice device in client.Devices ?? [])
            {
                if (device is null || string.IsNullOrWhiteSpace(device.DeviceId))
                    continue;
                if (operatorDeviceIds.Contains(device.DeviceId))
                    continue;

                targets.Add(CreateTarget(new LicenseGrant
                {
                    SchemaVersion = manifest.SchemaVersion,
                    ClientId = client.ClientId,
                    DeviceId = device.DeviceId,
                    ClientEnabled = clientEnabled,
                    DeviceEnabled = device.Enabled,
                    MinHonestFlowVersion = client.MinHonestFlowVersion,
                    OfflineGraceHours = client.OfflineGraceHours,
                    Features = client.Features?.Distinct().OrderBy(feature => feature).ToList() ?? [],
                    PointAddress = device.Address ?? string.Empty
                }));
            }

            foreach (OperatorDevice device in manifest.OperatorDevices ?? [])
            {
                if (device is null || string.IsNullOrWhiteSpace(device.DeviceId))
                    continue;

                targets.Add(CreateTarget(new LicenseGrant
                {
                    SchemaVersion = manifest.SchemaVersion,
                    ClientId = client.ClientId,
                    DeviceId = device.DeviceId,
                    ClientEnabled = clientEnabled,
                    DeviceEnabled = device.Enabled,
                    OperatorDevice = true,
                    MinHonestFlowVersion = "0.0.0",
                    OfflineGraceHours = client.OfflineGraceHours,
                    Features = []
                }));
            }
        }

        var duplicate = targets
            .GroupBy(target => target.TargetHash, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException(
                $"Duplicate ClientId/DeviceId grant target: {duplicate.Key}.");

        return targets;
    }

    public LicenseGrantTarget CreateTarget(LicenseGrant grant)
    {
        string targetHash = TargetHash(grant.ClientId, grant.DeviceId);
        return new LicenseGrantTarget(
            targetHash,
            "/licenses/grants/" + targetHash,
            SemanticHash(grant),
            grant);
    }

    public IncrementalLicenseGrantPublication BuildChanged(
        LicenseGrantTarget target,
        LicenseGrantPublicationIndexEntry? previous,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset validUntilUtc)
    {
        long revision = checked((previous?.GrantRevision ?? 0) + 1);
        return Build(target, revision, issuedAtUtc, validUntilUtc, false);
    }

    public IncrementalLicenseGrantPublication BuildRevocation(
        LicenseGrantPublicationIndexEntry previous,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset validUntilUtc)
    {
        var target = CreateTarget(new LicenseGrant
        {
            SchemaVersion = 1,
            ClientId = previous.ClientId,
            DeviceId = previous.DeviceId,
            ClientEnabled = false,
            DeviceEnabled = false,
            OperatorDevice = false,
            MinHonestFlowVersion = "0.0.0",
            OfflineGraceHours = 0,
            Features = []
        });
        return Build(
            target,
            checked(previous.GrantRevision + 1),
            issuedAtUtc,
            validUntilUtc,
            true);
    }

    public LicenseGrantPublicationIndexEntry CreateIndexEntry(
        IncrementalLicenseGrantPublication item)
    {
        var pointer = JsonConvert.DeserializeObject<LicenseGrantPublicationPointer>(
            Encoding.UTF8.GetString(item.Publication.PointerBytes))
            ?? throw new InvalidDataException("Generated grant pointer is invalid.");
        return new LicenseGrantPublicationIndexEntry
        {
            ClientId = item.Target.Grant.ClientId,
            DeviceId = item.Target.Grant.DeviceId,
            SemanticSha256 = item.Target.SemanticSha256,
            GrantRevision = item.Revision,
            VersionPath = item.Publication.VersionPath,
            GrantSha256 = pointer.GrantSha256,
            SignatureSha256 = pointer.SignatureSha256,
            IssuedAtUtc = item.IssuedAtUtc,
            ValidUntilUtc = item.ValidUntilUtc,
            Revoked = item.Revoked
        };
    }

    public (byte[] Json, byte[] Signature) SignIndex(LicenseGrantPublicationIndex index)
    {
        byte[] json = Utf8(JsonConvert.SerializeObject(index, Formatting.Indented));
        return (json, Sign(json));
    }

    public static string SemanticHash(LicenseGrant grant)
    {
        var semantic = new
        {
            grant.SchemaVersion,
            grant.ClientId,
            grant.DeviceId,
            grant.ClientEnabled,
            grant.DeviceEnabled,
            grant.OperatorDevice,
            grant.MinHonestFlowVersion,
            grant.OfflineGraceHours,
            Features = (grant.Features ?? []).Distinct().OrderBy(feature => feature).ToList(),
            grant.PointAddress
        };
        return Hash(Utf8(JsonConvert.SerializeObject(semantic, Formatting.None)));
    }

    public static string TargetHash(string clientId, string deviceId) =>
        Convert.ToHexString(SHA256.HashData(Utf8(clientId + "\n" + deviceId)))
            .ToLowerInvariant();

    private IncrementalLicenseGrantPublication Build(
        LicenseGrantTarget target,
        long revision,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset validUntilUtc,
        bool revoked)
    {
        LicenseGrant grant = Clone(target.Grant);
        grant.Revision = revision;
        grant.IssuedAtUtc = issuedAtUtc;
        grant.ValidUntilUtc = validUntilUtc;
        ValidateGrant(grant);

        byte[] grantBytes = Utf8(JsonConvert.SerializeObject(grant, Formatting.Indented));
        byte[] signatureBytes = Sign(grantBytes);
        string versionPath =
            target.GrantRoot + "/versions/revision-" + revision.ToString("D20");
        byte[] pointerBytes = Utf8(JsonConvert.SerializeObject(
            new LicenseGrantPublicationPointer
            {
                Revision = revision,
                VersionPath = versionPath,
                GrantSha256 = Hash(grantBytes),
                SignatureSha256 = Hash(signatureBytes),
                PublishedAtUtc = DateTimeOffset.UtcNow
            },
            Formatting.Indented));
        var publication = new LicenseGrantPublication(
            target.GrantRoot,
            versionPath,
            grantBytes,
            signatureBytes,
            pointerBytes);
        return new IncrementalLicenseGrantPublication(
            target,
            publication,
            revision,
            issuedAtUtc,
            validUntilUtc,
            revoked);
    }

    private byte[] Sign(byte[] payload)
    {
        using ECDsa key = ECDsa.Create();
        key.ImportFromPem(_privateKeyPem);
        if (key.KeySize != 256)
            throw new CryptographicException("An ECDSA P-256 key is required.");
        byte[] signature = key.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        return Utf8(JsonConvert.SerializeObject(
            new LicenseSignatureEnvelope
            {
                KeyId = _keyId,
                Algorithm = LicenseSignatureEnvelope.AlgorithmName,
                Signature = Convert.ToBase64String(signature)
            },
            Formatting.Indented));
    }

    private static LicenseGrant Clone(LicenseGrant grant) =>
        JsonConvert.DeserializeObject<LicenseGrant>(
            JsonConvert.SerializeObject(grant))
        ?? throw new InvalidDataException("Could not clone license grant.");

    private static void ValidateGrant(LicenseGrant grant)
    {
        if (grant.SchemaVersion <= 0)
            throw new ArgumentException("SchemaVersion must be positive.", nameof(grant));
        if (grant.Revision < 0)
            throw new ArgumentException("Revision cannot be negative.", nameof(grant));
        if (string.IsNullOrWhiteSpace(grant.ClientId))
            throw new ArgumentException("ClientId is required.", nameof(grant));
        if (string.IsNullOrWhiteSpace(grant.DeviceId))
            throw new ArgumentException("DeviceId is required.", nameof(grant));
        if (grant.ValidUntilUtc < grant.IssuedAtUtc)
            throw new ArgumentException("ValidUntilUtc cannot be earlier than IssuedAtUtc.", nameof(grant));
        if (grant.OfflineGraceHours < 0)
            throw new ArgumentException("OfflineGraceHours cannot be negative.", nameof(grant));
        if (grant.PointAddress?.Trim().Length > 500)
            throw new ArgumentException("PointAddress must not exceed 500 characters.", nameof(grant));
    }

    private static byte[] Utf8(string value) => new UTF8Encoding(false).GetBytes(value);
    private static string Hash(byte[] bytes) =>
        Convert.ToBase64String(SHA256.HashData(bytes));
}

