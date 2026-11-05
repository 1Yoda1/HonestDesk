using System.Security.Cryptography;
using HonestDesk.Infrastructure.Diagnostics;
using Xunit;

namespace HonestDesk.Tests;

public sealed class DiagnosticArchiveFileVerifierTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "HonestDesk.DiagnosticArchiveTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task VerifyAsync_accepts_file_matching_server_metadata()
    {
        byte[] content = "diagnostic archive"u8.ToArray();
        string path = await WriteAsync(content);

        await DiagnosticArchiveFileVerifier.VerifyAsync(
            path, content.LongLength, Convert.ToHexString(SHA256.HashData(content)), CancellationToken.None);
    }

    [Fact]
    public async Task VerifyAsync_rejects_file_with_unexpected_size()
    {
        byte[] content = "diagnostic archive"u8.ToArray();
        string path = await WriteAsync(content);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            DiagnosticArchiveFileVerifier.VerifyAsync(
                path, content.LongLength + 1, Convert.ToHexString(SHA256.HashData(content)), CancellationToken.None));

        Assert.Contains("Размер", error.Message);
    }

    [Fact]
    public async Task VerifyAsync_rejects_file_with_unexpected_hash()
    {
        byte[] content = "diagnostic archive"u8.ToArray();
        string path = await WriteAsync(content);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            DiagnosticArchiveFileVerifier.VerifyAsync(path, content.LongLength, new string('0', 64), CancellationToken.None));

        Assert.Contains("Контрольная сумма", error.Message);
    }

    [Fact]
    public async Task VerifyAsync_rejects_missing_or_malformed_server_hash()
    {
        byte[] content = "diagnostic archive"u8.ToArray();
        string path = await WriteAsync(content);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DiagnosticArchiveFileVerifier.VerifyAsync(path, content.LongLength, "not-a-hash", CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private async Task<string> WriteAsync(byte[] content)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "diagnostics.zip");
        await File.WriteAllBytesAsync(path, content);
        return path;
    }
}
