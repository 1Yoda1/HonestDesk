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
using System.Xml.Linq;

namespace HonestDesk.Infrastructure
{
    public class YandexDiskClient
    {
        private const string PublicResourcesApi = "https://cloud-api.yandex.net/v1/disk/public/resources";
        private const int GrantPublicationConcurrency = 2;
        private const int GrantPublicationMaxAttempts = 3;
        private static readonly TimeSpan GrantUploadTimeout = TimeSpan.FromMinutes(10);
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
            var grantBuilder =
                new IncrementalLicenseGrantPublicationBuilder(keyId, privateKeyPath);
            IReadOnlyList<LicenseGrantTarget> grantTargets =
                grantBuilder.BuildTargets(manifest);
            var verifier = new LicenseDocumentService();

            if (!verifier.Verify(json, signature, publicKeyPath, out string localError))
                throw new LicensePublicationException("LocalSignatureVerification", localError);

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

                string remoteGrantsRoot = CombineRemotePath(remoteRoot, "grants");
                await EnsureDirectoryAsync(remoteGrantsRoot);
                LicenseGrantPublicationIndex grantIndex =
                    await LoadOrBootstrapGrantIndexAsync(
                        grantTargets, keyId, publicKeyPath, verifier);
                bool grantCoverageChanged = !grantIndex.CoverageComplete;
                if (grantCoverageChanged)
                {
                    await ReconcileGrantIndexCoverageAsync(
                        grantIndex, publicKeyPath, verifier);
                    grantIndex.CoverageComplete = true;
                }
                var changedGrants = new List<IncrementalLicenseGrantPublication>();
                var desiredHashes = grantTargets
                    .Select(target => target.TargetHash)
                    .ToHashSet(StringComparer.Ordinal);
                bool keyChanged =
                    !string.Equals(grantIndex.KeyId, keyId, StringComparison.Ordinal);
                foreach (LicenseGrantTarget target in grantTargets)
                {
                    grantIndex.Entries.TryGetValue(target.TargetHash, out var previous);
                    if (!keyChanged && previous != null && !previous.Revoked &&
                        previous.SemanticSha256 == target.SemanticSha256 &&
                        previous.ValidUntilUtc > manifest.IssuedAtUtc.AddDays(30))
                        continue;
                    changedGrants.Add(grantBuilder.BuildChanged(
                        target,
                        previous,
                        manifest.IssuedAtUtc,
                        manifest.ValidUntilUtc));
                }
                foreach (var pair in grantIndex.Entries.ToList())
                {
                    if (desiredHashes.Contains(pair.Key) || pair.Value.Revoked)
                        continue;
                    changedGrants.Add(grantBuilder.BuildRevocation(
                        pair.Value,
                        manifest.IssuedAtUtc,
                        manifest.ValidUntilUtc));
                }
                foreach (IncrementalLicenseGrantPublication item in changedGrants)
                    if (!verifier.Verify(
                        item.Publication.GrantBytes,
                        item.Publication.SignatureBytes,
                        publicKeyPath,
                        out localError))
                        throw new LicensePublicationException(
                            "LocalGrantSignatureVerification", localError);
                ConfigEditorLogger.Info(
                    $"Grant publication plan: desired={grantTargets.Count}; " +
                    $"changed-or-new-or-revoked={changedGrants.Count}; " +
                    $"skipped={grantTargets.Count - changedGrants.Count(item => !item.Revoked)}.");
                IReadOnlyList<LicenseGrantPublication> grantPublications =
                    changedGrants.Select(item => item.Publication).ToList();
                await RunGrantBatchAsync(
                    grantPublications,
                    grant => UploadAndVerifyGrantVersionAsync(grant, publicKeyPath, verifier));
                await RunGrantBatchAsync(grantPublications, SwitchAndVerifyGrantPointerAsync);
                foreach (IncrementalLicenseGrantPublication item in changedGrants)
                    grantIndex.Entries[item.Target.TargetHash] =
                        grantBuilder.CreateIndexEntry(item);
                if (changedGrants.Count > 0 || grantCoverageChanged)
                {
                    grantIndex.LegacyManifestRevision = revision;
                    grantIndex.KeyId = keyId;
                    grantIndex.PublishedAtUtc = DateTimeOffset.UtcNow;
                    await PublishGrantIndexAsync(
                        grantIndex, grantBuilder, publicKeyPath, verifier, remoteRoot);
                }
                else
                {
                    ConfigEditorLogger.Info("Grant index is unchanged; publication skipped.");
                }

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




