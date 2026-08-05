using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HonestDesk.Models;
using HonestDesk.Models.Licensing;
using HonestDesk.Infrastructure.Licensing;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;

namespace HonestDesk.Infrastructure
{
    public class YandexDiskClient
    {
        private const string PublicResourcesApi = "https://cloud-api.yandex.net/v1/disk/public/resources";
        private const string PublicDownloadApi = "https://cloud-api.yandex.net/v1/disk/public/resources/download";
        private static readonly Uri WebDavBaseUri = new Uri("https://webdav.yandex.ru");

        public async Task<Dictionary<string, string>> GetFilesAsync()
        {
            ConfigEditorLogger.Info("Loading public Yandex Disk file list.");
            using var client = CreatePublicClient();
            var resourcesJson = await client.GetStringAsync(BuildPublicResourcesUrl());
            var root = JObject.Parse(resourcesJson);
            var items = root["_embedded"]?["items"] as JArray;

            if (items == null)
                throw new InvalidDataException("Yandex Disk public folder does not contain a file list.");

            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var type = (string)item["type"];
                var name = (string)item["name"];
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
                {
                    var url = (string)item["file"];
                    if (string.IsNullOrWhiteSpace(url))
                        url = await GetPublicDownloadUrl(client, "/" + name);

                    files[name] = url;
                }
                else if (string.Equals(type, "dir", StringComparison.OrdinalIgnoreCase))
                {
                    files[name] = "/" + name.Trim('/');
                }
            }

            ConfigEditorLogger.Info($"Loaded public Yandex Disk file list. Items: {files.Count}");
            return files;
        }

        public async Task<string> DownloadFileContentAsync(string downloadUrl)
        {
            ConfigEditorLogger.Info($"Downloading text file from Yandex Disk. Source: {downloadUrl}");
            using var client = CreatePublicClient();

            if (Uri.TryCreate(downloadUrl, UriKind.Absolute, out var absoluteUri))
                return await client.GetStringAsync(absoluteUri);

            var resolvedUrl = await GetPublicDownloadUrl(client, downloadUrl);
            return await client.GetStringAsync(resolvedUrl);
        }

        public async Task UploadConfigFilesAsync(string ipsPath, string versionsPath, string supportMailPath)
        {
            ConfigEditorLogger.Info("Uploading config files to Yandex Disk.");
            await UploadFileAsync(ipsPath, "ips_encrypted.json");
            await UploadFileAsync(versionsPath, "versions.json");
            await UploadFileAsync(supportMailPath, "support_mail_encrypted.json");
            ConfigEditorLogger.Info("Config files uploaded to Yandex Disk.");
        }

        public async Task UploadSupportMailFileAsync(string supportMailPath)
        {
            ConfigEditorLogger.Info("Uploading support_mail_encrypted.json to Yandex Disk.");
            await UploadFileAsync(supportMailPath, "support_mail_encrypted.json");
            ConfigEditorLogger.Info("support_mail_encrypted.json uploaded to Yandex Disk.");
        }

        public async Task UploadIpsFileAsync(string ipsPath)
        {
            ConfigEditorLogger.Info("Uploading ips_encrypted.json to Yandex Disk.");
            await UploadFileAsync(ipsPath, "ips_encrypted.json");
            ConfigEditorLogger.Info("ips_encrypted.json uploaded to Yandex Disk.");
        }

        public async Task UploadVersionsFileAsync(string versionsPath)
        {
            ConfigEditorLogger.Info("Uploading versions.json to Yandex Disk.");
            await UploadFileAsync(versionsPath, "versions.json");
            ConfigEditorLogger.Info("versions.json uploaded to Yandex Disk.");
        }

        public async Task UploadHonestFlowExecutableAsync(string executablePath, string version)
        {
            var versionDirectory = CombineRemotePath(YandexDiskSettings.RemoteDirectory, version);
            ConfigEditorLogger.Info($"Uploading HonestFlow.exe. Local: {executablePath}. Remote folder: {versionDirectory}");
            await EnsureDirectoryAsync(versionDirectory);
            await UploadFileToRemotePathAsync(executablePath, CombineRemotePath(versionDirectory, "HonestFlow.exe"));
            ConfigEditorLogger.Info($"HonestFlow.exe uploaded for version {version}.");
        }

