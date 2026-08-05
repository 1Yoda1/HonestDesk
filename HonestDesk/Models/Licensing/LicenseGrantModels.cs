using System;
using System.Collections.Generic;

namespace HonestDesk.Models.Licensing;

public sealed class LicenseGrant
{
    private DateTimeOffset _issuedAtUtc;
    private DateTimeOffset _validUntilUtc;

    public int SchemaVersion { get; set; }
    public long Revision { get; set; }
    public string ClientId { get; set; }
    public string DeviceId { get; set; }
    public bool ClientEnabled { get; set; }
    public bool DeviceEnabled { get; set; }
    public bool OperatorDevice { get; set; }
    public string MinHonestFlowVersion { get; set; }
    public int OfflineGraceHours { get; set; }
    public List<LicenseFeature> Features { get; set; } = new();
    public string PointAddress { get; set; }
    public DateTimeOffset IssuedAtUtc
    {
        get => _issuedAtUtc;
        set => _issuedAtUtc = value.ToUniversalTime();
    }
    public DateTimeOffset ValidUntilUtc
    {
        get => _validUntilUtc;
        set => _validUntilUtc = value.ToUniversalTime();
    }
}

public sealed class LicenseGrantPublicationPointer
{
    public int SchemaVersion { get; set; } = 1;
    public long Revision { get; set; }
    public string VersionPath { get; set; }
    public string GrantSha256 { get; set; }
    public string SignatureSha256 { get; set; }
    public DateTimeOffset PublishedAtUtc { get; set; }
}