        private async Task ReconcileGrantIndexCoverageAsync(
            LicenseGrantPublicationIndex index,
            string publicKeyPath,
            LicenseDocumentService verifier)
        {
            IReadOnlyList<string> remoteTargetHashes =
                await ListRemoteGrantTargetHashesAsync();
            string[] missing = remoteTargetHashes
                .Where(hash => !index.Entries.ContainsKey(hash))
                .ToArray();
            if (missing.Length == 0)
            {
                ConfigEditorLogger.Info(
                    $"Grant index coverage is complete: {index.Entries.Count} targets.");
                return;
            }

            ConfigEditorLogger.Info(
                $"Grant index coverage found {missing.Length} unindexed remote targets.");
            using var concurrency = new SemaphoreSlim(GrantPublicationConcurrency);
            Task<KeyValuePair<string, LicenseGrantPublicationIndexEntry>?>[] imports =
                missing.Select(async targetHash =>
                {
                    await concurrency.WaitAsync();
                    try
                    {
                        return await ImportRemoteGrantByHashAsync(
                            targetHash, publicKeyPath, verifier);
                    }
                    finally
                    {
                        concurrency.Release();
                    }
                }).ToArray();
            foreach (var imported in await Task.WhenAll(imports))
                if (imported.HasValue)
                    index.Entries[imported.Value.Key] = imported.Value.Value;
        }

