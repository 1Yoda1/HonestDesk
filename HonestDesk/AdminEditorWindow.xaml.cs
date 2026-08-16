using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using HonestDesk.Models;
using HonestDesk.Services;
using HonestDesk.Infrastructure;
using HonestDesk.Infrastructure.Api;
using Newtonsoft.Json;

namespace HonestDesk;

public partial class AdminEditorWindow : Window
{
    private readonly AdminAccessService _access;
    private readonly AppSettings _appSettings;
    private readonly ObservableCollection<IPData> _clients = [];
    private readonly ICollectionView _clientsView;
    private VersionsData _versions = new();
    private AvailableVersionsData _availableVersions = new();
    private SupportMailSettings _supportMailSettings = new();
    private string _baselineJson = "";
    private bool _loaded;
    private bool _busy;
    private bool _serviceInstallHasPassword;
    private readonly HashSet<string> _serverClientIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly DateTimeOffset _sessionUntilUtc;
    private readonly DispatcherTimer _sessionTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    public AdminEditorWindow(AdminAccessService access, DateTimeOffset sessionUntilUtc, AppSettings? appSettings = null)
    {
        InitializeComponent();
        _access = access;
        _appSettings = appSettings ?? new AppSettings();
        _sessionUntilUtc = sessionUntilUtc;
        _clientsView = CollectionViewSource.GetDefaultView(_clients);
        _clientsView.Filter = FilterClient;
        ClientsGrid.ItemsSource = _clientsView;
        Loaded += async (_, _) => await LoadRemoteAsync();
        if (_sessionUntilUtc != DateTimeOffset.MaxValue)
        {
            _sessionTimer.Tick += SessionTimer_Tick;
            _sessionTimer.Start();
        }
        Closed += (_, _) => { _sessionTimer.Stop(); ClearSecrets(); };
    }
    public event Action<IReadOnlyDictionary<string, string>, SupportMailSettings>? LicensesRequested;
    public event Action? SessionExpired;
    private bool _embedded;

    public FrameworkElement DetachContentForEmbedding()
    {
        _embedded = true;
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        var content = (FrameworkElement)Content;
        Content = null;
        return content;
    }

    public Task InitializeEmbeddedAsync() => LoadRemoteAsync();

    public string? SelectedClientId => (ClientsGrid.SelectedItem as IPData)?.ClientId;

    public void RestoreSelectedClient(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return;
        ClientsGrid.SelectedItem = _clients.FirstOrDefault(x =>
            string.Equals(x.ClientId, clientId, StringComparison.OrdinalIgnoreCase));
        ClientsGrid.ScrollIntoView(ClientsGrid.SelectedItem);
    }

    public void RequestLicenses() => LicensesButton_Click(this, new RoutedEventArgs());

    private void SessionTimer_Tick(object? sender, EventArgs e)
    {
        if (DateTimeOffset.UtcNow < _sessionUntilUtc) return;
        _access.Audit("Admin session expired");
        _sessionTimer.Stop();
        if (_embedded)
        {
            StatusText.Text = "Административная сессия завершена.";
            SessionExpired?.Invoke();
            return;
        }
        System.Windows.MessageBox.Show(this, "Административная сессия завершена. Несохранённые изменения не опубликованы.",
            "Сессия заблокирована", MessageBoxButton.OK, MessageBoxImage.Information);
        Close();
    }

