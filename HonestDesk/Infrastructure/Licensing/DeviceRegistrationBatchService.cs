using System;
using System.Collections.Generic;
using System.Linq;
using HonestDesk.Models.Licensing;

namespace HonestDesk.Infrastructure.Licensing
{
    public sealed class DeviceRegistrationBatchService
    {
        public DeviceRegistrationBatchPlan BuildPlan(
            LicenseManifest manifest,
            IReadOnlyCollection<DeviceRegistrationInboxItem> inboxItems)
        {
            if (manifest == null)
                throw new ArgumentNullException(nameof(manifest));

            var clients = (manifest.Clients ?? new List<ClientLicense>())
                .Where(client => client != null && !string.IsNullOrWhiteSpace(client.ClientId))
                .ToDictionary(client => client.ClientId, StringComparer.Ordinal);

            var deviceOwners = clients.Values
                .SelectMany(client => (client.Devices ?? new List<LicensedDevice>())
                    .Where(device => device != null && !string.IsNullOrWhiteSpace(device.DeviceId))
                    .Select(device => new { device.DeviceId, client.ClientId }))
                .GroupBy(item => item.DeviceId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().ClientId, StringComparer.Ordinal);

            var requestKeys = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<DeviceRegistrationBatchItem>();

            foreach (DeviceRegistrationInboxItem inboxItem in inboxItems ?? Array.Empty<DeviceRegistrationInboxItem>())
            {
                DeviceRegistrationRequest request = inboxItem?.Request;
                if (request == null)
                    continue;

                string requestKey = request.ClientId + "\n" + request.DeviceId;
                DeviceRegistrationBatchStatus status;
                string existingOwnerClientId = null;

                if (!requestKeys.Add(requestKey))
                {
                    status = DeviceRegistrationBatchStatus.DuplicateRequest;
                }
                else if (!clients.TryGetValue(request.ClientId, out ClientLicense client))
                {
                    status = DeviceRegistrationBatchStatus.UnknownClient;
                }
                else if (deviceOwners.TryGetValue(request.DeviceId, out string ownerClientId))
                {
                    if (string.Equals(ownerClientId, request.ClientId, StringComparison.Ordinal))
                    {
                        LicensedDevice existingDevice = client.Devices?.FirstOrDefault(device =>
                            string.Equals(device?.DeviceId, request.DeviceId, StringComparison.Ordinal));
                        status = existingDevice != null &&
                                 string.IsNullOrWhiteSpace(existingDevice.Address) &&
                                 !string.IsNullOrWhiteSpace(request.Address)
                            ? DeviceRegistrationBatchStatus.AddressUpdate
                            : DeviceRegistrationBatchStatus.AlreadyRegistered;
                    }
                    else
                    {
                        status = DeviceRegistrationBatchStatus.DeviceAssignedToAnotherClient;
                    }

                    if (status == DeviceRegistrationBatchStatus.DeviceAssignedToAnotherClient)
                        existingOwnerClientId = ownerClientId;
                }
                else
                {
                    status = DeviceRegistrationBatchStatus.NewDevice;
                    deviceOwners[request.DeviceId] = client.ClientId;
                }

                result.Add(new DeviceRegistrationBatchItem
                {
                    InboxItem = inboxItem,
                    Status = status,
                    ExistingOwnerClientId = existingOwnerClientId
                });
            }

            return new DeviceRegistrationBatchPlan { Items = result };
        }

        public void Apply(LicenseManifest manifest, DeviceRegistrationBatchPlan plan)
        {
            if (manifest == null)
                throw new ArgumentNullException(nameof(manifest));
            if (plan == null)
                throw new ArgumentNullException(nameof(plan));

            var clients = (manifest.Clients ?? new List<ClientLicense>())
                .Where(client => client != null && !string.IsNullOrWhiteSpace(client.ClientId))
                .ToDictionary(client => client.ClientId, StringComparer.Ordinal);

            foreach (DeviceRegistrationBatchItem item in plan.Items.Where(item =>
                         item.Status == DeviceRegistrationBatchStatus.NewDevice ||
                         item.Status == DeviceRegistrationBatchStatus.AddressUpdate))
            {
                DeviceRegistrationRequest request = item.InboxItem.Request;
                ClientLicense client = clients[request.ClientId];
                client.Devices ??= new List<LicensedDevice>();

                if (item.Status == DeviceRegistrationBatchStatus.AddressUpdate)
                {
                    LicensedDevice existingDevice = client.Devices.FirstOrDefault(device =>
                        string.Equals(device?.DeviceId, request.DeviceId, StringComparison.Ordinal));
                    if (existingDevice != null &&
                        string.IsNullOrWhiteSpace(existingDevice.Address) &&
                        !string.IsNullOrWhiteSpace(request.Address))
                    {
                        existingDevice.Address = request.Address.Trim();
                    }
                    continue;
                }

                if (client.Devices.Any(device => string.Equals(
                        device?.DeviceId,
                        request.DeviceId,
                        StringComparison.Ordinal)))
                {
                    continue;
                }

                client.Devices.Add(new LicensedDevice
                {
                    DeviceId = request.DeviceId,
                    Name = string.IsNullOrWhiteSpace(request.DeviceName)
                        ? request.DeviceId
                        : request.DeviceName.Trim(),
                    Address = string.IsNullOrWhiteSpace(request.Address)
                        ? null
                        : request.Address.Trim(),
                    Enabled = true,
                    Comment = "Импорт заявки " + request.RequestedAtUtc
                        .ToUniversalTime()
                        .ToString("yyyy-MM-dd HH:mm:ss 'UTC'")
                });
            }
        }

