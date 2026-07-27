using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using HonestDesk.Services;
using HonestFlowConfigEditor.Infrastructure;
using HonestFlowConfigEditor.Models;
using Newtonsoft.Json;

namespace HonestDesk;

public partial class AdminEditorWindow : Window
{
    private readonly AdminAccessService _access;
    private readonly YandexDiskClient _disk = new();
    private readonly ObservableCollection<IPData> _clients = [];
    private readonly ICollectionView _clientsView;
    private VersionsData _versions = new();
    private AvailableVersionsData _availableVersions = new();
    private string _originalIpsEncrypted = "";
    private string _originalVersionsJson = "";
    private string _baselineJson = "";
    private bool _loaded;
    private bool _busy;
    private readonly DateTimeOffset _sessionUntilUtc;
    private readonly DispatcherTimer _sessionTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    public AdminEditorWindow(AdminAccessService access, DateTimeOffset sessionUntilUtc)
    {
        InitializeComponent();
        _access = access;
        _sessionUntilUtc = sessionUntilUtc;
        _clientsView = CollectionViewSource.GetDefaultView(_clients);
        _clientsView.Filter = FilterClient;
        ClientsGrid.ItemsSource = _clientsView;
        Loaded += async (_, _) => await LoadRemoteAsync();
        _sessionTimer.Tick += SessionTimer_Tick;
        _sessionTimer.Start();
        Closed += (_, _) => { _sessionTimer.Stop(); ClearSecrets(); };
    }

    private void SessionTimer_Tick(object? sender, EventArgs e)
    {
        if (DateTimeOffset.UtcNow < _sessionUntilUtc) return;
        _access.Audit("Admin session expired");
        _sessionTimer.Stop();
        System.Windows.MessageBox.Show(this, "Административная сессия завершена. Несохранённые изменения не опубликованы.",
            "Сессия заблокирована", MessageBoxButton.OK, MessageBoxImage.Information);
        Close();
    }