        public async Task PublishLicensesAsync(
            string jsonPath,
            string signaturePath,
            LicenseManifest manifest,
            string keyId,
            string privateKeyPath,
            string publicKeyPath)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            long revision = manifest.Revision;
            string versionName = "revision-" + revision.ToString("D20");
            string publicVersionPath = "/licenses/versions/" + versionName;
            string remoteRoot = CombineRemotePath(YandexDiskSettings.RemoteDirectory, "licenses");
            string remoteVersions = CombineRemotePath(remoteRoot, "versions");
            string remoteVersion = CombineRemotePath(remoteVersions, versionName);
            byte[] json = File.ReadAllBytes(jsonPath);
            byte[] signature = File.ReadAllBytes(signaturePath);
            var grantBuilder = new LicenseGrantPublicationBuilder();
            IReadOnlyList<LicenseGrantPublication> grants =
                grantBuilder.Build(manifest, keyId, privateKeyPath);
            var verifier = new LicenseDocumentService();

            if (!verifier.Verify(json, signature, publicKeyPath, out string localError))
                throw new LicensePublicationException("LocalSignatureVerification", localError);

            foreach (LicenseGrantPublication grant in grants)
                if (!verifier.Verify(
                    grant.GrantBytes, grant.SignatureBytes, publicKeyPath, out localError))
                    throw new LicensePublicationException(
                        "LocalGrantSignatureVerification", localError);

            string manifestHash = ComputeHash(json);
            string signatureHash = ComputeHash(signature);
            LicensePublicationJournal.Write(revision, "Start", "Started",
                publicVersionPath, manifestHash, signatureHash);