    private async Task LoadRemoteAsync()
    {
        if (_busy) return;
        SetBusy(true, "Загрузка конфигурации с HonestLicenseServer…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            HonestLicenseAdminApiClient api = CreateApiClient();
            IReadOnlyList<AdminClient> serverClients = await api.GetClientsAsync(cts.Token, includeInactive: true);
            IReadOnlyList<AdminVersion> serverVersions = await api.GetVersionsAsync(cts.Token);
            IReadOnlyList<AdminAsset> serverAssets = await api.GetAssetsAsync(null, cts.Token);
            AdminServiceInstallAccessSettings serviceInstallAccess =
                await api.GetServiceInstallAccessAsync(cts.Token);
            _serviceInstallHasPassword = serviceInstallAccess.HasPassword;
            ServiceInstallEnabledCheck.IsChecked = serviceInstallAccess.IsEnabled;
            ServiceInstallPasswordBox.Clear();
            ServiceInstallPasswordStateText.Text = serviceInstallAccess.HasPassword
                ? "Пароль настроен; его значение сервер не возвращает."
                : "Пароль ещё не настроен.";
            AssetsGrid.ItemsSource = serverAssets
                .OrderBy(x => x.Component).ThenByDescending(x => x.UpdatedAtUtc).ThenBy(x => x.Architecture).ToList();
            _serverClientIds.Clear();
            var clients = new List<IPData>();
            foreach (AdminClient source in serverClients.Where(x => x.IsActive))
            {
                string clientId = source.ClientId ?? "";
                _serverClientIds.Add(clientId);
                IReadOnlyList<AdminComponentOverride> overrides = await api.GetComponentOverridesAsync(clientId, cts.Token);
                AdminClientIntegrationSettings integration = await api.GetClientIntegrationSettingsAsync(clientId, cts.Token);
                clients.Add(new IPData
                {
                    ClientId = clientId,
                    Name = source.Name ?? clientId,
                    Inn = source.Inn ?? "",
                    Architecture = source.Architecture ?? "x64",
                    HasLmDatabaseBackup = source.HasLmDatabaseBackup,
                    Password = integration.IdentificationCode ?? "",
                    Token = integration.ChzToken ?? "",
                    RuDesktop = new RuDesktopSettings
                    {
                        Enabled = integration.RuDesktopEnabled,
                        AutoOfferPasswordSetup = integration.RuDesktopAutoOfferPasswordSetup
                    },
                    Versions = new VersionsData
                    {
                        LmModule = Override(overrides, "LmModule"),
                        AtolDriver = Override(overrides, "AtolDriver"),
                        ESM = Override(overrides, "ESM"),
                        Controller = Override(overrides, "Controller")
                    }
                });
            }
            _clients.Clear();
            foreach (var client in clients.OrderBy(x => x.Name))
                _clients.Add(client);

            _versions = new VersionsData
            {
                HonestFlow = Version(serverVersions, "HonestFlow"),
                LmModule = Version(serverVersions, "LmModule"),
                AtolDriver = Version(serverVersions, "AtolDriver"),
                ESM = Version(serverVersions, "ESM"),
                Controller = Version(serverVersions, "Controller")
            };
            _availableVersions = new AvailableVersionsData();
            AddAssets(_availableVersions.HonestFlow, serverAssets, "HonestFlow");
            AddAssets(_availableVersions.LmModule, serverAssets, "LmModule");
            AddAssets(_availableVersions.AtolDriver, serverAssets, "AtolDriver");
            AddAssets(_availableVersions.ESM, serverAssets, "ESM");
            AddAssets(_availableVersions.Controller, serverAssets, "Controller");
            AddAvailable(_availableVersions.HonestFlow, _versions.HonestFlow);
            AddAvailable(_availableVersions.LmModule, _versions.LmModule);
            AddAvailable(_availableVersions.AtolDriver, _versions.AtolDriver);
            AddAvailable(_availableVersions.ESM, _versions.ESM);
            AddAvailable(_availableVersions.Controller, _versions.Controller);
            FillVersionSelectors();

            LoadVersionFields();
            _baselineJson = Snapshot();
            _loaded = true;
            ClientsGrid.SelectedItem = _clients.FirstOrDefault();
            StatusText.Text = $"С HonestLicenseServer загружено клиентов: {_clients.Count}; файлов в каталоге: {serverAssets.Count}.";
            DraftText.Text = "Изменений нет";
            _access.Audit($"Admin configuration loaded; clients={_clients.Count}");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка загрузки: " + ex.Message;
            _access.Audit("Admin configuration load failed");
        }
        finally { SetBusy(false); }
    }

    private HonestLicenseAdminApiClient CreateApiClient() => new(
        _appSettings.ApiBaseUrl,
        () => DpapiProtector.Unprotect(_appSettings.ProtectedAdminApiKey));

    private static string? Override(IEnumerable<AdminComponentOverride> values, string component) =>
        values.FirstOrDefault(x => Normalize(x.Component) == Normalize(component))?.RequiredVersion;

    private static string Version(IEnumerable<AdminVersion> values, string application) =>
        values.FirstOrDefault(x => Normalize(x.Application) == Normalize(application))?.CurrentVersion ?? "";