    private async Task LoadRemoteAsync()
    {
        if (_busy) return;
        SetBusy(true, "Загрузка конфигурации с Яндекс.Диска…");
        try
        {
            var files = await _disk.GetFilesAsync();
            _availableVersions = _disk.BuildAvailableVersions(files);
            FillVersionSelectors();
            if (!files.TryGetValue("ips_encrypted.json", out var ipsUrl))
                throw new InvalidDataException("ips_encrypted.json не найден.");

            _originalIpsEncrypted = await _disk.DownloadFileContentAsync(ipsUrl);
            string ipsJson = ConfigManager.Deobfuscate(_originalIpsEncrypted);
            var clients = JsonConvert.DeserializeObject<List<IPData>>(ipsJson) ?? [];
            _clients.Clear();
            foreach (var client in clients.OrderBy(x => x.Name))
                _clients.Add(client);

            if (files.TryGetValue("versions.json", out var versionsUrl))
            {
                _originalVersionsJson = await _disk.DownloadFileContentAsync(versionsUrl);
                _versions = JsonConvert.DeserializeObject<VersionsData>(_originalVersionsJson) ?? new();
            }
            else
            {
                _versions = new();
                _originalVersionsJson = JsonConvert.SerializeObject(_versions, Formatting.Indented);
            }

            LoadVersionFields();
            _baselineJson = Snapshot();
            _loaded = true;
            ClientsGrid.SelectedItem = _clients.FirstOrDefault();
            StatusText.Text = $"Загружено клиентов: {_clients.Count}. Конфигурация открыта как черновик.";
            DraftText.Text = "Изменений нет";
            _access.Audit($"Admin configuration loaded; clients={_clients.Count}");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка загрузки";
            System.Windows.MessageBox.Show(this, ex.Message, "Не удалось загрузить конфигурацию",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _access.Audit("Admin configuration load failed");
        }
        finally { SetBusy(false); }
    }

    private bool FilterClient(object item)
    {
        if (item is not IPData client) return false;
        string search = ClientSearchBox.Text.Trim();
        return search.Length == 0 ||
               Contains(client.Name, search) || Contains(client.ClientId, search) || Contains(client.Inn, search);
    }

    private static bool Contains(string? value, string search) =>
        value?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true;

    private void LoadClient(IPData? client)
    {
        ClearEditor();
        if (client is null) return;
        NameBox.Text = client.Name ?? "";
        ClientIdBox.Text = client.ClientId ?? "";
        InnBox.Text = client.Inn ?? "";
        SelectCombo(ArchitectureBox, client.Architecture ?? "x64");
        ClientPasswordBox.Text = client.Password ?? "";
        TokenBox.Text = client.Token ?? "";
        RuPasswordBox.Text = client.RuDesktop?.Password ?? "";
        RuEnabledCheck.IsChecked = client.RuDesktop?.Enabled == true;
        RuAutoPasswordCheck.IsChecked = client.RuDesktop?.AutoOfferPasswordSetup == true;
        BackupCheck.IsChecked = client.HasLmDatabaseBackup;
        With1CCheck.IsChecked = (client.Tags ?? []).Any(x =>
            string.Equals(x, "With1C", StringComparison.OrdinalIgnoreCase));
        client.Versions ??= new VersionsData();
        SelectVersion(ClientLmVersionBox, client.Versions.LmModule);
        SelectVersion(ClientAtolVersionBox, client.Versions.AtolDriver);
        SelectVersion(ClientEsmVersionBox, client.Versions.ESM);
        SelectVersion(ClientControllerVersionBox, client.Versions.Controller);
    }

    private bool ApplyCurrent(bool showErrors = true)
    {
        if (ClientsGrid.SelectedItem is not IPData client) return true;
        if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(ClientIdBox.Text))
        {
            if (showErrors) ShowValidation("Название и ClientId обязательны.");
            return false;
        }

        string id = ClientIdBox.Text.Trim();
        if (_clients.Any(x => !ReferenceEquals(x, client) &&
                              string.Equals(x.ClientId?.Trim(), id, StringComparison.OrdinalIgnoreCase)))
        {
            if (showErrors) ShowValidation($"ClientId «{id}» уже используется.");
            return false;
        }

        client.Name = NameBox.Text.Trim();
        client.ClientId = id;
        client.Inn = InnBox.Text.Trim();
        client.Architecture = SelectedText(ArchitectureBox, "x64");
        client.Password = ClientPasswordBox.Text;
        client.Token = TokenBox.Text;
        client.HasLmDatabaseBackup = BackupCheck.IsChecked == true;
        client.RuDesktop ??= new RuDesktopSettings();
        client.RuDesktop.Enabled = RuEnabledCheck.IsChecked == true;
        client.RuDesktop.AutoOfferPasswordSetup = RuAutoPasswordCheck.IsChecked == true;
        client.RuDesktop.Password = RuPasswordBox.Text;
        client.Tags ??= [];
        client.Tags.RemoveAll(x => string.Equals(x, "With1C", StringComparison.OrdinalIgnoreCase));
        if (With1CCheck.IsChecked == true) client.Tags.Add("With1C");
        client.Versions ??= new VersionsData();
        client.Versions.LmModule = SelectedOverride(ClientLmVersionBox);
        client.Versions.AtolDriver = SelectedOverride(ClientAtolVersionBox);
        client.Versions.ESM = SelectedOverride(ClientEsmVersionBox);
        client.Versions.Controller = SelectedOverride(ClientControllerVersionBox);
        ClientsGrid.Items.Refresh();
        MarkChanged();
        return true;
    }

