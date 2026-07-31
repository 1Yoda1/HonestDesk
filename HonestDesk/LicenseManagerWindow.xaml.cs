using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using HonestDesk.Services;
using HonestDesk.Infrastructure;
using HonestDesk.Infrastructure.Licensing;
using HonestDesk.Models;
using HonestDesk.Models.Licensing;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HonestDesk;

public partial class LicenseManagerWindow : Window
{
    private readonly AdminAccessService _access;
    private readonly IReadOnlyDictionary<string, string> _knownClients;
    private readonly SupportMailSettings _supportMailSettings;
    private readonly LicenseDocumentService _documents = new();
    private readonly YandexDiskClient _disk = new();
    private readonly IDeviceRegistrationInbox _registrationInbox = new YandexImapDeviceRegistrationInbox();
    private readonly DeviceRegistrationBatchService _registrationBatchService = new();
    private readonly ObservableCollection<ClientLicense> _licenses = [];
    private readonly HashSet<uint> _pendingRegistrationMessageUids = [];
    private ObservableCollection<LicensedDevice> _devices = [];
    private LicenseManifest _manifest = new();
    private LicenseManifest _publishedBase = new();
    private LicenseEditorSettings _settings = LicenseEditorSettings.Load();
    private string _path = AppPaths.LicenseManifestPath;
    private string _signedSnapshot = "";
    private bool _hasUnsavedRegistrationImports;
    private bool _busy;
    private bool _publishedAwaitingMessageProcessing;

    public FrameworkElement DetachContentForEmbedding()
    {
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        var content = (FrameworkElement)Content;
        Content = null;
        return content;
    }

    public LicenseManagerWindow(
        IReadOnlyDictionary<string, string> clients,
        AdminAccessService access,
        SupportMailSettings? supportMailSettings = null)
    {
        InitializeComponent();
        _access = access;
        _knownClients = clients;
        _supportMailSettings = supportMailSettings ?? new SupportMailSettings();
        LicensesGrid.ItemsSource = _licenses;
        PrivateKeyBox.Text = _settings.PrivateKeyPath ?? "";
        PublicKeyBox.Text = _settings.PublicKeyPath ?? "";
        KeyIdBox.Text = _settings.KeyId ?? "";
        ValidityDaysBox.Text = Math.Max(1, _settings.ValidityDays).ToString();
        foreach (var client in _knownClients.OrderBy(x => x.Value))
            LicenseClientIdBox.Items.Add(new ClientChoice(client.Key, client.Value));
        RefreshManifest();
    }

    private ClientLicense? SelectedLicense => LicensesGrid.SelectedItem as ClientLicense;

    private void SetManifest(LicenseManifest manifest, bool published)
    {
        _manifest = manifest;
        _manifest.Clients ??= [];
        _manifest.OperatorDevices ??= [];
        _licenses.Clear();
        foreach (var license in _manifest.Clients.OrderBy(x => ResolveName(x.ClientId)))
        {
            license.DisplayName = ResolveName(license.ClientId);
            _licenses.Add(license);
        }
        if (published) _publishedBase = Clone(_manifest);
        _signedSnapshot = published ? Snapshot() : "";
        LicensesGrid.SelectedItem = _licenses.FirstOrDefault();
        RefreshManifest();
    }

    private void RefreshManifest()
    {
        ClientCountText.Text = _licenses.Count.ToString();
        RevisionText.Text =
            $"Revision {_manifest.Revision}  ·  выдана {_manifest.IssuedAtUtc:dd.MM.yyyy}  ·  действует до {_manifest.ValidUntilUtc:dd.MM.yyyy}";
    }