        private static async Task<IReadOnlyList<string>> ListRemoteGrantTargetHashesAsync()
        {
            string remoteGrantsRoot = CombineRemotePath(
                YandexDiskSettings.RemoteDirectory,
                "licenses/grants");
            using var client = CreateAuthorizedClient(TimeSpan.FromMinutes(5));
            using var request = new HttpRequestMessage(
                new HttpMethod("PROPFIND"),
                BuildWebDavUri(remoteGrantsRoot));
            request.Headers.TryAddWithoutValidation("Depth", "1");
            using HttpResponseMessage response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                throw new LicensePublicationException(
                    "GrantIndexCoverage",
                    $"WebDAV PROPFIND failed for grants: HTTP {(int)response.StatusCode}.");

            string xml = await response.Content.ReadAsStringAsync();
            XDocument document;
            try
            {
                document = XDocument.Parse(xml);
            }
            catch (Exception exception) when (
                exception is System.Xml.XmlException ||
                exception is InvalidOperationException)
            {
                throw new LicensePublicationException(
                    "GrantIndexCoverage",
                    "WebDAV grants listing returned invalid XML.",
                    exception);
            }

            return document
                .Descendants()
                .Where(element => element.Name.LocalName == "href")
                .Select(element => element.Value)
                .Select(href =>
                {
                    string path = Uri.TryCreate(href, UriKind.Absolute, out Uri? uri)
                        ? uri.AbsolutePath
                        : href;
                    return Uri.UnescapeDataString(
                        path.Replace('\\', '/').TrimEnd('/').Split('/').LastOrDefault() ?? "");
                })
                .Where(name => Regex.IsMatch(name, "^[0-9a-fA-F]{64}$"))
                .Select(name => name.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        private async Task<KeyValuePair<string, LicenseGrantPublicationIndexEntry>?>
            ImportRemoteGrantByHashAsync(
                string targetHash,
                string publicKeyPath,
                LicenseDocumentService verifier)
        {
            string grantRoot = "/licenses/grants/" + targetHash;
            string remotePointerPath = CombineRemotePath(
                YandexDiskSettings.RemoteDirectory,
                grantRoot.TrimStart('/') + "/grant-current.json");
            byte[]? pointerBytes =
                await TryReadAuthorizedBytesAsync(remotePointerPath);
            if (pointerBytes == null)
            {
                ConfigEditorLogger.Info(
                    $"Ignoring inactive grant directory without pointer: {targetHash}.");
                return null;
            }
            var pointer =
                JsonConvert.DeserializeObject<LicenseGrantPublicationPointer>(
                    Encoding.UTF8.GetString(pointerBytes));
            if (pointer == null)
                throw new LicensePublicationException(
                    "GrantIndexCoverage",
                    $"Remote grant pointer is invalid for {targetHash}.");

            string remoteVersionPath = CombineRemotePath(
                YandexDiskSettings.RemoteDirectory,
                pointer.VersionPath.TrimStart('/'));
            byte[]? grantBytes = await TryReadAuthorizedBytesAsync(
                CombineRemotePath(remoteVersionPath, "grant.json"));
            byte[]? signatureBytes = await TryReadAuthorizedBytesAsync(
                CombineRemotePath(remoteVersionPath, "grant.json.sig"));
            string? error = null;
            if (grantBytes == null || signatureBytes == null ||
                ComputeHash(grantBytes) != pointer.GrantSha256 ||
                ComputeHash(signatureBytes) != pointer.SignatureSha256 ||
                !verifier.Verify(
                    grantBytes, signatureBytes, publicKeyPath, out error))
                throw new LicensePublicationException(
                    "GrantIndexCoverage",
                    error ?? $"Remote grant verification failed for {targetHash}.");

            var grant = JsonConvert.DeserializeObject<LicenseGrant>(
                Encoding.UTF8.GetString(grantBytes))
                ?? throw new LicensePublicationException(
                    "GrantIndexCoverage",
                    $"Remote grant JSON is invalid for {targetHash}.");
            string computedTargetHash =
                IncrementalLicenseGrantPublicationBuilder.TargetHash(
                    grant.ClientId, grant.DeviceId);
            if (!string.Equals(
                    computedTargetHash, targetHash, StringComparison.Ordinal))
                throw new LicensePublicationException(
                    "GrantIndexCoverage",
                    $"Remote grant identity does not match directory {targetHash}.");

            var entry = new LicenseGrantPublicationIndexEntry
            {
                ClientId = grant.ClientId,
                DeviceId = grant.DeviceId,
                SemanticSha256 =
                    IncrementalLicenseGrantPublicationBuilder.SemanticHash(grant),
                GrantRevision = pointer.Revision,
                VersionPath = pointer.VersionPath,
                GrantSha256 = pointer.GrantSha256,
                SignatureSha256 = pointer.SignatureSha256,
                IssuedAtUtc = grant.IssuedAtUtc,
                ValidUntilUtc = grant.ValidUntilUtc,
                Revoked = false
            };
            return new KeyValuePair<string, LicenseGrantPublicationIndexEntry>(
                targetHash, entry);
        }

        private async Task<LicenseGrantPublicationIndex> LoadOrBootstrapGrantIndexAsync(
            IReadOnlyList<LicenseGrantTarget> targets,
            string keyId,
            string publicKeyPath,
            LicenseDocumentService verifier)
        {
            string remotePointerPath = CombineRemotePath(
                YandexDiskSettings.RemoteDirectory,
                "licenses/grants-index-current.json");
            byte[]? pointerBytes = await TryReadAuthorizedBytesAsync(remotePointerPath);
            if (pointerBytes == null)
            {
                ConfigEditorLogger.Info(
                    "Grant publication index is absent; importing existing personal pointers once.");
                return await BootstrapGrantIndexAsync(
                    targets, keyId, publicKeyPath, verifier);
            }

            var pointer = JsonConvert.DeserializeObject<LicenseGrantIndexPublicationPointer>(
                Encoding.UTF8.GetString(pointerBytes))
                ?? throw new LicensePublicationException(
                    "GrantIndexRead", "Published grant index pointer is invalid.");
            byte[] indexBytes =
                await ReadPublicBytesAsync(pointer.VersionPath + "/index.json");
            byte[] signatureBytes =
                await ReadPublicBytesAsync(pointer.VersionPath + "/index.json.sig");
            string? error = null;
            if (ComputeHash(indexBytes) != pointer.IndexSha256 ||
                ComputeHash(signatureBytes) != pointer.SignatureSha256 ||
                !verifier.Verify(indexBytes, signatureBytes, publicKeyPath, out error))
                throw new LicensePublicationException(
                    "GrantIndexVerification",
                    error ?? "Published grant index hashes do not match.");

            var index = JsonConvert.DeserializeObject<LicenseGrantPublicationIndex>(
                Encoding.UTF8.GetString(indexBytes))
                ?? throw new LicensePublicationException(
                    "GrantIndexRead", "Published grant index is invalid.");
            index.Entries ??=
                new Dictionary<string, LicenseGrantPublicationIndexEntry>(
                    StringComparer.Ordinal);
            return index;
        }

        private async Task<LicenseGrantPublicationIndex> BootstrapGrantIndexAsync(
            IReadOnlyList<LicenseGrantTarget> targets,
            string keyId,
            string publicKeyPath,
            LicenseDocumentService verifier)
        {
            using var concurrency = new SemaphoreSlim(GrantPublicationConcurrency);
            Task<KeyValuePair<string, LicenseGrantPublicationIndexEntry>?>[] imports =
                targets.Select(async target =>
                {
                    await concurrency.WaitAsync();
                    try
                    {
                        return await ImportExistingGrantAsync(
                            target, publicKeyPath, verifier);
                    }
                    finally
                    {
                        concurrency.Release();
                    }
                }).ToArray();
            var index = new LicenseGrantPublicationIndex { KeyId = keyId };
            foreach (var imported in await Task.WhenAll(imports))
                if (imported.HasValue)
                    index.Entries[imported.Value.Key] = imported.Value.Value;
            ConfigEditorLogger.Info(
                $"Grant publication index bootstrap imported {index.Entries.Count} " +
                $"of {targets.Count} desired targets.");
            return index;
        }

        private async Task<KeyValuePair<string, LicenseGrantPublicationIndexEntry>?>
            ImportExistingGrantAsync(
                LicenseGrantTarget target,
                string publicKeyPath,
                LicenseDocumentService verifier)
        {
            string remotePointerPath = CombineRemotePath(
                YandexDiskSettings.RemoteDirectory,
                target.GrantRoot.TrimStart('/') + "/grant-current.json");
            byte[]? pointerBytes =
                await TryReadAuthorizedBytesAsync(remotePointerPath);
            if (pointerBytes == null)
                return null;

            var pointer = JsonConvert.DeserializeObject<LicenseGrantPublicationPointer>(
                Encoding.UTF8.GetString(pointerBytes))
                ?? throw new LicensePublicationException(
                    "GrantIndexBootstrap",
                    $"Existing pointer is invalid for {target.GrantRoot}.");
            string remoteVersionPath = CombineRemotePath(
                YandexDiskSettings.RemoteDirectory,
                pointer.VersionPath.TrimStart('/'));
            byte[]? grantBytes = await TryReadAuthorizedBytesAsync(
                CombineRemotePath(remoteVersionPath, "grant.json"));
            byte[]? signatureBytes = await TryReadAuthorizedBytesAsync(
                CombineRemotePath(remoteVersionPath, "grant.json.sig"));
            string? error = null;
            if (grantBytes == null || signatureBytes == null ||
                ComputeHash(grantBytes) != pointer.GrantSha256 ||
                ComputeHash(signatureBytes) != pointer.SignatureSha256 ||
                !verifier.Verify(
                    grantBytes, signatureBytes, publicKeyPath, out error))
                throw new LicensePublicationException(
                    "GrantIndexBootstrap",
                    error ?? $"Existing grant verification failed for {target.GrantRoot}.");

            var grant = JsonConvert.DeserializeObject<LicenseGrant>(
                Encoding.UTF8.GetString(grantBytes))
                ?? throw new LicensePublicationException(
                    "GrantIndexBootstrap",
                    $"Existing grant is invalid for {target.GrantRoot}.");
            if (!string.Equals(
                    grant.ClientId, target.Grant.ClientId, StringComparison.Ordinal) ||
                !string.Equals(
                    grant.DeviceId, target.Grant.DeviceId, StringComparison.Ordinal))
                throw new LicensePublicationException(
                    "GrantIndexBootstrap",
                    $"Existing grant identity does not match {target.GrantRoot}.");

            var entry = new LicenseGrantPublicationIndexEntry
            {
                ClientId = grant.ClientId,
                DeviceId = grant.DeviceId,
                SemanticSha256 =
                    IncrementalLicenseGrantPublicationBuilder.SemanticHash(grant),
                GrantRevision = pointer.Revision,
                VersionPath = pointer.VersionPath,
                GrantSha256 = pointer.GrantSha256,
                SignatureSha256 = pointer.SignatureSha256,
                IssuedAtUtc = grant.IssuedAtUtc,
                ValidUntilUtc = grant.ValidUntilUtc,
                Revoked = false
            };
            return new KeyValuePair<string, LicenseGrantPublicationIndexEntry>(
                target.TargetHash, entry);
        }

        private async Task PublishGrantIndexAsync(
            LicenseGrantPublicationIndex index,
            IncrementalLicenseGrantPublicationBuilder builder,
            string publicKeyPath,
            LicenseDocumentService verifier,
            string remoteLicensesRoot)
        {
            string versionName =
                "legacy-revision-" + index.LegacyManifestRevision.ToString("D20");
            string publicVersionPath =
                "/licenses/grant-index/versions/" + versionName;
            string remoteIndexRoot =
                CombineRemotePath(remoteLicensesRoot, "grant-index");
            string remoteVersions =
                CombineRemotePath(remoteIndexRoot, "versions");
            string remoteVersion =
                CombineRemotePath(remoteVersions, versionName);
            await EnsureDirectoryAsync(remoteIndexRoot);
            await EnsureDirectoryAsync(remoteVersions);
            await EnsureDirectoryAsync(remoteVersion);

            (byte[] indexBytes, byte[] signatureBytes) = builder.SignIndex(index);
            await UploadBytesAsync(
                indexBytes, CombineRemotePath(remoteVersion, "index.json"));
            await UploadBytesAsync(
                signatureBytes, CombineRemotePath(remoteVersion, "index.json.sig"));
            byte[] remoteIndex =
                await ReadPublicBytesAsync(publicVersionPath + "/index.json");
            byte[] remoteSignature =
                await ReadPublicBytesAsync(publicVersionPath + "/index.json.sig");
            string indexHash = ComputeHash(indexBytes);
            string signatureHash = ComputeHash(signatureBytes);
            string? error = null;
            if (ComputeHash(remoteIndex) != indexHash ||
                ComputeHash(remoteSignature) != signatureHash ||
                !verifier.Verify(
                    remoteIndex, remoteSignature, publicKeyPath, out error))
                throw new LicensePublicationException(
                    "GrantIndexVerification",
                    error ?? "Uploaded grant index verification failed.");

            var pointer = new LicenseGrantIndexPublicationPointer
            {
                LegacyManifestRevision = index.LegacyManifestRevision,
                VersionPath = publicVersionPath,
                IndexSha256 = indexHash,
                SignatureSha256 = signatureHash,
                PublishedAtUtc = DateTimeOffset.UtcNow
            };
            byte[] pointerBytes = new UTF8Encoding(false).GetBytes(
                JsonConvert.SerializeObject(pointer, Formatting.Indented));
            string temporaryPointer = CombineRemotePath(
                remoteLicensesRoot,
                ".grants-index-current-" + Guid.NewGuid().ToString("N") + ".tmp");
            string finalPointer =
                CombineRemotePath(remoteLicensesRoot, "grants-index-current.json");
            await UploadBytesAsync(pointerBytes, temporaryPointer);
            await MoveRemoteAsync(temporaryPointer, finalPointer);

            byte[] publishedPointerBytes =
                await ReadPublicBytesAsync("/licenses/grants-index-current.json");
            var published =
                JsonConvert.DeserializeObject<LicenseGrantIndexPublicationPointer>(
                    Encoding.UTF8.GetString(publishedPointerBytes));
            if (published == null ||
                published.LegacyManifestRevision != pointer.LegacyManifestRevision ||
                published.VersionPath != pointer.VersionPath ||
                published.IndexSha256 != pointer.IndexSha256 ||
                published.SignatureSha256 != pointer.SignatureSha256)
                throw new LicensePublicationException(
                    "GrantIndexPointerVerification",
                    "Published grant index pointer does not match.");
        }

        private static async Task<byte[]?> TryReadAuthorizedBytesAsync(string remotePath)
        {
            using var client = CreateAuthorizedClient(TimeSpan.FromMinutes(2));
            using HttpResponseMessage response =
                await client.GetAsync(BuildWebDavUri(remotePath));
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
                throw new LicensePublicationException(
                    "RemoteRead",
                    $"WebDAV GET failed for {Path.GetFileName(remotePath)}: " +
                    $"HTTP {(int)response.StatusCode}.");
            return await response.Content.ReadAsByteArrayAsync();
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

        private static async Task RunGrantBatchAsync(
            IReadOnlyList<LicenseGrantPublication> grants,
            Func<LicenseGrantPublication, Task> action)
        {
            using var concurrency = new SemaphoreSlim(GrantPublicationConcurrency);
            await Task.WhenAll(grants.Select(async grant =>
            {
                await concurrency.WaitAsync();
                try
                {
                    await RunGrantActionWithRetryAsync(grant, action);
                }
                finally
                {
                    concurrency.Release();
                }
            }));
        }

        private static async Task RunGrantActionWithRetryAsync(
            LicenseGrantPublication grant,
            Func<LicenseGrantPublication, Task> action)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await action(grant);
                    return;
                }
                catch (Exception exception) when (
                    attempt < GrantPublicationMaxAttempts && IsTransientGrantFailure(exception))
                {
                    var delay = TimeSpan.FromSeconds(attempt == 1 ? 1 : 3);
                    ConfigEditorLogger.Info(
                        $"Transient grant publication failure for {grant.GrantRoot}; " +
                        $"retry {attempt + 1}/{GrantPublicationMaxAttempts} in {delay.TotalSeconds:0}s: " +
                        exception.Message);
                    await Task.Delay(delay);
                }
            }
        }