            try
            {
                await EnsureDirectoryAsync(remoteRoot);
                await EnsureDirectoryAsync(remoteVersions);
                await EnsureDirectoryAsync(remoteVersion);
                await UploadBytesAsync(json, CombineRemotePath(remoteVersion, "licenses.json"));
                await UploadBytesAsync(signature, CombineRemotePath(remoteVersion, "licenses.json.sig"));

                byte[] remoteJson = await ReadPublicBytesAsync(publicVersionPath + "/licenses.json");
                byte[] remoteSignature = await ReadPublicBytesAsync(publicVersionPath + "/licenses.json.sig");
                string remoteError = null;
                if (ComputeHash(remoteJson) != manifestHash ||
                    ComputeHash(remoteSignature) != signatureHash ||
                    !verifier.Verify(remoteJson, remoteSignature, publicKeyPath, out remoteError))
                {
                    throw new LicensePublicationException(
                        "VersionVerification",
                        remoteError ?? "Remote hashes do not match local files.");
                }

                foreach (LicenseGrantPublication grant in grants)
                    await UploadAndVerifyGrantVersionAsync(grant, publicKeyPath, verifier);

                foreach (LicenseGrantPublication grant in grants)
                    await SwitchAndVerifyGrantPointerAsync(grant);

                var pointer = new LicensePublicationPointer
                {
                    Revision = revision,
                    VersionPath = publicVersionPath,
                    ManifestSha256 = manifestHash,
                    SignatureSha256 = signatureHash,
                    PublishedAtUtc = DateTimeOffset.UtcNow
                };
                byte[] pointerBytes = new UTF8Encoding(false).GetBytes(
                    JsonConvert.SerializeObject(pointer, Formatting.Indented));
                string temporaryPointer = CombineRemotePath(
                    remoteRoot, ".licenses-current-" + Guid.NewGuid().ToString("N") + ".tmp");
                string finalPointer = CombineRemotePath(remoteRoot, "licenses-current.json");
                await UploadBytesAsync(pointerBytes, temporaryPointer);
                await MoveRemoteAsync(temporaryPointer, finalPointer);

                byte[] publishedPointerBytes = await ReadPublicBytesAsync("/licenses/licenses-current.json");
                var publishedPointer = JsonConvert.DeserializeObject<LicensePublicationPointer>(
                    Encoding.UTF8.GetString(publishedPointerBytes));
                if (publishedPointer == null || publishedPointer.Revision != revision ||
                    publishedPointer.VersionPath != publicVersionPath ||
                    publishedPointer.ManifestSha256 != manifestHash ||
                    publishedPointer.SignatureSha256 != signatureHash)
                {
                    throw new LicensePublicationException(
                        "PointerVerification", "Published pointer does not match requested revision.");
                }

                remoteJson = await ReadPublicBytesAsync(publishedPointer.VersionPath + "/licenses.json");
                remoteSignature = await ReadPublicBytesAsync(
                    publishedPointer.VersionPath + "/licenses.json.sig");
                if (!verifier.Verify(remoteJson, remoteSignature, publicKeyPath, out remoteError))
                    throw new LicensePublicationException("FinalSignatureVerification", remoteError);

                LicensePublicationJournal.Write(revision, "Complete", "Success",
                    publicVersionPath, manifestHash, signatureHash);
            }
            catch (LicensePublicationException ex)
            {
                LicensePublicationJournal.Write(revision, ex.Stage, "Failed",
                    publicVersionPath, manifestHash, signatureHash);
                throw;
            }
            catch (Exception ex)
            {
                LicensePublicationJournal.Write(revision, "Transport", "Failed",
                    publicVersionPath, manifestHash, signatureHash);
                throw new LicensePublicationException("Transport", ex.Message, ex);
            }
        }

        private async Task UploadAndVerifyGrantVersionAsync(
            LicenseGrantPublication grant,
            string publicKeyPath,
            LicenseDocumentService verifier)
        {
            string remoteGrantRoot = CombineRemotePath(
                YandexDiskSettings.RemoteDirectory,
                grant.GrantRoot.TrimStart('/'));
            string remoteVersions = CombineRemotePath(remoteGrantRoot, "versions");
            string remoteVersion = CombineRemotePath(
                remoteVersions,
                Path.GetFileName(grant.VersionPath));
            await EnsureDirectoryAsync(remoteGrantRoot);
            await EnsureDirectoryAsync(remoteVersions);
            await EnsureDirectoryAsync(remoteVersion);
            await UploadBytesAsync(
                grant.GrantBytes, CombineRemotePath(remoteVersion, "grant.json"));
            await UploadBytesAsync(
                grant.SignatureBytes, CombineRemotePath(remoteVersion, "grant.json.sig"));

            byte[] remoteGrant = await ReadPublicBytesAsync(grant.VersionPath + "/grant.json");
            byte[] remoteSignature =
                await ReadPublicBytesAsync(grant.VersionPath + "/grant.json.sig");
            string error = null;
            if (ComputeHash(remoteGrant) != ComputeHash(grant.GrantBytes) ||
                ComputeHash(remoteSignature) != ComputeHash(grant.SignatureBytes) ||
                !verifier.Verify(
                    remoteGrant, remoteSignature, publicKeyPath, out error))
                throw new LicensePublicationException(
                    "GrantVersionVerification",
                    error ?? "Remote grant hashes do not match local files.");
        }

        private async Task SwitchAndVerifyGrantPointerAsync(
            LicenseGrantPublication grant)
        {
            string remoteGrantRoot = CombineRemotePath(
                YandexDiskSettings.RemoteDirectory,
                grant.GrantRoot.TrimStart('/'));
            string temporaryPointer = CombineRemotePath(
                remoteGrantRoot,
                ".grant-current-" + Guid.NewGuid().ToString("N") + ".tmp");
            string finalPointer =
                CombineRemotePath(remoteGrantRoot, "grant-current.json");
            await UploadBytesAsync(grant.PointerBytes, temporaryPointer);
            await MoveRemoteAsync(temporaryPointer, finalPointer);

            byte[] publishedBytes =
                await ReadPublicBytesAsync(grant.GrantRoot + "/grant-current.json");
            var expected =
                JsonConvert.DeserializeObject<LicenseGrantPublicationPointer>(
                    Encoding.UTF8.GetString(grant.PointerBytes));
            var published =
                JsonConvert.DeserializeObject<LicenseGrantPublicationPointer>(
                    Encoding.UTF8.GetString(publishedBytes));
            if (expected == null || published == null ||
                published.Revision != expected.Revision ||
                published.VersionPath != expected.VersionPath ||
                published.GrantSha256 != expected.GrantSha256 ||
                published.SignatureSha256 != expected.SignatureSha256)
                throw new LicensePublicationException(
                    "GrantPointerVerification",
                    "Published grant pointer does not match the requested revision.");
        }
        protected virtual async Task UploadBytesAsync(byte[] bytes, string remotePath)
        {
            using var client = CreateAuthorizedClient(TimeSpan.FromMinutes(2));
            using var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var response = await client.PutAsync(BuildWebDavUri(remotePath), content);
            if (!response.IsSuccessStatusCode)
                throw new LicensePublicationException("Upload",
                    $"WebDAV PUT failed for {Path.GetFileName(remotePath)}: HTTP {(int)response.StatusCode}.");
        }

        protected virtual async Task MoveRemoteAsync(string sourcePath, string destinationPath)
        {
            using var client = CreateAuthorizedClient(TimeSpan.FromSeconds(60));
            using var request = new HttpRequestMessage(new HttpMethod("MOVE"), BuildWebDavUri(sourcePath));
            request.Headers.TryAddWithoutValidation("Destination", BuildWebDavUri(destinationPath).AbsoluteUri);
            request.Headers.TryAddWithoutValidation("Overwrite", "T");
            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                throw new LicensePublicationException("PointerSwitch",
                    $"WebDAV MOVE failed: HTTP {(int)response.StatusCode}.");
        }

        protected virtual async Task<byte[]> ReadPublicBytesAsync(string publicPath)
        {
            using var client = CreatePublicClient();
            string url = await GetPublicDownloadUrl(client, publicPath);
            if (string.IsNullOrWhiteSpace(url))
                throw new LicensePublicationException("RemoteRead",
                    $"Public URL not returned for {publicPath}.");
            return await client.GetByteArrayAsync(url);
        }

        private static string ComputeHash(byte[] bytes)
        {
            using SHA256 sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(bytes));
        }

        public AvailableVersionsData BuildAvailableVersions(Dictionary<string, string> files)
        {
            var result = new AvailableVersionsData();

            foreach (var fileName in files.Keys)
            {
                if (fileName.Equals("ips_encrypted.json", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals("versions.json", StringComparison.OrdinalIgnoreCase))
                    continue;

                var version = ExtractVersion(fileName);
                if (string.IsNullOrWhiteSpace(version))
                    continue;

                var lower = fileName.ToLowerInvariant();

                if (IsHonestFlowVersionFolder(fileName, version))
                {
                    AddUnique(result.HonestFlow, version);
                }
                else if (lower.StartsWith("regime-"))
                {
                    AddUnique(result.LmModule, version);
                }
                else if (lower.StartsWith("kkt10-"))
                {
                    AddUnique(result.AtolDriver, version);
                }
                else if (lower.StartsWith("esm-lm-controller_"))
                {
                    AddUnique(result.Controller, version);
                }
                else if (lower.StartsWith("esm_"))
                {
                    AddUnique(result.ESM, version);
                }
            }

            SortVersions(result.LmModule);
            SortVersions(result.AtolDriver);
            SortVersions(result.ESM);
            SortVersions(result.Controller);
            SortVersions(result.HonestFlow);

            return result;
        }

        private async Task UploadFileAsync(string localPath, string fileName)
        {
            await UploadFileToRemotePathAsync(
                localPath,
                CombineRemotePath(YandexDiskSettings.RemoteDirectory, fileName));
        }

        private async Task UploadFileToRemotePathAsync(string localPath, string remotePath)
        {
            using var client = CreateAuthorizedClient(TimeSpan.FromMinutes(10));
            var fileInfo = new FileInfo(localPath);
            ConfigEditorLogger.Info($"Uploading file to WebDAV. Local: {localPath}. Remote: {remotePath}. Bytes: {fileInfo.Length}");

            await using var stream = new FileStream(
                localPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                true);

            using var content = new StreamContent(stream, 1024 * 1024);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = fileInfo.Length;

            var response = await client.PutAsync(BuildWebDavUri(remotePath), content);
            var text = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                ConfigEditorLogger.Info($"WebDAV upload failed. Remote: {remotePath}. Code: {(int)response.StatusCode}. Response: {text}");
                throw new Exception(
                    $"Could not upload {Path.GetFileName(localPath)} to Yandex Disk. " +
                    $"Code: {(int)response.StatusCode}. Response: {text}");
            }

            ConfigEditorLogger.Info($"WebDAV upload completed. Remote: {remotePath}. Code: {(int)response.StatusCode}");
        }

        protected virtual async Task EnsureDirectoryAsync(string remotePath)
        {
            using var client = CreateAuthorizedClient(TimeSpan.FromSeconds(60));
            var request = new HttpRequestMessage(new HttpMethod("MKCOL"), BuildWebDavUri(remotePath));
            ConfigEditorLogger.Info($"Ensuring WebDAV directory exists: {remotePath}");
            var response = await client.SendAsync(request);

            if (response.IsSuccessStatusCode || (int)response.StatusCode == 405)
            {
                ConfigEditorLogger.Info($"WebDAV directory ready: {remotePath}. Code: {(int)response.StatusCode}");
                return;
            }

            var text = await response.Content.ReadAsStringAsync();
            ConfigEditorLogger.Info($"WebDAV directory creation failed. Remote: {remotePath}. Code: {(int)response.StatusCode}. Response: {text}");
            throw new Exception(
                $"Could not create Yandex Disk folder {remotePath}. " +
                $"Code: {(int)response.StatusCode}. Response: {text}");
        }

        private static HttpClient CreatePublicClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "HonestDesk/2.0 YandexDisk");
            client.Timeout = TimeSpan.FromSeconds(60);

            return client;
        }

        private static HttpClient CreateAuthorizedClient(TimeSpan? timeout = null)
        {
            if (!YandexDiskSettings.IsConfigured)
            {
                throw new Exception(
                    "Не настроен доступ к Яндекс.Диску. " +
                    "Откройте в HonestDesk раздел «Доступ к Яндекс.Диску».");
            }

            var client = new HttpClient();
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{YandexDiskSettings.Login}:{YandexDiskSettings.AppPassword}"));

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
            client.Timeout = timeout ?? TimeSpan.FromSeconds(60);

            return client;
        }

        private static string BuildPublicResourcesUrl()
        {
            return PublicResourcesApi +
                "?public_key=" + Uri.EscapeDataString(YandexDiskSettings.PublicUrl) +
                "&limit=1000";
        }

        private static string BuildPublicDownloadUrl(string path)
        {
            return PublicDownloadApi +
                "?public_key=" + Uri.EscapeDataString(YandexDiskSettings.PublicUrl) +
                "&path=" + Uri.EscapeDataString(path);
        }

        private static async Task<string> GetPublicDownloadUrl(HttpClient client, string path)
        {
            var json = await client.GetStringAsync(BuildPublicDownloadUrl(path));
            var payload = JObject.Parse(json);
            return (string)payload["href"];
        }

        private static Uri BuildWebDavUri(string remotePath)
        {
            var escapedPath = string.Join(
                "/",
                NormalizeRemotePath(remotePath)
                    .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(Uri.EscapeDataString));

            return new Uri(WebDavBaseUri, "/" + escapedPath);
        }

        private static string CombineRemotePath(string directory, string fileName)
        {
            return $"{NormalizeRemotePath(directory).TrimEnd('/')}/{fileName}";
        }

        private static string NormalizeRemotePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "/";

            return "/" + path.Replace('\\', '/').Trim('/');
        }

        private static string ExtractVersion(string fileName)
        {
            var fullNameMatch = Regex.Match(fileName.Trim('/'), @"^\d+\.\d+\.\d+(?:\.\d+)?$");
            if (fullNameMatch.Success)
                return fullNameMatch.Value;

            var name = Path.GetFileNameWithoutExtension(fileName);
            var match = Regex.Match(name, @"\d+(?:\.\d+)+(?:[-_]\d+)?");

            return match.Success
                ? match.Value.Replace("_", "-")
                : null;
        }

        private static bool IsHonestFlowVersionFolder(string fileName, string version)
        {
            return string.Equals(fileName.Trim('/'), version, StringComparison.OrdinalIgnoreCase)
                && Version.TryParse(version, out _);
        }

        private static void AddUnique(List<string> list, string version)
        {
            if (!list.Contains(version))
                list.Add(version);
        }

        private static void SortVersions(List<string> list)
        {
            list.Sort((a, b) =>
            {
                try
                {
                    return Version.Parse(b.Replace("-", "."))
                        .CompareTo(Version.Parse(a.Replace("-", ".")));
                }
                catch
                {
                    return string.Compare(b, a, StringComparison.OrdinalIgnoreCase);
                }
            });
        }
    }
}
