using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using HonestDesk.Infrastructure.Api;
using HonestDesk.Infrastructure.Releases;
using Xunit;

namespace HonestDesk.Tests;

public sealed class ReleasePublicationServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "HonestDesk.ReleaseTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Publication_signs_locally_uploads_directly_and_completes_with_etag()
    {
        Directory.CreateDirectory(_directory);
        string binaryPath = Path.Combine(_directory, "esm.exe");
        byte[] binary = Encoding.UTF8.GetBytes("signed release bytes");
        await File.WriteAllBytesAsync(binaryPath, binary);
        char[] password = "release-password-123".ToCharArray();
        string keyPath = Path.Combine(_directory, "release.hfrelkey");
        await File.WriteAllBytesAsync(keyPath, CreateEncryptedKey(password));
        var apiHandler = new ReleaseApiHandler(binary.LongLength);
        var storageHandler = new StorageHandler();
        var api = new HonestLicenseAdminApiClient("https://api.test", () => "admin-key", apiHandler);
        var service = new ReleasePublicationService(api, storageHandler);

        ReleasePublicationResult result = await service.PublishAsync(
            new ReleasePublicationRequest(keyPath, password, binaryPath,
                "ESM", "1.7.0", "any"),
            progress: null, CancellationToken.None);

        Assert.False(result.UploadSkipped);
        Assert.StartsWith("release_", result.KeyId);
        Assert.Equal(binary, storageHandler.UploadedBytes);
        Assert.True(apiHandler.CreateManifestWasSigned);
        Assert.Contains("\"partNumber\":1", apiHandler.CompleteBody);
        Assert.Contains("\"eTag\":\"\\\"etag-1\\\"\"", apiHandler.CompleteBody);
        Assert.Equal(0, apiHandler.SetCurrentCalls);
    }

    [Fact]
    public void Distribution_ui_exposes_catalog_client_resolution_and_secure_upload_controls()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string xaml = File.ReadAllText(Path.Combine(root, "HonestDesk", "DistributionsWindow.xaml"));
        string code = File.ReadAllText(Path.Combine(root, "HonestDesk", "DistributionsWindow.xaml.cs"));
        string main = File.ReadAllText(Path.Combine(root, "HonestDesk", "MainWindow.xaml"));

        Assert.Contains("Каталог и выдача", xaml);
        Assert.Contains("Что получит HonestFlow", xaml);
        Assert.Contains("PasswordBox x:Name=\"KeyPasswordBox\"", xaml);
        Assert.Contains("SetCurrentCheck", xaml);
        Assert.Contains("KeyPasswordBox.Clear()", code);
        Assert.Contains("CryptographicOperations.ZeroMemory", code);
        Assert.Contains("Дистрибутивы", main);
    }

    private static byte[] CreateEncryptedKey(ReadOnlySpan<char> password)
    {
        byte[] magic = Encoding.ASCII.GetBytes("HFRK1");
        const int saltLength = 16;
        const int nonceLength = 12;
        const int tagLength = 16;
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] privateKey = key.ExportECPrivateKey();
        byte[] result = new byte[magic.Length + saltLength + nonceLength + tagLength + privateKey.Length];
        magic.CopyTo(result, 0);
        Span<byte> salt = result.AsSpan(magic.Length, saltLength);
        Span<byte> nonce = result.AsSpan(magic.Length + saltLength, nonceLength);
        Span<byte> tag = result.AsSpan(magic.Length + saltLength + nonceLength, tagLength);
        Span<byte> ciphertext = result.AsSpan(magic.Length + saltLength + nonceLength + tagLength);
        RandomNumberGenerator.Fill(salt);
        RandomNumberGenerator.Fill(nonce);
        byte[] derived = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, 250_000, HashAlgorithmName.SHA256, 32);
        try
        {
            using AesGcm cipher = new(derived, tagLength);
            cipher.Encrypt(nonce, privateKey, ciphertext, tag, magic);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
            CryptographicOperations.ZeroMemory(derived);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class ReleaseApiHandler(long sizeBytes) : HttpMessageHandler
    {
        public bool CreateManifestWasSigned { get; private set; }
        public string CompleteBody { get; private set; } = "";
        public int SetCurrentCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("admin-key", request.Headers.GetValues("X-Admin-Key").Single());
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/admin/assets")
                return Json(HttpStatusCode.OK, "[]");
            if (request.Method == HttpMethod.Post && path == "/api/admin/release-uploads")
            {
                string body = await request.Content!.ReadAsStringAsync(cancellationToken);
                CreateManifestWasSigned = body.Contains("manifestBase64") &&
                                          body.Contains("signatureBase64") &&
                                          !body.Contains("signed release bytes");
                return Json(HttpStatusCode.OK,
                    $$"""{"id":7,"component":"ESM","version":"1.7.0","architecture":"any","fileName":"esm.exe","sizeBytes":{{sizeBytes}},"sha256":"{{new string('a', 64)}}","partSizeBytes":{{sizeBytes}},"partCount":1}""");
            }
            if (request.Method == HttpMethod.Post && path == "/api/admin/release-uploads/7/parts/1")
                return Json(HttpStatusCode.OK,
                    "{\"partNumber\":1,\"uploadUrl\":\"https://storage.test/part-1\",\"uploadUrlExpiresAtUtc\":\"2030-01-01T00:00:00Z\"}");
            if (request.Method == HttpMethod.Post && path == "/api/admin/release-uploads/7/complete")
            {
                CompleteBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Put && path.StartsWith("/api/admin/versions/", StringComparison.Ordinal))
            {
                SetCurrentCalls++;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StorageHandler : HttpMessageHandler
    {
        public byte[] UploadedBytes { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("storage.test", request.RequestUri!.Host);
            UploadedBytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.ETag = new EntityTagHeaderValue("\"etag-1\"");
            return response;
        }
    }
}
