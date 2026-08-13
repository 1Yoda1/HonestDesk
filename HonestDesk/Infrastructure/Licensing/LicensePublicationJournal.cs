using System;
using System.IO;
using Newtonsoft.Json;

namespace HonestDesk.Infrastructure.Licensing
{
    public static class LicensePublicationJournal
    {
        public static string PathName => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "HonestDesk", "license-publications.jsonl");

        private static string FallbackPathName => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HonestDesk", "license-publications.jsonl");

        public static void Write(long revision, string stage, string status,
            string? versionPath = null, string? manifestHash = null, string? signatureHash = null)
        {
            var record = new { TimestampUtc = DateTimeOffset.UtcNow, Revision = revision,
                Stage = stage, Status = status, VersionPath = versionPath,
                ManifestSha256 = manifestHash, SignatureSha256 = signatureHash };
            string line = JsonConvert.SerializeObject(record) + Environment.NewLine;
            if (TryAppend(PathName, line))
                return;

            TryAppend(FallbackPathName, line);
        }

        private static bool TryAppend(string path, string line)
        {
            try
            {
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);
                File.AppendAllText(path, line);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