    private bool ApplyVersions()
    {
        _versions.HonestFlow = GlobalHonestFlowBox.Text.Trim();
        _versions.LmModule = GlobalLmBox.Text.Trim();
        _versions.AtolDriver = GlobalAtolBox.Text.Trim();
        _versions.ESM = GlobalEsmBox.Text.Trim();
        _versions.Controller = GlobalControllerBox.Text.Trim();
        foreach (string value in new[] { _versions.HonestFlow, _versions.LmModule, _versions.AtolDriver, _versions.ESM, _versions.Controller })
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                !System.Version.TryParse(value.Replace("-", "."), out _))
            {
                ShowValidation($"Некорректная версия: {value}");
                return false;
            }
        }
        return true;
    }

    private void LoadVersionFields()
    {
        GlobalHonestFlowBox.Text = _versions.HonestFlow ?? "";
        GlobalLmBox.Text = _versions.LmModule ?? "";
        GlobalAtolBox.Text = _versions.AtolDriver ?? "";
        GlobalEsmBox.Text = _versions.ESM ?? "";
        GlobalControllerBox.Text = _versions.Controller ?? "";
    }

    private void FillVersionSelectors()
    {
        FillGlobalVersions(GlobalHonestFlowBox, _availableVersions.HonestFlow);
        FillGlobalVersions(GlobalLmBox, _availableVersions.LmModule);
        FillGlobalVersions(GlobalAtolBox, _availableVersions.AtolDriver);
        FillGlobalVersions(GlobalEsmBox, _availableVersions.ESM);
        FillGlobalVersions(GlobalControllerBox, _availableVersions.Controller);
        FillClientVersions(ClientLmVersionBox, _availableVersions.LmModule);
        FillClientVersions(ClientAtolVersionBox, _availableVersions.AtolDriver);
        FillClientVersions(ClientEsmVersionBox, _availableVersions.ESM);
        FillClientVersions(ClientControllerVersionBox, _availableVersions.Controller);
    }

    private static void FillGlobalVersions(System.Windows.Controls.ComboBox combo, IEnumerable<string> versions)
    {
        combo.Items.Clear();
        foreach (var version in versions) combo.Items.Add(version);
    }

    private static void FillClientVersions(System.Windows.Controls.ComboBox combo, IEnumerable<string> versions)
    {
        combo.Items.Clear();
        combo.Items.Add("Глобальная");
        foreach (var version in versions) combo.Items.Add(version);
        combo.SelectedIndex = 0;
    }

    private static void SelectVersion(System.Windows.Controls.ComboBox combo, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            combo.SelectedIndex = 0;
            return;
        }
        if (!combo.Items.Contains(value)) combo.Items.Add(value);
        combo.SelectedItem = value;
    }

    private static string? SelectedOverride(System.Windows.Controls.ComboBox combo)
    {
        string? value = combo.SelectedItem?.ToString();
        return string.IsNullOrWhiteSpace(value) || value == "Глобальная" ? null : value;
    }

    private string Snapshot() => JsonConvert.SerializeObject(new
    {
        Clients = _clients.OrderBy(x => x.ClientId).ToList(),
        Versions = _versions
    });

    private void MarkChanged()
    {
        if (!_loaded) return;
        DraftText.Text = Snapshot() == _baselineJson ? "Изменений нет" : "Есть неопубликованные изменения";
    }

    private void ValidateAll()
    {
        if (_clients.Any(x => string.IsNullOrWhiteSpace(x.Name) || string.IsNullOrWhiteSpace(x.ClientId)))
            throw new InvalidDataException("У каждого клиента должны быть название и ClientId.");
        if (_clients.GroupBy(x => x.ClientId.Trim(), StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
            throw new InvalidDataException("ClientId должны быть уникальными.");
        if (_clients.Any(x => !string.IsNullOrWhiteSpace(x.Architecture) &&
                              x.Architecture is not "x86" and not "x64"))
            throw new InvalidDataException("Допустимая архитектура: x86 или x64.");
    }

    private void SaveProtectedDraft()
    {
        if (!ApplyCurrent() || !ApplyVersions()) return;
        ValidateAll();
        var draft = new AdminDraft { Clients = _clients.ToList(), Versions = _versions };
        string json = JsonConvert.SerializeObject(draft, Formatting.None);
        File.WriteAllText(AppPaths.AdminDraftPath, DpapiProtector.Protect(json));
        DraftText.Text = "Локальный черновик сохранён и защищён DPAPI";
        _access.Audit($"Admin draft saved; clients={_clients.Count}");
    }

    private async Task PublishAsync()
    {
        if (_busy || !ApplyCurrent() || !ApplyVersions()) return;
        try { ValidateAll(); }
        catch (Exception ex) { ShowValidation(ex.Message); return; }

        string current = Snapshot();
        if (current == _baselineJson)
        {
            ShowValidation("Изменений для публикации нет.");
            return;
        }
        if (!YandexDiskSettings.IsConfigured)
        {
            var credentials = new DiskCredentialsWindow { Owner = this };
            if (credentials.ShowDialog() != true) return;
        }

        int changed = CountChangedClients(_baselineJson, current);
        var answer = System.Windows.MessageBox.Show(this,
            $"Будет опубликована новая конфигурация.\n\nКлиентов: {_clients.Count}\nИзменённых карточек: {changed}\n" +
            "Пароли и токены не отображаются в журнале.\n\nПродолжить?",
            "Подтверждение публикации", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        string staging = Path.Combine(Path.GetTempPath(), "HonestDesk-admin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        string ipsPath = Path.Combine(staging, "ips_encrypted.json");
        string versionsPath = Path.Combine(staging, "versions.json");
        string originalVersionsPath = Path.Combine(staging, "versions.original.json");
        try
        {
            string ipsJson = JsonConvert.SerializeObject(_clients.ToList(), Formatting.Indented);
            string versionsJson = JsonConvert.SerializeObject(_versions, Formatting.Indented);
            File.WriteAllText(ipsPath, ConfigManager.Obfuscate(ipsJson));
            File.WriteAllText(versionsPath, versionsJson);
            File.WriteAllText(originalVersionsPath, _originalVersionsJson);

            SetBusy(true, "Публикация версий…");
            _access.Audit($"Admin publication started; clients={_clients.Count}; changed={changed}");
            await _disk.UploadVersionsFileAsync(versionsPath);
            try
            {
                StatusText.Text = "Публикация конфигурации клиентов…";
                await _disk.UploadIpsFileAsync(ipsPath);
            }
            catch
            {
                StatusText.Text = "Откат versions.json после ошибки…";
                await _disk.UploadVersionsFileAsync(originalVersionsPath);
                throw;
            }

            _originalIpsEncrypted = File.ReadAllText(ipsPath);
            _originalVersionsJson = versionsJson;
            _baselineJson = current;
            if (File.Exists(AppPaths.AdminDraftPath)) File.Delete(AppPaths.AdminDraftPath);
            DraftText.Text = "Все изменения опубликованы";
            StatusText.Text = $"Публикация завершена: {DateTime.Now:dd.MM.yyyy HH:mm}";
            _access.Audit($"Admin publication completed; clients={_clients.Count}; changed={changed}");
            System.Windows.MessageBox.Show(this, "Конфигурация успешно опубликована.", "HonestDesk",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _access.Audit("Admin publication failed");
            StatusText.Text = "Публикация не завершена";
            System.Windows.MessageBox.Show(this, ex.Message, "Ошибка публикации",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ClearDirectory(staging);
            SetBusy(false);
        }
    }

    private static int CountChangedClients(string before, string after)
    {
        var oldDraft = JsonConvert.DeserializeAnonymousType(before, new
        {
            Clients = new List<IPData>(),
            Versions = new VersionsData()
        });
        var newDraft = JsonConvert.DeserializeAnonymousType(after, new
        {
            Clients = new List<IPData>(),
            Versions = new VersionsData()
        });
        var oldMap = (oldDraft?.Clients ?? []).ToDictionary(x => x.ClientId ?? "", JsonConvert.SerializeObject,
            StringComparer.OrdinalIgnoreCase);
        var newMap = (newDraft?.Clients ?? []).ToDictionary(x => x.ClientId ?? "", JsonConvert.SerializeObject,
            StringComparer.OrdinalIgnoreCase);
        return oldMap.Keys.Union(newMap.Keys, StringComparer.OrdinalIgnoreCase)
            .Count(key => !oldMap.TryGetValue(key, out var left) || !newMap.TryGetValue(key, out var right) || left != right);
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        PublishButton.IsEnabled = !busy;
        if (status is not null) StatusText.Text = status;
    }

    private static void ClearDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            foreach (var file in Directory.GetFiles(path)) File.Delete(file);
            Directory.Delete(path);
        }
        catch { }
    }

    private void ClearEditor()
    {
        NameBox.Clear(); ClientIdBox.Clear(); InnBox.Clear();
        ClientPasswordBox.Clear(); TokenBox.Clear(); RuPasswordBox.Clear();
        RuEnabledCheck.IsChecked = false; RuAutoPasswordCheck.IsChecked = false;
        BackupCheck.IsChecked = false; With1CCheck.IsChecked = false;
        ClientLmVersionBox.SelectedIndex = 0; ClientAtolVersionBox.SelectedIndex = 0;
        ClientEsmVersionBox.SelectedIndex = 0; ClientControllerVersionBox.SelectedIndex = 0;
        SelectCombo(ArchitectureBox, "x64");
    }

    private void ClearSecrets()
    {
        ClientPasswordBox.Clear(); TokenBox.Clear(); RuPasswordBox.Clear();
    }

    private static void SelectCombo(System.Windows.Controls.ComboBox combo, string value) =>
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(x => x.Content?.ToString() == value);

    private static string SelectedText(System.Windows.Controls.ComboBox combo, string fallback) =>
        (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? fallback;

    private void ShowValidation(string message) =>
        System.Windows.MessageBox.Show(this, message, "Проверьте данные", MessageBoxButton.OK, MessageBoxImage.Warning);

    private void ClientsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        LoadClient(ClientsGrid.SelectedItem as IPData);

    private void ClientSearchBox_TextChanged(object sender, TextChangedEventArgs e) => _clientsView.Refresh();

    private void ApplyClientButton_Click(object sender, RoutedEventArgs e) => ApplyCurrent();

    private void AddClientButton_Click(object sender, RoutedEventArgs e)
    {
        var client = new IPData
        {
            ClientId = "client-" + Guid.NewGuid().ToString("N")[..8],
            Name = "Новый клиент",
            Architecture = "x64"
        };
        _clients.Add(client);
        ClientsGrid.SelectedItem = client;
        MarkChanged();
    }

    private void DeleteClientButton_Click(object sender, RoutedEventArgs e)
    {
        if (ClientsGrid.SelectedItem is not IPData client) return;
        if (System.Windows.MessageBox.Show(this, $"Удалить клиента «{client.Name}» из черновика?",
                "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _clients.Remove(client);
        ClientsGrid.SelectedItem = _clients.FirstOrDefault();
        MarkChanged();
    }

    private void CredentialsButton_Click(object sender, RoutedEventArgs e) =>
        new DiskCredentialsWindow { Owner = this }.ShowDialog();

    private void LicensesButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyCurrent(showErrors: false);
        _access.Audit("WPF license manager opened");
        var clients = _clients
            .Where(x => !string.IsNullOrWhiteSpace(x.ClientId))
            .GroupBy(x => x.ClientId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().Name ?? x.Key, StringComparer.OrdinalIgnoreCase);
        new LicenseManagerWindow(clients, _access) { Owner = this }.ShowDialog();
    }

    private async void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (Snapshot() != _baselineJson &&
            System.Windows.MessageBox.Show(this, "Несохранённые изменения будут потеряны. Продолжить?",
                "Обновить данные", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await LoadRemoteAsync();
    }

    private void SaveDraftButton_Click(object sender, RoutedEventArgs e)
    {
        try { SaveProtectedDraft(); }
        catch (Exception ex) { ShowValidation(ex.Message); }
    }

    private async void PublishButton_Click(object sender, RoutedEventArgs e) => await PublishAsync();

    private sealed class AdminDraft
    {
        public List<IPData> Clients { get; set; } = [];
        public VersionsData Versions { get; set; } = new();
    }
}