    private void LoadLicense(ClientLicense? license)
    {
        ClientChoice? choice = license is null
            ? null
            : LicenseClientIdBox.Items.OfType<ClientChoice>()
                .FirstOrDefault(x => string.Equals(x.ClientId, license.ClientId, StringComparison.OrdinalIgnoreCase));
        if (choice is null && !string.IsNullOrWhiteSpace(license?.ClientId))
        {
            choice = new ClientChoice(license.ClientId, "ИП не найдено в конфигурации");
            LicenseClientIdBox.Items.Add(choice);
        }
        LicenseClientIdBox.SelectedItem = choice;
        LicenseEnabledCheck.IsChecked = license?.Enabled ?? true;
        MinVersionBox.Text = license?.MinHonestFlowVersion ?? "2.4.2.0";
        GraceHoursBox.Text = (license?.OfflineGraceHours ?? 72).ToString();
        var features = license?.Features ?? [];
        ViewAndRepairCheck.IsChecked = features.Contains(LicenseFeature.ViewAndRepair);
        InstallAndMaintenanceCheck.IsChecked = features.Contains(LicenseFeature.InstallAndMaintenance);
        _devices = new ObservableCollection<LicensedDevice>((license?.Devices ?? []).Select(CloneDevice));
        DevicesGrid.ItemsSource = _devices;
    }

    private bool ApplySelected()
    {
        if (SelectedLicense is not { } license) return true;
        DevicesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        string clientId = (LicenseClientIdBox.SelectedItem as ClientChoice)?.ClientId ?? "";
        if (string.IsNullOrWhiteSpace(clientId) ||
            !Version.TryParse(MinVersionBox.Text.Trim(), out _) ||
            !int.TryParse(GraceHoursBox.Text, out int grace) || grace < 0)
        {
            ShowWarning("Заполните ClientId, корректную минимальную версию и неотрицательный офлайн-период.");
            return false;
        }
        if (_licenses.Any(x => !ReferenceEquals(x, license) &&
                               string.Equals(x.ClientId, clientId, StringComparison.OrdinalIgnoreCase)))
        {
            ShowWarning($"Лицензия клиента «{clientId}» уже существует.");
            return false;
        }
        if (_devices.Any(x => string.IsNullOrWhiteSpace(x.DeviceId)) ||
            _devices.GroupBy(x => x.DeviceId.Trim(), StringComparer.Ordinal).Any(x => x.Count() > 1))
        {
            ShowWarning("DeviceId устройств обязательны и должны быть уникальными внутри клиента.");
            return false;
        }
        if (_devices.Any(x => (x.Address ?? "").Trim().Length > 500))
        {
            ShowWarning("Адрес точки не может быть длиннее 500 символов.");
            return false;
        }

        license.ClientId = clientId;
        license.DisplayName = ResolveName(clientId);
        license.Enabled = LicenseEnabledCheck.IsChecked == true;
        license.MinHonestFlowVersion = MinVersionBox.Text.Trim();
        license.OfflineGraceHours = grace;
        license.Features = SelectedFeatures();
        license.Devices = _devices.ToList();
        LicensesGrid.Items.Refresh();
        return true;
    }

    private List<LicenseFeature> SelectedFeatures()
    {
        var result = new List<LicenseFeature>();
        if (ViewAndRepairCheck.IsChecked == true) result.Add(LicenseFeature.ViewAndRepair);
        if (InstallAndMaintenanceCheck.IsChecked == true) result.Add(LicenseFeature.InstallAndMaintenance);
        return result;
    }

    private static List<LicenseFeature> AllConfigurableFeatures() =>
        LicenseFeatureCatalog.ConfigurableFeatures.ToList();

    private bool SaveSettings()
    {
        if (!int.TryParse(ValidityDaysBox.Text, out int days) || days <= 0)
        {
            ShowWarning("Срок действия должен быть положительным числом дней.");
            return false;
        }
        _settings.PrivateKeyPath = PrivateKeyBox.Text.Trim();
        _settings.PublicKeyPath = PublicKeyBox.Text.Trim();
        _settings.KeyId = KeyIdBox.Text.Trim();
        _settings.ValidityDays = days;
        _settings.Save();
        return true;
    }

