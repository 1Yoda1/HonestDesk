using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HonestDesk.Models;
using HonestDesk.Models.Licensing;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using Newtonsoft.Json;

namespace HonestDesk.Infrastructure.Licensing
{
    public sealed class YandexImapDeviceRegistrationInbox : IDeviceRegistrationInbox
    {
        private const string ImapHost = "imap.yandex.ru";
        private const int ImapPort = 993;
        private const string SubjectMarker = "[HONESTFLOW-DEVICE-REGISTRATION]";
        private const string ProcessedKeyword = "HonestFlowProcessed";
        private const int MaxAttachmentBytes = 64 * 1024;
        private const int MaxMessagesPerBatch = 1000;

        public async Task<DeviceRegistrationInboxResult> ReadPendingAsync(
            SupportMailSettings settings,
            CancellationToken cancellationToken)
        {
            ValidateSettings(settings);
            using var client = await ConnectAsync(settings, cancellationToken);
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
            IList<UniqueId> matches = await client.Inbox.SearchAsync(
                SearchQuery.NotKeywords(new[] { ProcessedKeyword })
                    .And(SearchQuery.SubjectContains(SubjectMarker)),
                cancellationToken);

            var items = new List<DeviceRegistrationInboxItem>();
            int invalid = 0;
            foreach (UniqueId uid in matches.Take(MaxMessagesPerBatch))
            {
                MimeMessage message = await client.Inbox.GetMessageAsync(uid, cancellationToken);
                if (TryReadRequest(message, out DeviceRegistrationRequest request))
                {
                    items.Add(new DeviceRegistrationInboxItem
                    {
                        MessageUid = uid.Id,
                        Request = request
                    });
                }
                else
                {
                    invalid++;
                }
            }

            await client.DisconnectAsync(true, cancellationToken);
            return new DeviceRegistrationInboxResult
            {
                Items = items,
                InvalidMessageCount = invalid
            };
        }

        public async Task MarkProcessedAsync(
            SupportMailSettings settings,
            IReadOnlyCollection<uint> messageUids,
            CancellationToken cancellationToken)
        {
            if (messageUids == null || messageUids.Count == 0)
                return;

            ValidateSettings(settings);
            using var client = await ConnectAsync(settings, cancellationToken);
            await client.Inbox.OpenAsync(FolderAccess.ReadWrite, cancellationToken);
            var uids = messageUids.Distinct().Select(value => new UniqueId(value)).ToList();
            await client.Inbox.AddFlagsAsync(
                uids,
                MessageFlags.Seen,
                new HashSet<string>(StringComparer.Ordinal) { ProcessedKeyword },
                true,
                cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }

        private static async Task<ImapClient> ConnectAsync(
            SupportMailSettings settings,
            CancellationToken cancellationToken)
        {
            var client = new ImapClient { Timeout = 30000 };
            try
            {
                await client.ConnectAsync(
                    ImapHost,
                    ImapPort,
                    SecureSocketOptions.SslOnConnect,
                    cancellationToken);
                await client.AuthenticateAsync(
                    settings.SenderEmail,
                    settings.SenderPassword,
                    cancellationToken);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public static bool TryReadRequest(
            MimeMessage message,
            out DeviceRegistrationRequest request)
        {
            request = null;
            if (message == null ||
                message.Subject?.IndexOf(SubjectMarker, StringComparison.Ordinal) < 0)
            {
                return false;
            }

            MimePart attachment = message.Attachments
                .OfType<MimePart>()
                .FirstOrDefault(part => string.Equals(
                    part.FileName,
                    "honestflow-device-registration.json",
                    StringComparison.OrdinalIgnoreCase));
            if (attachment == null)
                return false;

            try
            {
                using var stream = new MemoryStream();
                attachment.Content.DecodeTo(stream);
                if (stream.Length == 0 || stream.Length > MaxAttachmentBytes)
                    return false;

                stream.Position = 0;
                using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
                request = JsonConvert.DeserializeObject<DeviceRegistrationRequest>(reader.ReadToEnd());
                return ValidateRequest(request);
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is JsonException ||
                ex is DecoderFallbackException)
            {
                request = null;
                return false;
            }
        }

        private static bool ValidateRequest(DeviceRegistrationRequest request)
        {
            return request != null &&
                   request.SchemaVersion == 1 &&
                   !string.IsNullOrWhiteSpace(request.ClientId) &&
                   request.ClientId.Length <= 128 &&
                   Guid.TryParse(request.DeviceId, out _) &&
                   (request.DeviceName == null || request.DeviceName.Length <= 256) &&
                   (request.Address == null || request.Address.Length <= 500) &&
                   request.RequestedAtUtc != default;
        }

        private static void ValidateSettings(SupportMailSettings settings)
        {
            if (settings == null ||
                string.IsNullOrWhiteSpace(settings.SenderEmail) ||
                string.IsNullOrWhiteSpace(settings.SenderPassword))
            {
                throw new InvalidOperationException(
                    "В настройках почты не заполнены адрес или пароль приложения.");
            }
        }
    }
}
