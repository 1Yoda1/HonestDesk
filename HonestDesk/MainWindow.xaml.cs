using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HonestDesk.Models;
using HonestDesk.Services;
using HonestDesk.Infrastructure.Api;
using HonestDesk.Infrastructure.Licensing;
using Forms = System.Windows.Forms;

namespace HonestDesk;

public partial class MainWindow : Window
{
    private readonly AppLogger _logger = new();
    private readonly SettingsService _settingsService = new();
    private readonly StateService _stateService = new();
    private readonly RuDesktopService _ruDesktopService;
    private readonly RemoteClientConfigService _remoteClientConfigService;
    private readonly AdminAccessService _adminAccess = new();
    private readonly DispatcherTimer _timer = new();
    private readonly ObservableCollection<HelpRequest> _requests = [];
    private readonly Forms.NotifyIcon _notifyIcon = new();
    private AppSettings _settings;
    private AppState _state;
    private bool _checking;
    private bool _connecting;
    private bool _resolvingDeviceRequest;
    private bool _exitRequested;
    private bool _startupStarted;
    private AdminEditorWindow? _adminEditor;
    private LicenseManagerWindow? _licenseManager;
    private FrameworkElement? _adminContent;
    private FrameworkElement? _licenseContent;
    private FrameworkElement? _settingsContent;
    private HelpRequest? _lastNotifiedRequest;
    private DateTimeOffset _adminSessionUntilUtc;