    private async Task LoadPublishedAsync()
    {
        if (!ConfirmDiscardUnsavedRegistrationImports()) return;
        if (string.IsNullOrWhiteSpace(PublicKeyBox.Text) || !File.Exists(PublicKeyBox.Text))
        {
            ShowWarning("Сначала укажите публичный ключ для проверки опубликованной лицензии.");
            return;
        }
        SetBusy(true, "Загрузка опубликованной лицензии…");
        string temporary = Path.Combine(Path.GetTempPath(), "HonestDesk-license-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string pointerJson = await _disk.DownloadFileContentAsync("/licenses/licenses-current.json");
            var pointer = JsonConvert.DeserializeObject<LicensePublicationPointer>(pointerJson)
                          ?? throw new InvalidDataException("Некорректный указатель лицензии.");
            string json = await _disk.DownloadFileContentAsync(pointer.VersionPath + "/licenses.json");
            string signature = await _disk.DownloadFileContentAsync(pointer.VersionPath + "/licenses.json.sig");
            string path = Path.Combine(temporary, "licenses.json");
            File.WriteAllText(path, json, new UTF8Encoding(false));
            File.WriteAllText(path + ".sig", signature, new UTF8Encoding(false));
            LicenseManifest manifest = OpenManifestMigratingLegacyFeatures(path, path + ".sig", PublicKeyBox.Text);
            ClearPendingRegistrationImports();
            SetManifest(manifest, published: true);
            StatusText.Text = $"Опубликованная лицензия проверена. Revision {manifest.Revision}";
            _access.Audit($"Published license loaded; revision={manifest.Revision}");
        }
        catch (Exception ex)
        {
            _access.Audit("Published license load failed");
            ShowWarning(ex.Message);
        }
        finally
        {
            try { Directory.Delete(temporary, true); } catch { }
            SetBusy(false);
        }
    }