        private static bool IsTransientGrantFailure(Exception exception)
        {
            if (exception is HttpRequestException ||
                exception is TaskCanceledException ||
                exception is IOException)
                return true;

            if (exception is LicensePublicationException publicationException)
                return publicationException.Stage == "Upload" ||
                       publicationException.Stage == "RemoteRead" ||
                       publicationException.Stage == "PointerSwitch" ||
                       publicationException.Stage == "Transport";

            return false;
        }
        protected virtual async Task UploadBytesAsync(byte[] bytes, string remotePath)
        {
            using var client = CreateAuthorizedClient(GrantUploadTimeout);
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
            ConfigEditorLogger.Info($"Ensuring WebDAV directory exists: {remotePath}");
            if (await RemoteDirectoryExistsAsync(client, remotePath))
            {
                ConfigEditorLogger.Info($"WebDAV directory already exists: {remotePath}");
                return;
            }

            using var request = new HttpRequestMessage(
                new HttpMethod("MKCOL"),
                BuildWebDavUri(remotePath));
            using var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode ||
                (int)response.StatusCode == 405 ||
                await RemoteDirectoryExistsAsync(client, remotePath))
            {
                ConfigEditorLogger.Info($"WebDAV directory ready: {remotePath}. Code: {(int)response.StatusCode}");
                return;
            }

            string text = await response.Content.ReadAsStringAsync();
            ConfigEditorLogger.Info($"WebDAV directory creation failed. Remote: {remotePath}. Code: {(int)response.StatusCode}. Response: {text}");
            throw new Exception(
                $"Could not create Yandex Disk folder {remotePath}. " +
                $"Code: {(int)response.StatusCode}. Response: {text}");
        }

        private static async Task<bool> RemoteDirectoryExistsAsync(
            HttpClient client,
            string remotePath)
        {
            using var request = new HttpRequestMessage(
                new HttpMethod("PROPFIND"),
                BuildWebDavUri(remotePath));
            request.Headers.TryAddWithoutValidation("Depth", "0");
            using HttpResponseMessage response = await client.SendAsync(request);
            return response.IsSuccessStatusCode;
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