    private static string Normalize(string? value) => (value ?? "").Replace("-", "").Replace("_", "").ToUpperInvariant();
    private static void AddAvailable(ICollection<string> target, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !target.Contains(value, StringComparer.OrdinalIgnoreCase)) target.Add(value);
    }
    private static void AddAssets(ICollection<string> target, IEnumerable<AdminAsset> assets, string component)
    {
        foreach (string version in assets
                     .Where(x => Normalize(x.Component) == Normalize(component) && !string.IsNullOrWhiteSpace(x.Version))
                     .OrderByDescending(x => x.UpdatedAtUtc)
                     .Select(x => x.Version!)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            target.Add(version);
    }

    private void OpenAssetLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AdminAsset asset)
        {
            ShowValidation("Для этого файла сервер не вернул корректную ссылку.");
            return;
        }
        try
        {
            string url = asset.EffectiveUrl;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidDataException("Сервер не вернул корректную ссылку.");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowValidation("Не удалось открыть ссылку: " + ex.Message); }
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
        ClientPasswordBox.IsEnabled = true;
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
        FillClientVersions(ClientLmVersionBox, _availableVersions.LmModule, _versions.LmModule);
        FillClientVersions(ClientAtolVersionBox, _availableVersions.AtolDriver, _versions.AtolDriver);
        FillClientVersions(ClientEsmVersionBox, _availableVersions.ESM, _versions.ESM);
        FillClientVersions(ClientControllerVersionBox, _availableVersions.Controller, _versions.Controller);
    }

    private static void FillGlobalVersions(System.Windows.Controls.ComboBox combo, IEnumerable<string> versions)
    {
        combo.Items.Clear();
        foreach (var version in versions) combo.Items.Add(version);
    }

    private static void FillClientVersions(System.Windows.Controls.ComboBox combo, IEnumerable<string> versions, string? globalVersion)
    {
        combo.Items.Clear();
        combo.Items.Add("Глобальная" + (string.IsNullOrWhiteSpace(globalVersion) ? " (не задана)" : $" ({globalVersion})"));
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
        return string.IsNullOrWhiteSpace(value) || value.StartsWith("Глобальная", StringComparison.Ordinal) ? null : value;
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
        if (_clients.Any(x => string.IsNullOrWhiteSpace(x.Password)))
            throw new InvalidDataException("У каждого клиента должен быть пароль продавца.");
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
        int changed = CountChangedClients(_baselineJson, current);
        var answer = System.Windows.MessageBox.Show(this,
            $"Изменения будут отправлены в HonestLicenseServer.\n\nКлиентов: {_clients.Count}\nИзменённых карточек: {changed}\n\nПродолжить?",
            "Подтверждение публикации", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            SetBusy(true, "Отправка конфигурации в HonestLicenseServer…");
            _access.Audit($"Admin publication started; clients={_clients.Count}; changed={changed}");
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            HonestLicenseAdminApiClient api = CreateApiClient();
            IReadOnlyList<AdminClient> currentServerClients = await api.GetClientsAsync(cts.Token, includeInactive: true);
            Dictionary<string, AdminClient> currentServerClientsById = currentServerClients
                .Where(x => !string.IsNullOrWhiteSpace(x.ClientId))
                .ToDictionary(x => x.ClientId!, StringComparer.OrdinalIgnoreCase);
            foreach (IPData client in _clients)
            {
                if (currentServerClientsById.TryGetValue(client.ClientId, out AdminClient? serverClient))
                {
                    if (!serverClient.IsActive)
                        throw new InvalidOperationException($"Клиент {client.ClientId} отключён на HonestLicenseServer. Повторная активация из этого окна не реализована.");
                    await api.UpdateClientAsync(client.ClientId, new { client.Name, client.Inn, client.Architecture, isActive = true, client.HasLmDatabaseBackup }, cts.Token);
                }
                else
                {
                    await api.CreateClientAsync(new { client.ClientId, client.Name, login = client.ClientId, password = client.Password, client.Inn, client.Architecture, client.HasLmDatabaseBackup }, cts.Token);
                    currentServerClientsById[client.ClientId] = new AdminClient { ClientId = client.ClientId, IsActive = true };
                }
                await api.PutClientIntegrationSettingsAsync(client.ClientId, client.Password, client.Token,
                    client.RuDesktop?.Enabled == true,
                    client.RuDesktop?.AutoOfferPasswordSetup == true,
                    cts.Token);
                await api.PutComponentOverrideAsync(client.ClientId, "LmModule", client.Versions?.LmModule, cts.Token);
                await api.PutComponentOverrideAsync(client.ClientId, "AtolDriver", client.Versions?.AtolDriver, cts.Token);
                await api.PutComponentOverrideAsync(client.ClientId, "ESM", client.Versions?.ESM, cts.Token);
                await api.PutComponentOverrideAsync(client.ClientId, "Controller", client.Versions?.Controller, cts.Token);
            }
            await api.PutVersionAsync("HonestFlow", _versions.HonestFlow ?? string.Empty, cts.Token);
            await api.PutVersionAsync("LmModule", _versions.LmModule ?? string.Empty, cts.Token);
            await api.PutVersionAsync("AtolDriver", _versions.AtolDriver ?? string.Empty, cts.Token);
            await api.PutVersionAsync("ESM", _versions.ESM ?? string.Empty, cts.Token);
            await api.PutVersionAsync("Controller", _versions.Controller ?? string.Empty, cts.Token);
            _serverClientIds.Clear();
            foreach (IPData client in _clients) _serverClientIds.Add(client.ClientId);
            _baselineJson = current;
            if (File.Exists(AppPaths.AdminDraftPath)) File.Delete(AppPaths.AdminDraftPath);
            DraftText.Text = "Все изменения опубликованы";
            StatusText.Text = $"Публикация завершена: {DateTime.Now:dd.MM.yyyy HH:mm}";
            _access.Audit($"Admin publication completed; clients={_clients.Count}; changed={changed}");
            System.Windows.MessageBox.Show(this, "Конфигурация сохранена на HonestLicenseServer.", "HonestDesk",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _access.Audit("Admin publication failed");
            StatusText.Text = "Публикация не завершена: " + ex.Message;
        }
        finally
        {
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
        ServiceInstallSaveButton.IsEnabled = !busy;
        if (status is not null) StatusText.Text = status;
    }

    private async void SaveServiceInstallAccess_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string newPassword = ServiceInstallPasswordBox.Password;
        if (!string.IsNullOrEmpty(newPassword) && newPassword.Length < 10)
        {
            StatusText.Text = "Новый пароль сервисного доступа должен содержать не менее 10 символов.";
            return;
        }
        if (ServiceInstallEnabledCheck.IsChecked == true && !_serviceInstallHasPassword &&
            string.IsNullOrEmpty(newPassword))
        {
            StatusText.Text = "Перед включением сервисного доступа задайте новый пароль.";
            return;
        }

        SetBusy(true, "Сохранение сервисного доступа…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            AdminServiceInstallAccessSettings saved = await CreateApiClient()
                .PutServiceInstallAccessAsync(
                    ServiceInstallEnabledCheck.IsChecked == true,
                    string.IsNullOrEmpty(newPassword) ? null : newPassword,
                    cts.Token);
            _serviceInstallHasPassword = saved.HasPassword;
            ServiceInstallEnabledCheck.IsChecked = saved.IsEnabled;
            ServiceInstallPasswordBox.Clear();
            ServiceInstallPasswordStateText.Text = "Пароль настроен; его значение сервер не возвращает.";
            StatusText.Text = saved.IsEnabled
                ? "Сервисный доступ к установке включён."
                : "Сервисный доступ к установке отключён.";
            _access.Audit($"Service install access updated; enabled={saved.IsEnabled}");
        }
        catch (Exception ex)
        {
            ServiceInstallPasswordBox.Clear();
            StatusText.Text = "Не удалось сохранить сервисный доступ: " + ex.Message;
            _access.Audit("Service install access update failed");
        }
        finally { SetBusy(false); }
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
        ClientPasswordBox.Clear(); ClientPasswordBox.IsEnabled = true; TokenBox.Clear(); RuPasswordBox.Clear();
        RuEnabledCheck.IsChecked = false; RuAutoPasswordCheck.IsChecked = false;
        BackupCheck.IsChecked = false; With1CCheck.IsChecked = false;
        ClientLmVersionBox.SelectedIndex = 0; ClientAtolVersionBox.SelectedIndex = 0;
        ClientEsmVersionBox.SelectedIndex = 0; ClientControllerVersionBox.SelectedIndex = 0;
        SelectCombo(ArchitectureBox, "x64");
    }

    private void ClearSecrets()
    {
        ClientPasswordBox.Clear(); TokenBox.Clear(); RuPasswordBox.Clear(); ServiceInstallPasswordBox.Clear();
    }

    private static void SelectCombo(System.Windows.Controls.ComboBox combo, string value) =>
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(x => x.Content?.ToString() == value);

    private static string SelectedText(System.Windows.Controls.ComboBox combo, string fallback) =>
        (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? fallback;

    private void ShowValidation(string message) => StatusText.Text = message;

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

    private async void DeleteClientButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (ClientsGrid.SelectedItem is not IPData client) return;
        bool existsOnServer = _serverClientIds.Contains(client.ClientId);
        string question = existsOnServer
            ? $"Отключить клиента «{client.Name}» на HonestLicenseServer? Активные сессии клиента будут отозваны."
            : $"Удалить нового клиента «{client.Name}» из локального черновика?";
        if (System.Windows.MessageBox.Show(this, question,
                "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!existsOnServer)
        {
            _clients.Remove(client);
            ClientsGrid.SelectedItem = _clients.FirstOrDefault();
            MarkChanged();
            return;
        }

        SetBusy(true, "Отключение клиента на HonestLicenseServer…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            HonestLicenseAdminApiClient api = CreateApiClient();
            IReadOnlyList<AdminClient> serverClients = await api.GetClientsAsync(cts.Token, includeInactive: true);
            AdminClient serverClient = serverClients.SingleOrDefault(x =>
                    string.Equals(x.ClientId, client.ClientId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Клиент не найден на HonestLicenseServer.");
            await api.UpdateClientAsync(client.ClientId, new
            {
                name = serverClient.Name ?? client.ClientId,
                serverClient.Inn,
                architecture = serverClient.Architecture ?? "x64",
                isActive = false,
                serverClient.HasLmDatabaseBackup
            }, cts.Token);
            _clients.Remove(client);
            ClientsGrid.SelectedItem = _clients.FirstOrDefault();
            AdminDraft? baseline = JsonConvert.DeserializeObject<AdminDraft>(_baselineJson);
            baseline?.Clients.RemoveAll(x => string.Equals(x.ClientId, client.ClientId, StringComparison.OrdinalIgnoreCase));
            if (baseline is not null)
                _baselineJson = JsonConvert.SerializeObject(new
                {
                    Clients = baseline.Clients.OrderBy(x => x.ClientId).ToList(),
                    Versions = baseline.Versions
                });
            MarkChanged();
            StatusText.Text = "Клиент отключён на HonestLicenseServer.";
            _access.Audit($"Client disabled; client={client.ClientId}");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось отключить клиента: " + ex.Message;
            _access.Audit($"Client disable failed; client={client.ClientId}");
        }
        finally { SetBusy(false); }
    }

    private void CredentialsButton_Click(object sender, RoutedEventArgs e) =>
        new DiskCredentialsWindow { Owner = this }.ShowDialog();

    private void LifecycleButton_Click(object sender, RoutedEventArgs e) =>
        new LifecycleAdministrationWindow(_access) { Owner = this }.ShowDialog();

    private void LicensesButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyCurrent(showErrors: false);
        _access.Audit("WPF license manager opened");
        var clients = _clients
            .Where(x => !string.IsNullOrWhiteSpace(x.ClientId))
            .GroupBy(x => x.ClientId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().Name ?? x.Key, StringComparer.OrdinalIgnoreCase);
        if (LicensesRequested is not null)
        {
            LicensesRequested.Invoke(clients, _supportMailSettings);
            return;
        }

        StatusText.Text = "Раздел лицензий доступен через главное окно HonestDesk.";
    }

    private void ApplyRegistrationMailOverride()
    {
        string registrationEmail = (_appSettings.RegistrationEmail ?? "").Trim();
        string registrationPassword = DpapiProtector.Unprotect(_appSettings.ProtectedRegistrationAppPassword);

        if (string.IsNullOrWhiteSpace(registrationEmail) &&
            string.IsNullOrWhiteSpace(registrationPassword))
        {
            return;
        }

        _supportMailSettings.SenderEmail = registrationEmail;
        _supportMailSettings.SenderPassword = registrationPassword;
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
