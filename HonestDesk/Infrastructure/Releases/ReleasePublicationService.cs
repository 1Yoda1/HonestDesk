using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using HonestDesk.Infrastructure.Api;

namespace HonestDesk.Infrastructure.Releases;

public sealed record ReleasePublicationRequest(
    string KeyPath,
    char[] KeyPassword,
    string FilePath,
    string Component,
    string Version,
    string Architecture);

public sealed record ReleasePublicationProgress(int Percent, string Status);
public sealed record ReleasePublicationResult(bool UploadSkipped, string KeyId);

public sealed class ReleasePublicationService
{
    private readonly HonestLicenseAdminApiClient _api;
    private readonly HttpClient _storageClient;

    public ReleasePublicationService(
        HonestLicenseAdminApiClient api,
        HttpMessageHandler? storageHandler = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _storageClient = storageHandler is null
            ? new HttpClient()
            : new HttpClient(storageHandler, disposeHandler: false);
        _storageClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<ReleasePublicationResult> PublishAsync(
        ReleasePublicationRequest request,
        IProgress<ReleasePublicationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Validate(request);
        progress?.Report(new(2, "Вычисляем SHA-256 и подписываем паспорт файла…"));
        byte[] encryptedKey = await File.ReadAllBytesAsync(request.KeyPath, cancellationToken);
        SignedReleaseEnvelope envelope;
        try
        {
            envelope = ReleaseSigner.Sign(encryptedKey, request.KeyPassword,
                request.FilePath, request.Component, request.Version, request.Architecture);
        }
        finally { CryptographicOperations.ZeroMemory(encryptedKey); }

        progress?.Report(new(8, "Проверяем каталог опубликованных версий…"));
        IReadOnlyList<AdminAsset> assets = await _api.GetAssetsAsync(request.Component, cancellationToken);
        AdminAsset? existing = assets.SingleOrDefault(x =>
            string.Equals(x.Version, request.Version, StringComparison.Ordinal) &&
            string.Equals(x.Architecture, request.Architecture, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && !string.IsNullOrWhiteSpace(existing.ObjectKey))
        {
            if (!string.Equals(existing.ReleaseKeyId, envelope.KeyId, StringComparison.Ordinal) ||
                !string.Equals(existing.ManifestBase64, envelope.ManifestBase64, StringComparison.Ordinal))
                throw new InvalidDataException(
                    "Для этой версии и архитектуры уже опубликован другой подписанный файл. " +
                    "Опубликованные выпуски неизменяемы.");

            progress?.Report(new(100,
                "Идентичный файл уже опубликован; повторная загрузка не требуется."));
            return new(true, envelope.KeyId);
        }

        AdminReleaseUpload upload = await _api.CreateReleaseUploadAsync(
            envelope.KeyId, envelope.ManifestBase64, envelope.SignatureBase64, cancellationToken);
        if (upload.PartSizeBytes is null or <= 0 || upload.PartCount is null or <= 0)
            throw new InvalidDataException("Сервер не создал multipart-загрузку.");

        long fileSize = new FileInfo(request.FilePath).Length;
        long partSize = upload.PartSizeBytes.Value;
        int partCount = upload.PartCount.Value;
        if ((fileSize + partSize - 1) / partSize != partCount)
            throw new InvalidDataException("Сервер вернул несогласованную разбивку файла.");

        var completed = new List<AdminCompletedReleasePart>(partCount);
        for (int partNumber = 1; partNumber <= partCount; partNumber++)
        {
            long offset = (partNumber - 1L) * partSize;
            long length = Math.Min(partSize, fileSize - offset);
            string etag = await UploadPartWithRetryAsync(
                upload.Id, request.FilePath, partNumber, partCount,
                offset, length, fileSize, progress, cancellationToken);
            completed.Add(new(partNumber, etag));
        }

        progress?.Report(new(94, "Cloud.ru собирает части; подтверждаем публикацию…"));
        await _api.CompleteReleaseUploadAsync(upload.Id, completed, cancellationToken);
        progress?.Report(new(100, "Файл опубликован. Global-версия пока не изменялась."));
        return new(false, envelope.KeyId);
    }

    private async Task<string> UploadPartWithRetryAsync(
        int uploadId, string filePath, int partNumber, int partCount,
        long offset, long length, long fileSize,
        IProgress<ReleasePublicationProgress>? progress,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                AdminReleaseUploadPart part = await _api.CreateReleaseUploadPartAsync(
                    uploadId, partNumber, cancellationToken);
                progress?.Report(new(ToOverallPercent(offset, fileSize),
                    $"Загружаем часть {partNumber} из {partCount}, попытка {attempt}…"));
                await using FileStream file = new(filePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                file.Position = offset;
                using var content = new ProgressStreamContent(file, length, transferred =>
                    progress?.Report(new(ToOverallPercent(offset + transferred, fileSize),
                        $"Загружаем часть {partNumber} из {partCount}…")));
                using HttpResponseMessage response = await _storageClient.PutAsync(
                    part.UploadUrl, content, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    string? etag = response.Headers.ETag?.Tag;
                    if (string.IsNullOrWhiteSpace(etag) &&
                        response.Headers.TryGetValues("ETag", out IEnumerable<string>? values))
                        etag = values.FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(etag))
                        throw new InvalidDataException($"Cloud.ru не вернул ETag части {partNumber}.");
                    return etag;
                }

                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                bool transient = response.StatusCode is HttpStatusCode.RequestTimeout or
                    HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or
                    HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout ||
                    (int)response.StatusCode >= 500;
                if (!transient || attempt == maxAttempts)
                    throw new HttpRequestException(
                        $"Cloud.ru отклонил часть {partNumber}: HTTP {(int)response.StatusCode}. {body}");
            }
            catch (Exception exception) when (attempt < maxAttempts &&
                exception is HttpRequestException or TaskCanceledException or IOException)
            {
                progress?.Report(new(ToOverallPercent(offset, fileSize),
                    $"Часть {partNumber}: соединение прервано, повторяем только эту часть…"));
            }

            await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
        }

        throw new IOException($"Не удалось загрузить часть {partNumber}.");
    }

