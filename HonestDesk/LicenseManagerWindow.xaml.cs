using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using HonestDesk.Services;
using HonestFlowConfigEditor.Infrastructure;
using HonestFlowConfigEditor.Infrastructure.Licensing;
using HonestFlowConfigEditor.Models.Licensing;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace HonestDesk;

public partial class LicenseManagerWindow : Window
{
    private readonly AdminAccessService _access;
    private readonly IReadOnlyDictionary<string, string> _knownClients;
    private readonly LicenseDocumentService _documents = new();
    private readonly YandexDiskClient _disk = new();
    private readonly ObservableCollection<ClientLicense> _licenses = [];
    private ObservableCollection<LicensedDevice> _devices = [];
    private LicenseManifest _manifest = new();
    private LicenseManifest _publishedBase = new();
    private LicenseEditorSettings _settings = LicenseEditorSettings.Load();
    private string _path = AppPaths.LicenseManifestPath;
    private string _signedSnapshot = "";
    private bool _busy;

    public LicenseManagerWindow(IReadOnlyDictionary<string, string> clients, AdminAccessService access)
    {
        InitializeComponent();
        _access = access;
        _knownClients = clients;
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
        ViewPointStatusCheck.IsChecked = HasFeature(features, LicenseFeature.ViewPointStatus, LicenseFeature.Diagnostics);
        CollectDiagnosticsCheck.IsChecked = HasFeature(features, LicenseFeature.CollectDiagnostics, LicenseFeature.Diagnostics);
        SendDiagnosticsCheck.IsChecked = HasFeature(features, LicenseFeature.SendDiagnostics, LicenseFeature.SendLogs);
        RequestHelpCheck.IsChecked = HasFeature(features, LicenseFeature.RequestHelp, LicenseFeature.SendLogs);
        InstallComponentsCheck.IsChecked = HasFeature(features, LicenseFeature.InstallComponents, LicenseFeature.Install);
        ReinstallComponentsCheck.IsChecked = HasFeature(features, LicenseFeature.ReinstallComponents, LicenseFeature.Repair);
        RestoreLmDatabaseCheck.IsChecked = HasFeature(features, LicenseFeature.RestoreLmDatabase, LicenseFeature.Repair);
        ManageServicesCheck.IsChecked = HasFeature(features, LicenseFeature.ManageServices, LicenseFeature.AutoFix);
        RecoverLmServicesCheck.IsChecked = HasFeature(features, LicenseFeature.RecoverLmServices, LicenseFeature.AutoFix);
        InitializeLmCheck.IsChecked = HasFeature(features, LicenseFeature.InitializeLm, LicenseFeature.AutoFix);
        InstallRuDesktopCheck.IsChecked = HasFeature(features, LicenseFeature.InstallRuDesktop, LicenseFeature.Install);
        ConfigureRuDesktopCheck.IsChecked = HasFeature(features, LicenseFeature.ConfigureRuDesktop, LicenseFeature.ManualTools);
        OpenLocalToolsCheck.IsChecked = HasFeature(features, LicenseFeature.OpenLocalTools, LicenseFeature.ManualTools);
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
        if (ViewPointStatusCheck.IsChecked == true) result.Add(LicenseFeature.ViewPointStatus);
        if (CollectDiagnosticsCheck.IsChecked == true) result.Add(LicenseFeature.CollectDiagnostics);
        if (SendDiagnosticsCheck.IsChecked == true) result.Add(LicenseFeature.SendDiagnostics);
        if (RequestHelpCheck.IsChecked == true) result.Add(LicenseFeature.RequestHelp);
        if (InstallComponentsCheck.IsChecked == true) result.Add(LicenseFeature.InstallComponents);
        if (ReinstallComponentsCheck.IsChecked == true) result.Add(LicenseFeature.ReinstallComponents);
        if (RestoreLmDatabaseCheck.IsChecked == true) result.Add(LicenseFeature.RestoreLmDatabase);
        if (ManageServicesCheck.IsChecked == true) result.Add(LicenseFeature.ManageServices);
        if (RecoverLmServicesCheck.IsChecked == true) result.Add(LicenseFeature.RecoverLmServices);
        if (InitializeLmCheck.IsChecked == true) result.Add(LicenseFeature.InitializeLm);
        if (InstallRuDesktopCheck.IsChecked == true) result.Add(LicenseFeature.InstallRuDesktop);
        if (ConfigureRuDesktopCheck.IsChecked == true) result.Add(LicenseFeature.ConfigureRuDesktop);
        if (OpenLocalToolsCheck.IsChecked == true) result.Add(LicenseFeature.OpenLocalTools);
        return result;
    }

    private static bool HasFeature(IEnumerable<LicenseFeature> features, LicenseFeature feature, LicenseFeature legacyFeature) =>
        features.Contains(feature) || features.Contains(legacyFeature);

