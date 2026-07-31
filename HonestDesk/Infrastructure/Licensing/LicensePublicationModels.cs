using System;

namespace HonestDesk.Infrastructure.Licensing
{
    public sealed class LicensePublicationPointer
    {
        public int SchemaVersion { get; set; } = 1;
        public long Revision { get; set; }
        public string VersionPath { get; set; }
        public string ManifestSha256 { get; set; }
        public string SignatureSha256 { get; set; }
        public DateTimeOffset PublishedAtUtc { get; set; }
    }

    public sealed class LicensePublicationException : Exception
    {
        public LicensePublicationException(string stage, string message, Exception inner = null)
            : base(message, inner) { Stage = stage; }
        public string Stage { get; }
    }
}