    public MainWindow()
    {
        InitializeComponent();
        _ruDesktopService = new RuDesktopService(_logger);
        _remoteClientConfigService = new RemoteClientConfigService(_logger);
        _settings = _settingsService.Load();
        _state = _stateService.Load();
        RequestsGrid.ItemsSource = _requests;
        BuildTray();
        ConfigureTimer();
        ReloadRequests();
        if (_settingsService.LastLoadError is not null)
            _logger.Error("Startup stage: Loading settings.json; defaults are active until settings are saved",
                _settingsService.LastLoadError);
        if (_stateService.LastLoadError is not null)
            _logger.Error("Startup stage: Loading optional state.json; empty UI state is active",
                _stateService.LastLoadError);
        _logger.Info("Startup stage: MainWindow constructed");
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_startupStarted) return;
        _startupStarted = true;
        await InitializeStartupAsync();
    }

    private async Task InitializeStartupAsync()
    {
        SetStatus("Инициализация HonestDesk…", "#F3A847");
        try
        {
            if (_settingsService.LastLoadError is not null)
            {
                SetStatus("Файл настроек повреждён — требуется проверка", "#F06B78");
                ShowWarning("Не удалось прочитать settings.json. Настройки не удалены. " +
                            "Проверьте и сохраните их заново. Подробности записаны в журнал.");
                ShowSettings();
            }
            else if (string.IsNullOrWhiteSpace(_settings.ProtectedAdminApiKey))
            {
                ShowSettings();
            }

            if (!string.IsNullOrWhiteSpace(_settings.ProtectedAdminApiKey))
            {
                SetStatus("Загрузка данных HonestLicenseServer…", "#F3A847");
                await CheckNowAsync();
            }
            else
            {
                SetStatus("Настройте подключение к HonestLicenseServer", "#F3A847");
            }

            await RestoreSelectedSectionAsync();
            if (!string.IsNullOrWhiteSpace(_settings.ProtectedAdminApiKey))
                ResetTimer();
            _logger.Info("Startup stage: Ready");
        }
        catch (Exception ex)
        {
            _logger.Error("Startup stage: MainWindow initialization", ex);
            SetStatus("Ошибка запуска. Исправьте настройки и нажмите «Обновить заявки».", "#F06B78");
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
            Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exitRequested)
        {
            e.Cancel = true;
            Hide();
            WindowState = WindowState.Minimized;
            return;
        }
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        base.OnClosing(e);
    }

    private void BuildTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть HonestDesk", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Проверить сейчас", null, (_, _) => Dispatcher.Invoke(async () => await CheckNowAsync()));
        menu.Items.Add("Настройки", null, (_, _) => Dispatcher.Invoke(ShowSettings));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        var iconResource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/HonestDesk;component/Resources/hd.ico"))
            ?? throw new InvalidOperationException("HonestDesk tray icon resource was not found.");
        using var trayIcon = new System.Drawing.Icon(iconResource.Stream);
        _notifyIcon.Icon = (System.Drawing.Icon)trayIcon.Clone();
        _notifyIcon.Text = "HonestDesk";
        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.Visible = true;
        _notifyIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        _notifyIcon.BalloonTipClicked += (_, _) => Dispatcher.Invoke(() => OpenRequest(_lastNotifiedRequest));
    }

    private void ConfigureTimer()
    {
        _timer.Tick += async (_, _) => await CheckNowAsync();
    }

    private void ResetTimer()
    {
        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(Math.Max(10, _settings.CheckIntervalSeconds));
        _timer.Start();
    }

    private async Task CheckNowAsync()
    {
        if (_checking) return;
        _checking = true;
        CheckButton.IsEnabled = false;
        SetStatus("Синхронизация с HonestLicenseServer…", "#F3A847");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            HonestLicenseAdminApiClient api = CreateApiClient();
            IReadOnlyList<AdminSupportRequest> support = await api.GetSupportRequestsAsync(null, cts.Token);
            IReadOnlyList<AdminDeviceRequest> devices = await api.GetDeviceRequestsAsync("Pending", cts.Token);
            IReadOnlyList<AdminDevice> registeredDevices = await api.GetDevicesAsync(null, cts.Token);
            IReadOnlyList<AdminLicense> licenses = await api.GetLicensesAsync(null, cts.Token);
            var licensedDeviceKeys = new HashSet<string>(licenses
                .Where(x => string.Equals(x.Status, "Active", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(x.ClientId) && !string.IsNullOrWhiteSpace(x.DeviceId))
                .Select(x => ServerEntityKey(x.ClientId, x.DeviceId)), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, AdminDevice> registeredByExternalId = registeredDevices
                .Where(x => !string.IsNullOrWhiteSpace(x.ClientId) && !string.IsNullOrWhiteSpace(x.DeviceId))
                .GroupBy(x => ServerEntityKey(x.ClientId, x.DeviceId), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            List<HelpRequest> incoming = support.Select(x => MapSupportRequest(x, registeredByExternalId))
                .Concat(devices.Select(MapDeviceRequest))
                .Concat(registeredDevices
                    .Where(x => string.Equals(x.Status, "Active", StringComparison.OrdinalIgnoreCase) &&
                                !string.IsNullOrWhiteSpace(x.ClientId) && !string.IsNullOrWhiteSpace(x.DeviceId) &&
                                !licensedDeviceKeys.Contains(ServerEntityKey(x.ClientId, x.DeviceId)))
                    .Select(MapUnlicensedDevice)).ToList();
            Dictionary<string, HelpRequest> existing = _state.Requests
                .ToDictionary(x => x.Uid, StringComparer.Ordinal);
            List<HelpRequest> newRequests = incoming
                .Where(x => !existing.ContainsKey(x.Uid)).ToList();
            _state.Requests = incoming;
            SetStatus($"Сервер: заявок {incoming.Count}", "#48D39C");
            SideLastCheckText.Text = $"Последняя проверка: {DateTime.Now:HH:mm:ss}";

            foreach (HelpRequest request in newRequests)
            {
                _logger.Info($"New server request: {request.ClientName}, {request.ProblemType}");
                ShowNotification(request);
            }

            _stateService.Save(_state);
            ReloadRequests();
        }
        catch (Exception ex)
        {
            _logger.Error("HonestLicenseServer synchronization failed", ex);
            SetStatus("Ошибка API: " + ex.Message, "#F06B78");
        }
        finally
        {
            _checking = false;
            CheckButton.IsEnabled = true;
        }
    }

    private HonestLicenseAdminApiClient CreateApiClient() => new(
        _settings.ApiBaseUrl,
        () => DpapiProtector.Unprotect(_settings.ProtectedAdminApiKey));

    private static HelpRequest MapSupportRequest(AdminSupportRequest value, IReadOnlyDictionary<string, AdminDevice> devices)
    {
        devices.TryGetValue(ServerEntityKey(value.ClientId, value.ExternalDeviceId), out AdminDevice? device);
        return new HelpRequest
        {
        Uid = "api-support-" + value.Id,
        MessageId = value.Id.ToString(),
        Subject = value.Subject ?? "Запрос поддержки",
        ClientId = value.ClientId ?? "",
        ClientName = value.ClientId ?? "Клиент HonestFlow",
        MachineName = device?.Name ?? value.ExternalDeviceId ?? "",
        DeviceId = value.ExternalDeviceId ?? "",
        Address = device?.Address ?? "",
        HonestFlowVersion = value.HonestFlowVersion ?? "",
        ProblemType = value.Subject ?? "Запрос поддержки",
        Message = value.Message ?? "",
        CreatedAt = value.CreatedAtUtc,
        IsHandled = string.Equals(value.Status, "Resolved", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value.Status, "Closed", StringComparison.OrdinalIgnoreCase)
        };
    }

    private static HelpRequest MapDeviceRequest(AdminDeviceRequest value) => new()
    {
        Uid = "api-device-" + value.Id,
        MessageId = value.Id.ToString(),
        Subject = "Регистрация устройства",
        ClientId = value.ClientId ?? "",
        ClientName = value.ClientName ?? "",
        InnMasked = value.ClientInn ?? "",
        MachineName = value.RequestedName ?? "",
        DeviceId = value.DeviceId ?? "",
        Address = value.RequestedAddress ?? "",
        HonestFlowVersion = value.HonestFlowVersion ?? "",
        ProblemType = "Регистрация устройства / доступ к HonestFlow",
        Message = $"Статус: {DisplayValue(value.Status)}\nКомментарий: {DisplayValue(value.Comment)}",
        CreatedAt = value.RequestedAtUtc,
        IsHandled = !string.Equals(value.Status, "Pending", StringComparison.OrdinalIgnoreCase)
    };

    private static HelpRequest MapUnlicensedDevice(AdminDevice value) => new()
    {
        Uid = "api-unlicensed-" + value.Id,
        MessageId = value.Id.ToString(),
        Subject = "Лицензия устройства",
        ClientId = value.ClientId ?? "",
        ClientName = value.ClientName ?? value.ClientId ?? "Клиент HonestFlow",
        MachineName = value.Name ?? value.DeviceId ?? "",
        DeviceId = value.DeviceId ?? "",
        Address = value.Address ?? "",
        ProblemType = "Устройство зарегистрировано — лицензия не выдана",
        Message = $"DeviceId: {value.DeviceId}\nАдрес: {value.Address}\nРегистрация подтверждена. Требуется подписать и опубликовать лицензию.",
        CreatedAt = value.RegisteredAtUtc,
        IsHandled = false
    };

    private void SetStatus(string text, string color)
    {
        SideStatusText.Text = text;
        SideStatusDot.Fill = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(color)!;
    }

    private void ShowNotification(HelpRequest request)
    {
        _lastNotifiedRequest = request;
        _notifyIcon.ShowBalloonTip(10000,
            string.IsNullOrWhiteSpace(request.ClientName) ? "Новая заявка HonestFlow" : request.ClientName,
            string.IsNullOrWhiteSpace(request.ProblemType) ? "Запрос помощи" : request.ProblemType,
            Forms.ToolTipIcon.Info);
    }

    private void ReloadRequests()
    {
        var selectedUid = SelectedRequest()?.Uid;
        _requests.Clear();
        foreach (var request in _state.Requests.OrderByDescending(x => x.CreatedAt))
            _requests.Add(request);

        TotalCountText.Text = _requests.Count.ToString();
        OpenCountText.Text = _requests.Count(x => !x.IsHandled).ToString();
        HandledCountText.Text = _requests.Count(x => x.IsHandled).ToString();
        EmptyHint.Visibility = _requests.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RequestsGrid.SelectedItem = _requests.FirstOrDefault(x => x.Uid == selectedUid) ?? _requests.FirstOrDefault();
        ShowSelectedDetails();
    }

    private HelpRequest? SelectedRequest() => RequestsGrid.SelectedItem as HelpRequest;

    private void ShowSelectedDetails()
    {
        var request = SelectedRequest();
        DetailsText.Text = request is null
            ? "Выберите заявку, чтобы увидеть подробности"
            : BuildRequestDetails(request);
        DetailsText.Foreground = request is null
            ? (System.Windows.Media.Brush)FindResource("Muted")
            : (System.Windows.Media.Brush)FindResource("Ink");
    }

    private static string BuildRequestDetails(HelpRequest request)
    {
        if (TryGetDeviceRequestId(request, out _))
        {
            string requestedAt = request.CreatedAt == default
                ? "—"
                : request.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
            return $"Клиент: {DisplayValue(request.ClientName)}\n" +
                   $"ИНН: {DisplayValue(request.InnMasked)}\n" +
                   $"Адрес: {DisplayValue(request.Address)}\n" +
                   $"Компьютер: {DisplayValue(request.MachineName)}\n" +
                   $"Device ID: {DisplayValue(request.DeviceId)}\n" +
                   $"Версия HonestFlow: {DisplayValue(request.HonestFlowVersion)}\n" +
                   $"Дата заявки: {requestedAt}\n\n{request.Message}";
        }

        return $"Клиент: {request.ClientName}\nИНН: {request.InnMasked}\nАдрес: {request.Address}\n" +
               $"Компьютер: {request.MachineName}  ·  {request.WindowsUser}\nRuDesktop ID: {request.RuDesktopId}\n" +
               $"HonestFlow: {request.HonestFlowVersion}\nТип проблемы: {request.ProblemType}\n" +
               $"Получено: {request.CreatedAt:dd.MM.yyyy HH:mm:ss zzz}\n\n{request.Message}";
    }

    private static string DisplayValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    private async Task ConnectSelectedAsync()
    {
        if (_connecting || SelectedRequest() is not { } request) return;
        if (string.IsNullOrWhiteSpace(request.RuDesktopId))
        {
            ShowWarning("В заявке отсутствует RuDesktop ID.");
            return;
        }

        _connecting = true;
        try
        {
            SetStatus("Получаем настройки…", "#F3A847");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            ClientMatchResult match = await _remoteClientConfigService.FindClientAsync(
                request.ClientId, request.ClientName, request.InnMasked, cts.Token);
            if (!match.Success)
            {
                SetStatus("Подключение не выполнено", "#F06B78");
                ShowWarning(match.Error);
                return;
            }
            request.ClientId = match.Client!.ClientId;
            string password = match.Client.RuDesktop.Password;
            if (string.IsNullOrWhiteSpace(password))
            {
                SetStatus("Подключение не выполнено", "#F06B78");
                ShowWarning($"Для клиента «{request.ClientName}» не задан пароль RuDesktop.");
                return;
            }

            var result = _ruDesktopService.Connect(_settings.RuDesktopPath, request.RuDesktopId, password);
            if (result == RuDesktopConnectResult.Started)
            {
                SetStatus("RuDesktop запущен", "#48D39C");
                return;
            }

            var error = result switch
            {
                RuDesktopConnectResult.ExecutableNotFound => "RuDesktop.exe не найден. Укажите путь в настройках.",
                RuDesktopConnectResult.InvalidPeerId => "В заявке указан некорректный RuDesktop ID.",
                RuDesktopConnectResult.PasswordMissing => "Для клиента не задан пароль RuDesktop.",
                _ => "Не удалось запустить подключение RuDesktop. Подробности записаны в журнал."
            };
            ShowWarning(error);
        }
        finally { _connecting = false; }
    }

    private void ShowSettings()
    {
        var window = new SettingsWindow(_settings) { Owner = this };
        if (window.ShowDialog() == true)
        {
            _settings = window.Settings;
            _settingsService.Save(_settings);
            ResetTimer();
            SetStatus("Настройки сохранены", "#48D39C");
            _logger.Info("Settings saved");
        }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OpenRequest(HelpRequest? request)
    {
        ShowFromTray();
        if (request is not null)
            RequestsGrid.SelectedItem = _requests.FirstOrDefault(x => x.Uid == request.Uid);
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        _timer.Stop();
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void ShowWarning(string message) =>
        System.Windows.MessageBox.Show(this, message, "HonestDesk", MessageBoxButton.OK, MessageBoxImage.Warning);

    private async void CheckButton_Click(object sender, RoutedEventArgs e) => await CheckNowAsync();
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettingsSection();
    private void DashboardButton_Click(object sender, RoutedEventArgs e) => ShowSection("Support", null);
    private async void ConnectButton_Click(object sender, RoutedEventArgs e) => await ConnectSelectedAsync();
    private async void RequestsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await ConnectSelectedAsync();
    private void RequestsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSelectedDetails();

    private void CopyIdButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRequest() is { RuDesktopId.Length: > 0 } request)
        {
            System.Windows.Clipboard.SetText(request.RuDesktopId);
            SetStatus("ID скопирован", "#48D39C");
        }
    }

    private void OpenRuDesktopButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_ruDesktopService.Open(_settings.RuDesktopPath))
            ShowWarning("RuDesktop.exe не найден. Укажите путь в настройках.");
    }

    private async void ClientProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRequest() is not { } request) return;
        SetStatus("Загружаем карточку клиента…", "#F3A847");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            HonestLicenseAdminApiClient api = CreateApiClient();
            IReadOnlyList<AdminClient> clients = await api.GetClientsAsync(cts.Token);
            AdminClient? source = clients.FirstOrDefault(x => string.Equals(x.ClientId, request.ClientId, StringComparison.OrdinalIgnoreCase))
                ?? clients.FirstOrDefault(x => string.Equals(x.Name, request.ClientName, StringComparison.CurrentCultureIgnoreCase));
            if (source is null) throw new InvalidDataException("Клиент не найден на HonestLicenseServer.");
            string clientId = source.ClientId ?? "";
            AdminClientIntegrationSettings integration = await api.GetClientIntegrationSettingsAsync(clientId, cts.Token);
            IReadOnlyList<AdminComponentOverride> overrides = await api.GetComponentOverridesAsync(clientId, cts.Token);
            IReadOnlyList<AdminVersion> versions = await api.GetVersionsAsync(cts.Token);
            IReadOnlyList<AdminDevice> devices = await api.GetDevicesAsync(clientId, cts.Token);
            string? Component(string name) => overrides.FirstOrDefault(x => NormalizeApiName(x.Component) == NormalizeApiName(name))?.RequiredVersion
                ?? versions.FirstOrDefault(x => NormalizeApiName(x.Application) == NormalizeApiName(name))?.CurrentVersion;
            var client = new ClientConfiguration
            {
                ClientId = clientId,
                Name = source.Name ?? clientId,
                Inn = source.Inn ?? "",
                Architecture = source.Architecture ?? "x64",
                HasLmDatabaseBackup = source.HasLmDatabaseBackup,
                Password = integration.IdentificationCode ?? "",
                Token = integration.ChzToken ?? "",
                Versions = new ComponentVersions
                {
                    HonestFlow = Component("HonestFlow"), LmModule = Component("LmModule"),
                    AtolDriver = Component("AtolDriver"), ESM = Component("ESM"), Controller = Component("Controller")
                },
                Devices = devices.Select(x => new ClientDeviceConfiguration
                {
                    DeviceId = x.DeviceId ?? "", Name = x.Name ?? "", Address = x.Address ?? "", Status = x.Status ?? ""
                }).ToList()
            };

            if (!string.Equals(request.ClientId, client.ClientId, StringComparison.Ordinal))
            {
                request.ClientId = client.ClientId;
                _stateService.Save(_state);
            }
            SetStatus("Карточка клиента загружена", "#48D39C");
            var window = new ClientProfileWindow(client, "HonestLicenseServer", OpenAdminEditor) { Owner = this };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            SetStatus("Клиент не найден", "#F06B78");
            _logger.Error("Server client profile load failed", ex);
            ShowWarning(ex.Message);
        }
    }

    private static string NormalizeApiName(string? value) => (value ?? "").Replace("-", "").Replace("_", "").ToUpperInvariant();

    private static string ServerEntityKey(string? clientId, string? deviceId) =>
        (clientId ?? "") + "\n" + (deviceId ?? "");

    private async void OpenConfigEditorButton_Click(object sender, RoutedEventArgs e) =>
        await OpenAdminEditorSafelyAsync();

    private async void OpenAdminEditor() => await OpenAdminEditorSafelyAsync();

    private async Task OpenAdminEditorSafelyAsync()
    {
        try
        {
            await OpenAdminEditorAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Opening admin editor", ex);
            SetStatus("Не удалось открыть раздел клиентов", "#F06B78");
            ShowWarning("Не удалось открыть раздел клиентов. Подробности записаны в журнал.");
        }
    }

    private async Task OpenAdminEditorAsync()
    {
        if (!EnsureAdminSession()) return;
        if (_adminEditor is null)
        {
            _adminEditor = new AdminEditorWindow(_adminAccess, _adminSessionUntilUtc, _settings) { Owner = this };
            _adminEditor.LicensesRequested += ShowLicenses;
            _adminEditor.SessionExpired += () =>
            {
                _adminSessionUntilUtc = default;
                ShowSection("Support", null);
                SetStatus("Административная сессия завершена", "#F3A847");
            };
            _adminContent = _adminEditor.DetachContentForEmbedding();
            await _adminEditor.InitializeEmbeddedAsync();
            _adminEditor.RestoreSelectedClient(_state.SelectedClientId);
        }
        _adminAccess.Audit("Embedded admin editor opened");
        ShowSection("Clients", _adminContent);
    }

    private bool EnsureAdminSession()
    {
        if (_adminSessionUntilUtc > DateTimeOffset.UtcNow) return true;
        _adminSessionUntilUtc = default;
        var access = new AdminAccessWindow(_adminAccess) { Owner = this };
        if (access.ShowDialog() != true) return false;
        _adminSessionUntilUtc = DateTimeOffset.MaxValue;
        _adminEditor = null;
        _adminContent = null;
        _licenseManager = null;
        _licenseContent = null;
        return true;
    }

    private async void OpenLicensesButton_Click(object sender, RoutedEventArgs e) =>
        await OpenLicensesSafelyAsync();

    private async Task OpenLicensesSafelyAsync()
    {
        try
        {
            if (!EnsureAdminSession()) return;
            if (_adminEditor is null) await OpenAdminEditorAsync();
            _adminEditor?.RequestLicenses();
        }
        catch (Exception ex)
        {
            _logger.Error("Opening license manager", ex);
            SetStatus("Не удалось открыть раздел лицензий", "#F06B78");
            ShowWarning("Не удалось открыть раздел лицензий. Подробности записаны в журнал.");
        }
    }

    private void ShowLicenses(IReadOnlyDictionary<string, string> clients, SupportMailSettings mailSettings)
    {
        if (_licenseManager is null)
        {
            _licenseManager = new LicenseManagerWindow(clients, _adminAccess, mailSettings) { Owner = this };
            _licenseContent = _licenseManager.DetachContentForEmbedding();
        }
        ShowSection("Licenses", _licenseContent);
    }

    private void ShowSettingsSection()
    {
        if (_settingsContent is null)
        {
            var button = new System.Windows.Controls.Button { Content = "Изменить настройки", Padding = new System.Windows.Thickness(18, 10, 18, 10), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            button.SetResourceReference(StyleProperty, "PrimaryButton");
            button.Click += (_, _) => ShowSettings();
            _settingsContent = new StackPanel
            {
                Margin = new Thickness(34, 28, 34, 30),
                Children =
                {
                    new TextBlock { Text = "Настройки", FontSize = 29, FontWeight = FontWeights.SemiBold },
                    new TextBlock { Text = "Почта, интервал проверки и подключение RuDesktop", Margin = new Thickness(0, 6, 0, 22), Foreground = (System.Windows.Media.Brush)FindResource("Muted") },
                    button
                }
            };
        }
        ShowSection("Settings", _settingsContent);
    }

    private async void OpenJournalButton_Click(object sender, RoutedEventArgs e) =>
        await OpenJournalAsync();

    private async Task OpenJournalAsync()
    {
        var text = new System.Windows.Controls.TextBox
        {
            Text = "Загрузка последних записей…",
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var grid = new Grid { Margin = new Thickness(34, 28, 34, 30) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.Children.Add(new TextBlock { Text = "Журнал операций", FontSize = 29, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) });
        Grid.SetRow(text, 1);
        grid.Children.Add(text);
        ShowSection("Journal", grid);
        try
        {
            string content = await Task.Run(() =>
                "=== HonestDesk ===" + Environment.NewLine + ReadLogTail(AppPaths.LogPath) +
                Environment.NewLine + Environment.NewLine + "=== Административные операции ===" + Environment.NewLine +
                ReadLogTail(AppPaths.AdminAuditPath));
            if (_state.SelectedSection == "Journal")
            {
                text.Text = content;
                text.ScrollToEnd();
            }
        }
        catch (Exception ex)
        {
            text.Text = "Не удалось прочитать журнал: " + ex.Message;
            _logger.Error("Journal read failed", ex);
        }
    }

    private static string ReadLogTail(string path, int maxBytes = 512 * 1024)
    {
        if (!File.Exists(path)) return "Журнал пока пуст.";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, stream.Length - maxBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        if (start > 0) reader.ReadLine();
        string value = reader.ReadToEnd();
        return start > 0 ? "… показан только конец большого журнала …" + Environment.NewLine + value : value;
    }

    private void ShowSection(string section, FrameworkElement? content)
    {
        if (_adminEditor is not null) _state.SelectedClientId = _adminEditor.SelectedClientId;
        _state.SelectedSection = section;
        _stateService.Save(_state);
        DashboardView.Visibility = content is null ? Visibility.Visible : Visibility.Collapsed;
        SectionHost.Visibility = content is null ? Visibility.Collapsed : Visibility.Visible;
        SectionHost.Content = content;
    }

    private async Task RestoreSelectedSectionAsync()
    {
        switch (_state.SelectedSection)
        {
            case "Clients":
            case "Licenses":
                _logger.Info("Startup stage: Protected section restore skipped; operator unlock is required.");
                ShowSection("Support", null);
                break;
            case "Settings": ShowSettingsSection(); break;
            case "Journal": await OpenJournalAsync(); break;
            default: ShowSection("Support", null); break;
        }
    }
    private async void MarkHandledButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRequest() is not { } request) return;
        if (TryGetDeviceRequestId(request, out int requestId))
        {
            if (_resolvingDeviceRequest) return;
            MessageBoxResult answer = System.Windows.MessageBox.Show(this,
                $"Одобрить регистрацию устройства «{request.MachineName}» для клиента {request.ClientName}?",
                "HonestLicenseServer", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            bool approved = false;
            _resolvingDeviceRequest = true;
            try
            {
                HonestLicenseAdminApiClient api = CreateApiClient();
                await api.ApproveDeviceRequestAsync(requestId, request.MachineName, request.Address, null, CancellationToken.None);
                approved = true;
                _logger.Info($"Device request approved through API: {requestId}");
                await IssueDeviceLicenseAsync(api, request);
            }
            catch (Exception ex)
            {
                _logger.Error(approved
                    ? $"Device request approved but license issue failed: {requestId}"
                    : $"Device request approval failed: {requestId}", ex);
                string message = approved
                    ? "Регистрация уже одобрена на HonestLicenseServer, но лицензию выдать не удалось. Повторите выдачу для устройства без лицензии.\n\n" + ex.Message
                    : ex.Message;
                System.Windows.MessageBox.Show(this, message, "Ошибка HonestLicenseServer", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (approved) await CheckNowAsync();
                _resolvingDeviceRequest = false;
            }
            return;
        }
        if (request.Uid.StartsWith("api-unlicensed-", StringComparison.Ordinal))
        {
            if (_resolvingDeviceRequest) return;
            _resolvingDeviceRequest = true;
            try
            {
                await IssueDeviceLicenseAsync(CreateApiClient(), request);
                await CheckNowAsync();
            }
            catch (Exception ex)
            {
                _logger.Error($"Device license issue failed: {request.DeviceId}", ex);
                System.Windows.MessageBox.Show(this, ex.Message, "Ошибка выдачи лицензии", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { _resolvingDeviceRequest = false; }
            return;
        }
        if (TryGetSupportRequestId(request, out int supportRequestId))
        {
            if (_resolvingDeviceRequest) return;
            _resolvingDeviceRequest = true;
            try
            {
                await CreateApiClient().ResolveSupportRequestAsync(supportRequestId, CancellationToken.None);
                _logger.Info($"Support request resolved through API: {supportRequestId}");
                await CheckNowAsync();
            }
            catch (Exception ex)
            {
                _logger.Error($"Support request resolve failed: {supportRequestId}", ex);
                System.Windows.MessageBox.Show(this, ex.Message, "Ошибка HonestLicenseServer", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { _resolvingDeviceRequest = false; }
            return;
        }
        var stored = _state.Requests.FirstOrDefault(x => x.Uid == request.Uid);
        if (stored is null) return;
        stored.IsHandled = true;
        _stateService.Save(_state);
        _logger.Info($"Request marked handled: {stored.Uid}");
        ReloadRequests();
    }

    private async Task IssueDeviceLicenseAsync(HonestLicenseAdminApiClient api, HelpRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.DeviceId))
            throw new InvalidDataException("В заявке отсутствуют ClientId или DeviceId.");
        var service = new ServerDeviceLicenseService(api);
        long revision;
        try
        {
            revision = await service.IssueAsync(request.ClientId, request.DeviceId, null, CancellationToken.None);
        }
        catch (LicensePolicyRequiredException)
        {
            var dialog = new DeviceLicensePolicyWindow { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Policy is null)
                throw new OperationCanceledException("Выдача лицензии отменена оператором.");
            revision = await service.IssueAsync(request.ClientId, request.DeviceId, dialog.Policy, CancellationToken.None);
        }
        _adminAccess.Audit($"Device approved and licensed; client={request.ClientId}; device={request.DeviceId}; revision={revision}");
        System.Windows.MessageBox.Show(this,
            $"Устройство зарегистрировано, лицензия revision {revision} подписана и опубликована.",
            "HonestDesk", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void RejectDeviceRequestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_resolvingDeviceRequest) return;
        if (SelectedRequest() is not { } request || !TryGetDeviceRequestId(request, out int requestId))
        {
            System.Windows.MessageBox.Show(this, "Выберите заявку регистрации устройства.", "HonestDesk", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new RejectDeviceRequestWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _resolvingDeviceRequest = true;
        try
        {
            await CreateApiClient().RejectDeviceRequestAsync(requestId, dialog.Reason, CancellationToken.None);
            _logger.Info($"Device request rejected through API: {requestId}");
            await CheckNowAsync();
        }
        catch (Exception ex)
        {
            _logger.Error($"Device request rejection failed: {requestId}", ex);
            System.Windows.MessageBox.Show(this, ex.Message, "Ошибка HonestLicenseServer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _resolvingDeviceRequest = false; }
    }

    private static bool TryGetDeviceRequestId(HelpRequest request, out int id)
    {
        id = 0;
        return request.Uid.StartsWith("api-device-", StringComparison.Ordinal) && int.TryParse(request.MessageId, out id);
    }

    private static bool TryGetSupportRequestId(HelpRequest request, out int id)
    {
        id = 0;
        return request.Uid.StartsWith("api-support-", StringComparison.Ordinal) && int.TryParse(request.MessageId, out id);
    }

    private void MailButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRequest() is not { } request) return;
        System.Windows.MessageBox.Show(this,
            $"Папка: {_settings.MailFolder}\nТема: {request.Subject}\nMessage-ID: {request.MessageId}",
            "Данные письма", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