        public string BuildPreview(DeviceRegistrationBatchPlan plan, int invalidMessageCount)
        {
            if (plan == null)
                throw new ArgumentNullException(nameof(plan));

            string groups = string.Join(
                Environment.NewLine,
                plan.Items
                    .Where(item => item.Status == DeviceRegistrationBatchStatus.NewDevice)
                    .GroupBy(item => item.InboxItem.Request.ClientId, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group =>
                    {
                        string devices = string.Join(
                            Environment.NewLine,
                            group.Select(item =>
                            {
                                DeviceRegistrationRequest request = item.InboxItem.Request;
                                string name = string.IsNullOrWhiteSpace(request.DeviceName)
                                    ? request.DeviceId
                                    : request.DeviceName.Trim();
                                string address = string.IsNullOrWhiteSpace(request.Address)
                                    ? "адрес не указан"
                                    : request.Address.Trim();
                                return $"  • {name}: {address}";
                            }));

                        return $"{group.Key}: {group.Count()} новых устройств{Environment.NewLine}{devices}";
                    }));

            if (string.IsNullOrWhiteSpace(groups))
                groups = "Новых устройств нет";

            string addressUpdates = string.Join(
                Environment.NewLine,
                plan.Items
                    .Where(item => item.Status == DeviceRegistrationBatchStatus.AddressUpdate)
                    .Select(item =>
                        $"  • {item.InboxItem.Request.DeviceId}: {item.InboxItem.Request.Address?.Trim()}"));

            if (!string.IsNullOrWhiteSpace(addressUpdates))
                groups += $"{Environment.NewLine}{Environment.NewLine}Адреса для дозаполнения:{Environment.NewLine}{addressUpdates}";

            DeviceRegistrationBatchItem[] conflicts = plan.Items
                .Where(item => item.Status == DeviceRegistrationBatchStatus.DeviceAssignedToAnotherClient)
                .ToArray();

            string conflictDetails = "";
            if (conflicts.Length > 0)
            {
                string conflictRows = string.Join(
                    Environment.NewLine,
                    conflicts.Select(item =>
                    {
                        DeviceRegistrationRequest request = item.InboxItem.Request;
                        string deviceName = string.IsNullOrWhiteSpace(request.DeviceName)
                            ? "без названия"
                            : request.DeviceName.Trim();

                        return
                            $"  • DeviceId: {request.DeviceId}\n" +
                            $"    Устройство: {deviceName}\n" +
                            $"    Клиент в заявке: {request.ClientId}\n" +
                            $"    Уже закреплено за клиентом: {item.ExistingOwnerClientId}";
                    }));

                conflictDetails =
                    "\n\nЧто означает конфликт DeviceId:\n" +
                    "DeviceId — уникальный идентификатор устройства. Заявка просит привязать его к одному клиенту, " +
                    "но в открытом licenses.json этот DeviceId уже закреплён за другим клиентом. " +
                    "Такое устройство не будет добавлено и письмо не будет помечено обработанным.\n\n" +
                    "Найденные конфликты:\n" +
                    conflictRows +
                    "\n\nКак исправить:\n" +
                    "• если в заявке указан неверный клиент — исправьте ClientId на устройстве и отправьте заявку повторно;\n" +
                    "• если устройство действительно переносится — удалите его DeviceId у прежнего клиента, " +
                    "сохраните и опубликуйте licenses.json, затем повторите импорт;\n" +
                    "• один DeviceId нельзя одновременно использовать у нескольких клиентов.";
            }

            return
                $"Получено корректных заявок: {plan.Items.Count}\n" +
                $"Новых устройств: {plan.NewDeviceCount}\n" +
                $"Адресов для дозаполнения: {plan.AddressUpdateCount}\n" +
                $"Повторных заявок: {plan.Items.Count(x => x.Status == DeviceRegistrationBatchStatus.DuplicateRequest)}\n" +
                $"Уже зарегистрировано: {plan.Items.Count(x => x.Status == DeviceRegistrationBatchStatus.AlreadyRegistered)}\n" +
                $"Неизвестных клиентов: {plan.Items.Count(x => x.Status == DeviceRegistrationBatchStatus.UnknownClient)}\n" +
                $"Конфликтов DeviceId: {plan.Items.Count(x => x.Status == DeviceRegistrationBatchStatus.DeviceAssignedToAnotherClient)}\n" +
                $"Некорректных писем: {invalidMessageCount}\n\n" +
                groups +
                conflictDetails +
                "\n\nПрименить новые устройства и дозаполнить пустые адреса в открытом manifest?\n" +
                "Подписание и публикация автоматически выполняться не будут.";
        }
    }
}
