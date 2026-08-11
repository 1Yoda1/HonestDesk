using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using HonestDesk.Services;
using HonestDesk.Infrastructure;
using HonestDesk.Infrastructure.Licensing;
using HonestDesk.Infrastructure.Api;
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
    private readonly AppSettings _appSettings = new SettingsService().Load();
    private readonly LicenseGrantPublicationBuilder _grantBuilder = new();
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
        SetBusy(true, "Загрузка лицензий с HonestLicenseServer…");
        try
        {
            HonestLicenseAdminApiClient api = new(_appSettings.ApiBaseUrl,
                () => DpapiProtector.Unprotect(_appSettings.ProtectedAdminApiKey));
            IReadOnlyList<AdminLicense> licenses = await api.GetLicensesAsync(null, CancellationToken.None);
            IReadOnlyList<AdminClient> activeClients = await api.GetClientsAsync(CancellationToken.None);
            IReadOnlyList<AdminDevice> serverDevices = await api.GetDevicesAsync(null, CancellationToken.None);
            var activeClientIds = new HashSet<string>(activeClients
                .Where(x => !string.IsNullOrWhiteSpace(x.ClientId))
                .Select(x => x.ClientId!), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, AdminDevice> devicesByPair = serverDevices
                .Where(x => !string.IsNullOrWhiteSpace(x.ClientId) && !string.IsNullOrWhiteSpace(x.DeviceId))
                .GroupBy(x => LicenseKey(x.ClientId, x.DeviceId), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            var visibleDevicePairs = new HashSet<string>(serverDevices
                .Where(x => !string.IsNullOrWhiteSpace(x.ClientId) && !string.IsNullOrWhiteSpace(x.DeviceId))
                .Select(x => LicenseKey(x.ClientId, x.DeviceId)), StringComparer.OrdinalIgnoreCase);
            AdminLicense[] current = licenses
                .Where(x => string.Equals(x.Status, "Active", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(x.ClientId) && activeClientIds.Contains(x.ClientId!) &&
                            visibleDevicePairs.Contains(LicenseKey(x.ClientId, x.DeviceId)))
                .GroupBy(x => (x.ClientId ?? "") + "\n" + (x.DeviceId ?? ""), StringComparer.OrdinalIgnoreCase)
                .Select(x => x.OrderByDescending(y => y.Revision).ThenByDescending(y => y.Id).First())
                .ToArray();
            var grants = new List<LicenseGrant>();
            foreach (AdminLicense item in current)
            {
                AdminLicenseDetail detail = await api.GetLicenseAsync(item.Id, CancellationToken.None);
                if (string.IsNullOrWhiteSpace(detail.GrantJson)) continue;
                LicenseGrant? grant = JsonConvert.DeserializeObject<LicenseGrant>(detail.GrantJson);
                if (grant is not null) grants.Add(grant);
            }
            if (grants.Count == 0)
            {
                ClearPendingRegistrationImports();
                SetManifest(new LicenseManifest { Revision = licenses.Count == 0 ? 0 : licenses.Max(x => x.Revision) }, published: true);
                StatusText.Text = "На HonestLicenseServer нет действующих лицензий.";
                _access.Audit("Server licenses loaded; active grants=0");
                return;
            }
            var manifest = new LicenseManifest
            {
                SchemaVersion = grants.Max(x => x.SchemaVersion),
                Revision = grants.Max(x => x.Revision),
                IssuedAtUtc = grants.Max(x => x.IssuedAtUtc),
                ValidUntilUtc = grants.Max(x => x.ValidUntilUtc)
            };
            foreach (IGrouping<string, LicenseGrant> group in grants.Where(x => !x.OperatorDevice).GroupBy(x => x.ClientId, StringComparer.OrdinalIgnoreCase))
            {
                LicenseGrant first = group.OrderByDescending(x => x.Revision).First();
                manifest.Clients.Add(new ClientLicense
                {
                    ClientId = group.Key,
                    Enabled = first.ClientEnabled,
                    MinHonestFlowVersion = first.MinHonestFlowVersion,
                    OfflineGraceHours = first.OfflineGraceHours,
                    Features = first.Features?.Distinct().ToList() ?? [],
                    Devices = group.Select(x =>
                    {
                        devicesByPair.TryGetValue(LicenseKey(x.ClientId, x.DeviceId), out AdminDevice? serverDevice);
                        return new LicensedDevice
                        {
                            DeviceId = x.DeviceId,
                            Name = serverDevice?.Name ?? x.DeviceId,
                            Address = serverDevice?.Address ?? x.PointAddress ?? "",
                            Enabled = x.DeviceEnabled,
                            Comment = serverDevice?.Comment ?? ""
                        };
                    }).ToList()
                });
            }
            foreach (LicenseGrant grant in grants.Where(x => x.OperatorDevice &&
                         visibleDevicePairs.Contains(LicenseKey(x.ClientId, x.DeviceId)))
                         .GroupBy(x => x.DeviceId, StringComparer.OrdinalIgnoreCase).Select(x => x.First()))
                manifest.OperatorDevices.Add(new OperatorDevice { DeviceId = grant.DeviceId, Name = grant.DeviceId, Enabled = grant.DeviceEnabled });
            ClearPendingRegistrationImports();
            SetManifest(manifest, published: true);
            StatusText.Text = $"С HonestLicenseServer загружено grants: {grants.Count}; revision {manifest.Revision}.";
            _access.Audit($"Server licenses loaded; grants={grants.Count}; revision={manifest.Revision}");
        }
        catch (Exception ex)
        {
            _access.Audit("Published license load failed");
            ShowWarning(ex.Message);
        }
        finally { SetBusy(false); }
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
            if (_manifest.Clients.Any(x => x.Devices is null || x.Devices.Count == 0))
                throw new InvalidDataException("У каждого публикуемого клиента должно быть хотя бы одно зарегистрированное устройство.");
            stage = "Сохранение и подпись licenses.json";
            StatusText.Text = stage + "…";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            SaveSignedPreservingFeatureNames(_path, DateTimeOffset.UtcNow);
            _signedSnapshot = Snapshot();
            RefreshManifest();
            stage = "Публикация лицензий в HonestLicenseServer";
            StatusText.Text = stage + "…";
            _access.Audit($"License publication started; revision={_manifest.Revision}");
            IReadOnlyList<LicenseGrantPublication> grants = _grantBuilder.Build(_manifest, _settings.KeyId, _settings.PrivateKeyPath);
            HonestLicenseAdminApiClient api = new(_appSettings.ApiBaseUrl,
                () => DpapiProtector.Unprotect(_appSettings.ProtectedAdminApiKey));
            using var publishCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            IReadOnlyList<AdminLicense> serverLicenses = await api.GetLicensesAsync(null, publishCts.Token);
            IReadOnlyList<AdminDevice> registeredDevices = await api.GetDevicesAsync(null, publishCts.Token);
            var registeredPairs = new HashSet<string>(registeredDevices.Select(x => LicenseKey(x.ClientId, x.DeviceId)), StringComparer.OrdinalIgnoreCase);
            var alreadyPublished = new HashSet<string>(serverLicenses
                .Where(x => x.Revision == _manifest.Revision && !string.Equals(x.Status, "Revoked", StringComparison.OrdinalIgnoreCase))
                .Select(x => LicenseKey(x.ClientId, x.DeviceId)), StringComparer.OrdinalIgnoreCase);
            int completed = 0, skipped = 0, skippedOperatorPairs = 0;
            foreach (LicenseGrantPublication grant in grants)
            {
                LicenseGrant payload = JsonConvert.DeserializeObject<LicenseGrant>(Encoding.UTF8.GetString(grant.GrantBytes))
                    ?? throw new InvalidDataException("Не удалось прочитать grant перед публикацией.");
                string targetKey = LicenseKey(payload.ClientId, payload.DeviceId);
                if (!registeredPairs.Contains(targetKey))
                {
                    if (payload.OperatorDevice)
                    {
                        skippedOperatorPairs++;
                        continue;
                    }
                    throw new InvalidDataException($"Устройство {payload.DeviceId} не зарегистрировано за клиентом {payload.ClientId} на HonestLicenseServer.");
                }
                if (alreadyPublished.Contains(targetKey))
                {
                    skipped++;
                    continue;
                }
                LicenseSignatureEnvelope envelope = JsonConvert.DeserializeObject<LicenseSignatureEnvelope>(Encoding.UTF8.GetString(grant.SignatureBytes))
                    ?? throw new InvalidDataException("Не удалось прочитать подпись grant.");
                StatusText.Text = $"HonestLicenseServer: {completed + skipped + 1}/{grants.Count}…";
                await PublishGrantWithRetryAsync(api, grant, envelope, publishCts.Token);
                completed++;
                await Task.Delay(100, publishCts.Token);
            }
            _pendingRegistrationMessageUids.Clear();
            _hasUnsavedRegistrationImports = false;
            _publishedBase = Clone(_manifest);
            _publishedAwaitingMessageProcessing = false;
            StatusText.Text = $"Готово: revision {_manifest.Revision}; отправлено: {completed}; уже было: {skipped}; неприменимых операторских связей пропущено: {skippedOperatorPairs}.";
            _access.Audit($"License publication completed; revision={_manifest.Revision}; sent={completed}; skipped={skipped}; operatorPairsSkipped={skippedOperatorPairs}");
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

    private static string LicenseKey(string? clientId, string? deviceId) => (clientId ?? "") + "\n" + (deviceId ?? "");

    private static async Task PublishGrantWithRetryAsync(
        HonestLicenseAdminApiClient api,
        LicenseGrantPublication grant,
        LicenseSignatureEnvelope envelope,
        CancellationToken token)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await api.PublishLicenseAsync(Convert.ToBase64String(grant.GrantBytes), envelope.Signature, envelope.KeyId, token);
                return;
            }
            catch (HonestLicenseAdminApiException ex) when (
                attempt < 5 && (ex.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                                ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                                (int)ex.StatusCode >= 500) &&
                !ex.ResponseBody.Contains("signing-key-not-configured", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), token);
            }
        }
    }

    private async Task RetryPendingMessageProcessingAsync()
    {
        await Task.CompletedTask;
        _publishedAwaitingMessageProcessing = false;
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
        if (_manifest.Clients == null || _manifest.Clients.Count == 0)
        {
            ShowWarning("Сначала откройте или создайте licenses.json с клиентами.");
            return;
        }

        SetBusy(true, "Получение устройств с HonestLicenseServer…");
        try
        {
            HonestLicenseAdminApiClient api = new(_appSettings.ApiBaseUrl,
                () => DpapiProtector.Unprotect(_appSettings.ProtectedAdminApiKey));
            IReadOnlyList<AdminDevice> devices = await api.GetDevicesAsync(null, CancellationToken.None);
            int added = 0, updated = 0;
            foreach (AdminDevice source in devices.Where(x => string.Equals(x.Status, "Active", StringComparison.OrdinalIgnoreCase)))
            {
                ClientLicense? client = _manifest.Clients.FirstOrDefault(x => string.Equals(x.ClientId, source.ClientId, StringComparison.OrdinalIgnoreCase));
                if (client is null || string.IsNullOrWhiteSpace(source.DeviceId)) continue;
                client.Devices ??= [];
                LicensedDevice? target = client.Devices.FirstOrDefault(x => string.Equals(x.DeviceId, source.DeviceId, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                {
                    client.Devices.Add(new LicensedDevice { DeviceId = source.DeviceId, Name = source.Name ?? "", Address = source.Address ?? "", Comment = source.Comment ?? "", Enabled = true });
                    added++;
                }
                else
                {
                    target.Name = source.Name ?? target.Name;
                    target.Address = source.Address ?? target.Address;
                    target.Comment = source.Comment ?? target.Comment;
                    updated++;
                }
            }
            RefreshLicenseCollection();
            _hasUnsavedRegistrationImports = added > 0 || updated > 0;
            StatusText.Text = $"С сервера добавлено устройств: {added}; обновлено: {updated}.";
            _access.Audit($"Server devices imported; added={added}; updated={updated}");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка получения заявок.";
            _access.Audit("License registration request import failed");
            ShowWarning("Не удалось получить заявки:\n" + ex.Message);
        }
        finally
        {
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
    private void AddDeviceButton_Click(object sender, RoutedEventArgs e) =>
        ShowWarning("Устройство добавляется через заявку регистрации и Approve. Локальный Device без записи на HonestLicenseServer не создан.");
    private async void DeleteDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (DevicesGrid.SelectedItem is not LicensedDevice value || SelectedLicense is not { } license) return;
        if (System.Windows.MessageBox.Show(this,
                $"Удалить устройство «{value.Name}»? Оно будет помечено как Deleted на HonestLicenseServer.",
                "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        if (string.IsNullOrWhiteSpace(value.DeviceId) || string.IsNullOrWhiteSpace(license.ClientId))
        {
            _devices.Remove(value);
            license.Devices = _devices.ToList();
            StatusText.Text = "Локальный черновик устройства удалён.";
            return;
        }

        SetBusy(true, "Удаление устройства на HonestLicenseServer…");
        try
        {
            HonestLicenseAdminApiClient api = new(_appSettings.ApiBaseUrl,
                () => DpapiProtector.Unprotect(_appSettings.ProtectedAdminApiKey));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            IReadOnlyList<AdminDevice> serverDevices = await api.GetDevicesAsync(
                license.ClientId, cts.Token, includeDeleted: true);
            AdminDevice? serverDevice = AdminLifecycleVisibility.FindCurrentBoundDevice(
                serverDevices, license.ClientId, value.DeviceId);

            if (serverDevice is not null &&
                !string.Equals(serverDevice.Status, "Deleted", StringComparison.OrdinalIgnoreCase))
            {
                await api.UpdateDeviceAsync(serverDevice.Id, new
                {
                    name = serverDevice.Name ?? value.Name ?? value.DeviceId,
                    address = serverDevice.Address,
                    comment = serverDevice.Comment,
                    status = "Deleted"
                }, cts.Token);
            }

            _devices.Remove(value);
            license.Devices = _devices.ToList();
            StatusText.Text = serverDevice is null
                ? "Устройства не было на сервере; оно удалено из локального черновика."
                : "Устройство помечено Deleted на HonestLicenseServer.";
            _access.Audit($"Device soft-deleted; client={license.ClientId}; device={value.DeviceId}");
        }
        catch (Exception ex)
        {
            _access.Audit($"Device soft-delete failed; client={license.ClientId}; device={value.DeviceId}");
            ShowWarning("Не удалось удалить устройство на HonestLicenseServer:\n" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

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
        ShowWarning("Отзыв опубликованных лицензий из этого окна пока не реализован. Локальные данные и HonestLicenseServer не изменены.");
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