    private static int ChangedClients(LicenseManifest before, LicenseManifest after)
    {
        var oldMap = (before.Clients ?? []).ToDictionary(x => x.ClientId ?? "", JsonConvert.SerializeObject);
        var newMap = (after.Clients ?? []).ToDictionary(x => x.ClientId ?? "", JsonConvert.SerializeObject);
        return oldMap.Keys.Union(newMap.Keys).Count(key =>
            !oldMap.TryGetValue(key, out var left) || !newMap.TryGetValue(key, out var right) || left != right);
    }
    private void SaveDraft()
    {
        if (_busy || !ApplySelected() || !SaveSettings()) return;
        try
        {
            _manifest.Clients = _licenses.ToList();
            LicenseDocumentService.Validate(_manifest);
            string draftPath = _path + ".draft";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(draftPath))!);
            File.WriteAllText(draftPath,
                JsonConvert.SerializeObject(_manifest, Formatting.Indented), new UTF8Encoding(false));
            StatusText.Text = $"Черновик сохранён локально: {draftPath}";
            _access.Audit($"License draft saved; revision={_manifest.Revision}");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Черновик не сохранён: " + ex.Message;
            _access.Audit("License draft save failed");
        }
    }

    private async Task SaveAndPublishAsync()
    {
        if (_busy) return;
        if (_publishedAwaitingMessageProcessing) { await RetryPendingMessageProcessingAsync(); return; }
        if (!ApplySelected() || !SaveSettings()) return;
        if (!YandexDiskSettings.IsConfigured)
        {
            var credentials = new DiskCredentialsWindow { Owner = Owner };
            if (credentials.ShowDialog() != true) return;
        }

        LicenseManifest before = Clone(_manifest);
        byte[]? previousJson = File.Exists(_path) ? File.ReadAllBytes(_path) : null;
        byte[]? previousSignature = File.Exists(_path + ".sig") ? File.ReadAllBytes(_path + ".sig") : null;
        uint[] pendingMessages = _pendingRegistrationMessageUids.ToArray();
        string stage = "Завершение редактирования";
        SetBusy(true, stage + "…");
        try
        {
            DevicesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            DevicesGrid.CommitEdit(DataGridEditingUnit.Row, true);
            if (!ApplySelected()) return;
            stage = "Проверка данных";
            StatusText.Text = stage + "…";
            _manifest.Clients = _licenses.ToList();
            LicenseDocumentService.Validate(_manifest);
            stage = "Сохранение и подпись licenses.json";
            StatusText.Text = stage + "…";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            SaveSignedPreservingFeatureNames(_path, DateTimeOffset.UtcNow);
            _signedSnapshot = Snapshot();
            RefreshManifest();
            stage = "Публикация версии и обновление licenses-current.json";
            StatusText.Text = stage + "…";
            _access.Audit($"License publication started; revision={_manifest.Revision}");
            await _disk.PublishLicensesAsync(_path, _path + ".sig", _manifest.Revision, PublicKeyBox.Text);
            _publishedAwaitingMessageProcessing = true;
            stage = "Пометка импортированных писем";
            StatusText.Text = stage + "…";
            if (pendingMessages.Length > 0)
                await _registrationInbox.MarkProcessedAsync(_supportMailSettings, pendingMessages, CancellationToken.None);
            _pendingRegistrationMessageUids.Clear();
            _hasUnsavedRegistrationImports = false;
            _publishedBase = Clone(_manifest);
            _publishedAwaitingMessageProcessing = false;
            StatusText.Text = $"Готово: revision {_manifest.Revision} сохранена, подписана и опубликована. Обработано писем: {pendingMessages.Length}.";
            _access.Audit($"License publication completed; revision={_manifest.Revision}; messages={pendingMessages.Length}");
        }
        catch (Exception ex)
        {
            if (!_publishedAwaitingMessageProcessing)
            {
                _manifest = before;
                RestoreLocalFile(_path, previousJson);
                RestoreLocalFile(_path + ".sig", previousSignature);
                RefreshLicenseCollection();
            }
            string detail = ex is LicensePublicationException publication
                ? publication.Stage + ": " + publication.Message : ex.Message;
            StatusText.Text = _publishedAwaitingMessageProcessing
                ? $"Revision {_manifest.Revision} опубликована, но письма не обработаны: {detail}. Повторите операцию."
                : $"Не опубликовано. Этап «{stage}»: {detail}. Можно повторить операцию.";
            _access.Audit($"License save and publication failed; stage={stage}");
        }
        finally { SetBusy(false); }
    }

    private async Task RetryPendingMessageProcessingAsync()
    {
        SetBusy(true, "Повторная обработка импортированных писем…");
        try
        {
            uint[] pending = _pendingRegistrationMessageUids.ToArray();
            if (pending.Length > 0)
                await _registrationInbox.MarkProcessedAsync(_supportMailSettings, pending, CancellationToken.None);
            _pendingRegistrationMessageUids.Clear();
            _hasUnsavedRegistrationImports = false;
            _publishedAwaitingMessageProcessing = false;
            StatusText.Text = $"Готово: revision {_manifest.Revision} опубликована; обработано писем: {pending.Length}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Лицензия уже опубликована, но письма всё ещё не обработаны: " + ex.Message;
        }
        finally { SetBusy(false); }
    }
    private static void RestoreLocalFile(string path, byte[]? content)
    {
        if (content is null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        File.WriteAllBytes(path, content);
    }

    private static int ChangedDevices(LicenseManifest before, LicenseManifest after) =>
        ChangedClients(before, after) +
        Math.Abs((before.OperatorDevices?.Count ?? 0) - (after.OperatorDevices?.Count ?? 0));

    private async Task ImportRegistrationRequestsAsync()
    {
        if (_busy || !ApplySelected()) return;
        if (string.IsNullOrWhiteSpace(_supportMailSettings.SenderEmail) ||
            string.IsNullOrWhiteSpace(_supportMailSettings.SenderPassword))
        {
            ShowWarning(
                "Не настроена почта для импорта заявок устройств.\n\n" +
                "Откройте настройки HonestDesk и заполните отдельный блок «Почта заявок лицензий», " +
                "или заполните эти реквизиты в support_mail_encrypted.json.");
            return;
        }
        if (_manifest.Clients == null || _manifest.Clients.Count == 0)
        {
            ShowWarning("Сначала откройте или создайте licenses.json с клиентами.");
            return;
        }

        SetBusy(true, "Получение заявок регистрации устройств…");
        ImportRegistrationRequestsButton.IsEnabled = false;
        try
        {
            DeviceRegistrationInboxResult inbox =
                await _registrationInbox.ReadPendingAsync(_supportMailSettings, CancellationToken.None);
            DeviceRegistrationBatchPlan plan = _registrationBatchService.BuildPlan(_manifest, inbox.Items);

            if (plan.Items.Count == 0 && inbox.InvalidMessageCount == 0)
            {
                StatusText.Text = "Новых заявок регистрации нет.";
                System.Windows.MessageBox.Show(this,
                    "Новых заявок регистрации нет.",
                    "Импорт заявок", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string preview = _registrationBatchService.BuildPreview(plan, inbox.InvalidMessageCount);
            if (System.Windows.MessageBox.Show(this, preview, "Импорт заявок устройств",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                StatusText.Text = "Импорт заявок отменён.";
                return;
            }

            _registrationBatchService.Apply(_manifest, plan);
            RefreshLicenseCollection();

            foreach (uint messageUid in plan.ProcessedMessageUids)
                _pendingRegistrationMessageUids.Add(messageUid);
            _hasUnsavedRegistrationImports = _pendingRegistrationMessageUids.Count > 0;


            StatusText.Text = $"Добавлено устройств: {plan.NewDeviceCount}; дозаполнено адресов: {plan.AddressUpdateCount}. Сохраните и подпишите licenses.json.";
            _access.Audit($"License registration requests imported; newDevices={plan.NewDeviceCount}; addressUpdates={plan.AddressUpdateCount}");
            System.Windows.MessageBox.Show(this,
                $"Добавлено устройств: {plan.NewDeviceCount}.\nДозаполнено адресов: {plan.AddressUpdateCount}.\n\nПисьма будут помечены обработанными только после успешного сохранения и подписи.",
                "Импорт завершён", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка получения заявок.";
            _access.Audit("License registration request import failed");
            ShowWarning("Не удалось получить заявки:\n" + ex.Message);
        }
        finally
        {
            ImportRegistrationRequestsButton.IsEnabled = true;
            SetBusy(false);
        }
    }
    private void ClearPendingRegistrationImports()
    {
        _pendingRegistrationMessageUids.Clear();
        _hasUnsavedRegistrationImports = false;
    }

    private bool ConfirmDiscardUnsavedRegistrationImports() =>
        !_hasUnsavedRegistrationImports ||
        System.Windows.MessageBox.Show(this,
            "Есть импортированные заявки, которые ещё не сохранены и не подписаны. Если продолжить, импорт будет потерян, а письма останутся во входящих. Продолжить?",
            "Несохранённый импорт заявок", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private void RefreshLicenseCollection()
    {
        _licenses.Clear();
        foreach (var license in _manifest.Clients.OrderBy(x => ResolveName(x.ClientId)))
        {
            license.DisplayName = ResolveName(license.ClientId);
            _licenses.Add(license);
        }
        LicensesGrid.SelectedItem = _licenses.FirstOrDefault();
        RefreshManifest();
        LoadLicense(SelectedLicense);
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        PublishButton.IsEnabled = !busy;
        ImportRegistrationRequestsButton.IsEnabled = !busy;
        if (status is not null) StatusText.Text = status;
    }

    private static LicenseManifest Clone(LicenseManifest value) =>
        JsonConvert.DeserializeObject<LicenseManifest>(JsonConvert.SerializeObject(value)) ?? new();

    private string Snapshot()
    {
        _manifest.Clients = _licenses.ToList();
        return JsonConvert.SerializeObject(_manifest);
    }

    private LicenseManifest OpenManifestMigratingLegacyFeatures(string jsonPath, string signaturePath, string publicKeyPath)
    {
        byte[] json = File.ReadAllBytes(jsonPath);
        byte[] signature = File.ReadAllBytes(signaturePath);
        if (!_documents.Verify(json, signature, publicKeyPath, out string error))
            throw new CryptographicException("Подпись недействительна: " + error);

        JObject rawManifest = JObject.Parse(Encoding.UTF8.GetString(json));
        if ((rawManifest["Clients"] ?? rawManifest["clients"]) is JArray clients)
        {
            foreach (JObject client in clients.OfType<JObject>())
            {
                var names = ((client["Features"] ?? client["features"]) as JArray)?
                    .Values<string>()
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToHashSet(StringComparer.Ordinal) ?? [];

                bool viewAndRepair =
                    names.Contains(nameof(LicenseFeature.ViewAndRepair)) ||
                    HasLegacyFeatureSet(
                        names,
                        "ViewPointStatus",
                        "ManageServices",
                        "RecoverLmServices",
                        "InitializeLm",
                        "OpenLocalTools");
                bool installAndMaintenance =
                    names.Contains(nameof(LicenseFeature.InstallAndMaintenance)) ||
                    HasLegacyFeatureSet(
                        names,
                        "InstallComponents",
                        "ReinstallComponents",
                        "RestoreLmDatabase");

                var migrated = new JArray();
                if (viewAndRepair) migrated.Add(nameof(LicenseFeature.ViewAndRepair));
                if (installAndMaintenance) migrated.Add(nameof(LicenseFeature.InstallAndMaintenance));
                client["Features"] = migrated;
            }
        }

        return rawManifest.ToObject<LicenseManifest>() ?? throw new InvalidDataException("licenses.json пуст.");
    }

    private static bool HasLegacyFeatureSet(
        IReadOnlySet<string> features,
        params string[] required) =>
        required.All(features.Contains);

    private void SaveSignedPreservingFeatureNames(string jsonPath, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(_settings.PrivateKeyPath) || !File.Exists(_settings.PrivateKeyPath))
            throw new InvalidOperationException("Закрытый ключ не настроен или недоступен.");
        if (string.IsNullOrWhiteSpace(_settings.KeyId))
            throw new InvalidOperationException("KeyId не задан.");
        if (_settings.ValidityDays <= 0)
            throw new InvalidOperationException("ValidityDays должен быть больше нуля.");

        LicenseDocumentService.Validate(_manifest);
        _manifest.Revision = checked(_manifest.Revision + 1);
        _manifest.IssuedAtUtc = nowUtc.ToUniversalTime();
        _manifest.ValidUntilUtc = _manifest.IssuedAtUtc.AddDays(_settings.ValidityDays);

        byte[] json = new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(_manifest, Formatting.Indented));
        byte[] signature = Sign(json, _settings.KeyId, File.ReadAllText(_settings.PrivateKeyPath));
        SafeReplacePair(jsonPath, json, jsonPath + ".sig", signature);
    }

    private static byte[] Sign(byte[] json, string keyId, string pem)
    {
        using ECDsa key = ECDsa.Create();
        key.ImportFromPem(pem);
        if (key.KeySize != 256) throw new CryptographicException("Требуется ECDSA P-256 key.");
        byte[] signature = key.SignData(json, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(new LicenseSignatureEnvelope
        {
            KeyId = keyId,
            Algorithm = LicenseSignatureEnvelope.AlgorithmName,
            Signature = Convert.ToBase64String(signature)
        }, Formatting.Indented));
    }

    private static void SafeReplacePair(string jsonPath, byte[] json, string signaturePath, byte[] signature)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonPath))!);
        string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        string jsonTemp = jsonPath + ".tmp-" + Guid.NewGuid().ToString("N");
        string signatureTemp = signaturePath + ".tmp-" + Guid.NewGuid().ToString("N");
        string jsonBackup = jsonPath + ".bak-" + stamp;
        string signatureBackup = signaturePath + ".bak-" + stamp;
        bool hadJson = File.Exists(jsonPath);
        bool hadSignature = File.Exists(signaturePath);

        File.WriteAllBytes(jsonTemp, json);
        File.WriteAllBytes(signatureTemp, signature);
        if (hadJson) File.Copy(jsonPath, jsonBackup, true);
        if (hadSignature) File.Copy(signaturePath, signatureBackup, true);

        try
        {
            File.Move(jsonTemp, jsonPath, true);
            File.Move(signatureTemp, signaturePath, true);
        }
        catch
        {
            if (hadJson) File.Copy(jsonBackup, jsonPath, true);
            else if (File.Exists(jsonPath)) File.Delete(jsonPath);
            if (hadSignature) File.Copy(signatureBackup, signaturePath, true);
            else if (File.Exists(signaturePath)) File.Delete(signaturePath);
            throw;
        }
        finally
        {
            if (File.Exists(jsonTemp)) File.Delete(jsonTemp);
            if (File.Exists(signatureTemp)) File.Delete(signatureTemp);
        }
    }

    private static LicensedDevice CloneDevice(LicensedDevice value) => new()
    {
        DeviceId = value.DeviceId,
        Name = value.Name,
        Address = value.Address,
        Enabled = value.Enabled,
        Comment = value.Comment
    };

    private void ShowWarning(string message) => StatusText.Text = message;

    private static string? PickKey(string title)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = "PEM и ключи|*.pem;*.key;*.pub|Все файлы|*.*" };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void LicensesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadLicense(SelectedLicense);
    private void ApplyLicenseButton_Click(object sender, RoutedEventArgs e) => ApplySelected();
    private void AddDeviceButton_Click(object sender, RoutedEventArgs e) => _devices.Add(new LicensedDevice { Enabled = true });
    private void DeleteDeviceButton_Click(object sender, RoutedEventArgs e) { if (DevicesGrid.SelectedItem is LicensedDevice value) _devices.Remove(value); }

    private void AddLicenseButton_Click(object sender, RoutedEventArgs e)
    {
        var license = new ClientLicense
        {
            ClientId = _knownClients.Keys.FirstOrDefault(x => _licenses.All(y => !string.Equals(x, y.ClientId, StringComparison.OrdinalIgnoreCase))) ?? "",
            Enabled = true,
            MinHonestFlowVersion = "2.4.2.0",
            OfflineGraceHours = 72,
            Features = AllConfigurableFeatures()
        };
        license.DisplayName = ResolveName(license.ClientId);
        _licenses.Add(license);
        LicensesGrid.SelectedItem = license;
        RefreshManifest();
    }

    private void DeleteLicenseButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLicense is not { } license) return;
        if (System.Windows.MessageBox.Show(this, $"Удалить лицензию «{license.DisplayName}»?",
                "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _licenses.Remove(license);
        LicensesGrid.SelectedItem = _licenses.FirstOrDefault();
        RefreshManifest();
    }

    private void AddMissingButton_Click(object sender, RoutedEventArgs e)
    {
        int added = 0;
        foreach (string clientId in _knownClients.Keys.Where(id => _licenses.All(x =>
                     !string.Equals(x.ClientId, id, StringComparison.OrdinalIgnoreCase))))
        {
            var license = new ClientLicense
            {
                ClientId = clientId, Enabled = true, MinHonestFlowVersion = "2.4.2.0",
                OfflineGraceHours = 72, Features = AllConfigurableFeatures()
            };
            license.DisplayName = ResolveName(clientId);
            _licenses.Add(license);
            added++;
        }
        StatusText.Text = $"Добавлено отсутствующих лицензий: {added}";
        RefreshManifest();
    }

    private void OperatorDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new OperatorDevicesWindow(_manifest.OperatorDevices ?? []) { Owner = this };
        if (window.ShowDialog() == true) _manifest.OperatorDevices = window.Devices.ToList();
    }

    private void NewButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardUnsavedRegistrationImports()) return;
        ClearPendingRegistrationImports();
        SetManifest(new LicenseManifest(), published: false);
        StatusText.Text = "Новый манифест";
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "licenses.json|licenses.json|JSON|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        if (!ConfirmDiscardUnsavedRegistrationImports()) return;
        try
        {
            LicenseManifest manifest = OpenManifestMigratingLegacyFeatures(dialog.FileName, dialog.FileName + ".sig", PublicKeyBox.Text);
            _path = dialog.FileName;
            ClearPendingRegistrationImports();
            SetManifest(manifest, published: true);
            _signedSnapshot = Snapshot();
            StatusText.Text = $"Подпись верна. Revision {manifest.Revision}";
        }
        catch (Exception ex) { ShowWarning(ex.Message); }
    }

    private async void LoadPublishedButton_Click(object sender, RoutedEventArgs e) => await LoadPublishedAsync();
    private void SaveDraftButton_Click(object sender, RoutedEventArgs e) => SaveDraft();
    private async void SaveAndPublishButton_Click(object sender, RoutedEventArgs e) => await SaveAndPublishAsync();
    private async void ImportRegistrationRequestsButton_Click(object sender, RoutedEventArgs e) => await ImportRegistrationRequestsAsync();
    private void PrivateKeyBrowse_Click(object sender, RoutedEventArgs e) { if (PickKey("Выберите закрытый ключ") is { } path) PrivateKeyBox.Text = path; }
    private void PublicKeyBrowse_Click(object sender, RoutedEventArgs e) { if (PickKey("Выберите публичный ключ") is { } path) PublicKeyBox.Text = path; }

    private string ResolveName(string? clientId) =>
        clientId is not null && _knownClients.TryGetValue(clientId, out string? name)
            ? name
            : "ИП не найдено в конфигурации";

    private sealed record ClientChoice(string ClientId, string Name)
    {
        public override string ToString() => Name;
    }
}