    private static List<LicenseFeature> AllConfigurableFeatures() =>
    [
        LicenseFeature.ViewPointStatus,
        LicenseFeature.CollectDiagnostics,
        LicenseFeature.SendDiagnostics,
        LicenseFeature.RequestHelp,
        LicenseFeature.InstallComponents,
        LicenseFeature.ReinstallComponents,
        LicenseFeature.RestoreLmDatabase,
        LicenseFeature.ManageServices,
        LicenseFeature.RecoverLmServices,
        LicenseFeature.InitializeLm,
        LicenseFeature.InstallRuDesktop,
        LicenseFeature.ConfigureRuDesktop,
        LicenseFeature.OpenLocalTools
    ];

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
            LicenseManifest manifest = _documents.Open(path, path + ".sig", PublicKeyBox.Text);
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

    private void SaveSigned()
    {
        if (!ApplySelected() || !SaveSettings()) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _manifest.Clients = _licenses.ToList();
        _documents.SaveSigned(_manifest, _path, _settings, DateTimeOffset.UtcNow);
        _signedSnapshot = Snapshot();
        StatusText.Text = $"Локально сохранено и подписано. Revision {_manifest.Revision}";
        RefreshManifest();
        _access.Audit($"License signed locally; revision={_manifest.Revision}");
    }

    private async Task PublishAsync()
    {
        if (_busy || !ApplySelected() || !SaveSettings()) return;
        if (!File.Exists(_path) || !File.Exists(_path + ".sig"))
        {
            ShowWarning("Сначала нажмите «Сохранить и подписать».");
            return;
        }
        if (Snapshot() != _signedSnapshot)
        {
            ShowWarning("После подписи появились изменения. Снова нажмите «Сохранить и подписать».");
            return;
        }
        if (!_documents.Verify(File.ReadAllBytes(_path), File.ReadAllBytes(_path + ".sig"),
                PublicKeyBox.Text, out string error))
        {
            ShowWarning("Подпись не прошла повторную проверку: " + error);
            return;
        }
        if (!YandexDiskSettings.IsConfigured &&
            new DiskCredentialsWindow { Owner = this }.ShowDialog() != true) return;

        int clientsChanged = ChangedClients(_publishedBase, _manifest);
        int devicesChanged = ChangedDevices(_publishedBase, _manifest);
        if (System.Windows.MessageBox.Show(this,
                $"Опубликовать подписанную лицензию?\n\nRevision: {_manifest.Revision}\n" +
                $"Изменено клиентов: {clientsChanged}\nИзменено устройств: {devicesChanged}\n" +
                $"Действует до: {_manifest.ValidUntilUtc:dd.MM.yyyy}",
                "Публикация лицензии", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        SetBusy(true, "Публикация и удалённая проверка лицензии…");
        try
        {
            _access.Audit($"License publication started; revision={_manifest.Revision}");
            await _disk.PublishLicensesAsync(_path, _path + ".sig", _manifest.Revision, PublicKeyBox.Text);
            _publishedBase = Clone(_manifest);
            StatusText.Text = $"Revision {_manifest.Revision} опубликована и проверена";
            _access.Audit($"License publication completed; revision={_manifest.Revision}");
            System.Windows.MessageBox.Show(this,
                $"Лицензия опубликована и проверена.\nЖурнал: {LicensePublicationJournal.PathName}",
                "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (LicensePublicationException ex)
        {
            _access.Audit($"License publication failed; stage={ex.Stage}");
            ShowWarning($"Этап: {ex.Stage}\n{ex.Message}");
        }
        catch (Exception ex)
        {
            _access.Audit("License publication failed; stage=Unexpected");
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

    private static int ChangedDevices(LicenseManifest before, LicenseManifest after) =>
        ChangedClients(before, after) +
        Math.Abs((before.OperatorDevices?.Count ?? 0) - (after.OperatorDevices?.Count ?? 0));

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

    private static LicensedDevice CloneDevice(LicensedDevice value) => new()
    {
        DeviceId = value.DeviceId,
        Name = value.Name,
        Address = value.Address,
        Enabled = value.Enabled,
        Comment = value.Comment
    };

    private void ShowWarning(string message) =>
        System.Windows.MessageBox.Show(this, message, "Лицензии HonestFlow", MessageBoxButton.OK, MessageBoxImage.Warning);

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
        SetManifest(new LicenseManifest(), published: false);
        StatusText.Text = "Новый манифест";
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "licenses.json|licenses.json|JSON|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            LicenseManifest manifest = _documents.Open(dialog.FileName, dialog.FileName + ".sig", PublicKeyBox.Text);
            _path = dialog.FileName;
            SetManifest(manifest, published: true);
            _signedSnapshot = Snapshot();
            StatusText.Text = $"Подпись верна. Revision {manifest.Revision}";
        }
        catch (Exception ex) { ShowWarning(ex.Message); }
    }

    private async void LoadPublishedButton_Click(object sender, RoutedEventArgs e) => await LoadPublishedAsync();
    private void SaveSignedButton_Click(object sender, RoutedEventArgs e) { try { SaveSigned(); } catch (Exception ex) { ShowWarning(ex.Message); } }
    private async void PublishButton_Click(object sender, RoutedEventArgs e) => await PublishAsync();
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