    private static int ToOverallPercent(long transferred, long total) =>
        10 + (int)Math.Clamp(transferred * 82 / Math.Max(total, 1), 0, 82);

    private static void Validate(ReleasePublicationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.KeyPath) || !File.Exists(request.KeyPath))
            throw new FileNotFoundException("Файл ключа выпуска не найден.", request.KeyPath);
        if (request.KeyPassword is null || request.KeyPassword.Length == 0)
            throw new ArgumentException("Введите пароль ключа выпуска.");
        if (string.IsNullOrWhiteSpace(request.FilePath) || !File.Exists(request.FilePath))
            throw new FileNotFoundException("Файл дистрибутива не найден.", request.FilePath);
        string extension = Path.GetExtension(request.FilePath).ToLowerInvariant();
        if (extension is not (".exe" or ".msi" or ".zip"))
            throw new ArgumentException("Поддерживаются файлы .exe, .msi и .zip.");
    }

    private sealed class ProgressStreamContent : HttpContent
    {
        private readonly Stream _source;
        private readonly long _length;
        private readonly Action<long> _progress;

        public ProgressStreamContent(Stream source, long length, Action<long> progress)
        {
            _source = source;
            _length = length;
            _progress = progress;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            Headers.ContentLength = length;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            CopySourceToAsync(stream, CancellationToken.None);

        protected override Task SerializeToStreamAsync(
            Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            CopySourceToAsync(stream, cancellationToken);

        private async Task CopySourceToAsync(Stream destination, CancellationToken cancellationToken)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            long transferred = 0;
            var reportTimer = Stopwatch.StartNew();
            try
            {
                while (transferred < _length)
                {
                    int read = await _source.ReadAsync(buffer.AsMemory(
                        0, (int)Math.Min(buffer.Length, _length - transferred)), cancellationToken);
                    if (read == 0) break;
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    transferred += read;
                    if (reportTimer.ElapsedMilliseconds >= 250 || transferred == _length)
                    {
                        _progress(transferred);
                        reportTimer.Restart();
                    }
                }

                if (transferred != _length)
                    throw new EndOfStreamException(
                        $"Часть файла закончилась раньше ожидаемого: {_length} байт, прочитано {transferred}.");
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
    }
}
