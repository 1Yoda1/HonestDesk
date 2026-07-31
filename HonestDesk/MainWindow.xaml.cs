using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HonestDesk.Models;
using HonestDesk.Services;
using Forms = System.Windows.Forms;

namespace HonestDesk;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService = new();
    private readonly StateService _stateService = new();
    private readonly AppLogger _logger = new();
    private readonly RuDesktopService _ruDesktopService;
    private readonly RemoteClientConfigService _remoteClientConfigService;
    private readonly MaxInboxImportService _maxInboxImportService;
    private readonly AdminAccessService _adminAccess = new();
    private readonly DispatcherTimer _timer = new();
    private readonly ObservableCollection<HelpRequest> _requests = [];
    private readonly Forms.NotifyIcon _notifyIcon = new();
    private AppSettings _settings;
    private AppState _state;
    private bool _checking;
    private bool _connecting;
    private bool _exitRequested;
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
        _maxInboxImportService = new MaxInboxImportService(_logger);
        _settings = _settingsService.Load();
        _state = _stateService.Load();
        RequestsGrid.ItemsSource = _requests;
        BuildTray();
        ConfigureTimer();
        Loaded += (_, _) => RestoreSelectedSection();
        ReloadRequests();
        _logger.Info("HonestDesk WPF started");
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (string.IsNullOrWhiteSpace(_settings.Email))
            ShowSettings();
        else
            await CheckNowAsync();
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
        var iconResource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Resources/hd.ico"))
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
        ResetTimer();
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
        SetStatus("Проверяем почту…", "#F3A847");

        try
        {
            var poller = new MailPoller(_logger);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = await poller.CheckAsync(_settings, _state, cts.Token);
            SetStatus(result.Status, "#48D39C");
            SideLastCheckText.Text = $"Последняя проверка: {DateTime.Now:HH:mm:ss}";

            foreach (var request in result.NewRequests)
            {
                _logger.Info($"New or repaired request: {request.ClientName}, {request.ProblemType}");
                ShowNotification(request);
            }

            foreach (var request in _maxInboxImportService.Import(_state))
            {
                _logger.Info($"New MAX request: {request.ClientName}, {request.ProblemType}");
                ShowNotification(request);
            }

            _stateService.Save(_state);
            ReloadRequests();
        }
        catch (Exception ex)
        {
            _logger.Error("Mail check failed", ex);
            SetStatus("Ошибка проверки", "#F06B78");
        }
        finally
        {
            _checking = false;
            CheckButton.IsEnabled = true;
        }
    }

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
            : $"Клиент: {request.ClientName}\nИНН: {request.InnMasked}\nАдрес: {request.Address}\n" +
              $"Компьютер: {request.MachineName}  ·  {request.WindowsUser}\nRuDesktop ID: {request.RuDesktopId}\n" +
              $"HonestFlow: {request.HonestFlowVersion}\nТип проблемы: {request.ProblemType}\n" +
              $"Получено: {request.CreatedAt:dd.MM.yyyy HH:mm:ss zzz}\n\n{request.Message}";
        DetailsText.Foreground = request is null
            ? (System.Windows.Media.Brush)FindResource("Muted")
            : (System.Windows.Media.Brush)FindResource("Ink");
    }

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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        ClientMatchResult match = await _remoteClientConfigService.FindClientAsync(
            request.ClientId, request.ClientName, request.InnMasked, cts.Token);
        if (!match.Success)
        {
            SetStatus("Клиент не найден", "#F06B78");
            ShowWarning(match.Error);
            return;
        }

        if (!string.Equals(request.ClientId, match.Client!.ClientId, StringComparison.Ordinal))
        {
            request.ClientId = match.Client.ClientId;
            _stateService.Save(_state);
        }

        SetStatus("Карточка клиента загружена", "#48D39C");
        var window = new ClientProfileWindow(match.Client, match.MatchSource, OpenAdminEditor) { Owner = this };
        window.ShowDialog();
    }

    private void OpenConfigEditorButton_Click(object sender, RoutedEventArgs e) => OpenAdminEditor();

    private void OpenAdminEditor() => _ = OpenAdminEditorAsync();

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
        _adminSessionUntilUtc = DateTimeOffset.UtcNow.AddMinutes(10);
        _adminEditor = null;
        _adminContent = null;
        _licenseManager = null;
        _licenseContent = null;
        return true;
    }

    private async void OpenLicensesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdminSession()) return;
        if (_adminEditor is null) await OpenAdminEditorAsync();
        _adminEditor?.RequestLicenses();
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

    private void OpenJournalButton_Click(object sender, RoutedEventArgs e)
    {
        string ReadLog(string path) => File.Exists(path) ? File.ReadAllText(path) : "Журнал пока пуст.";
        var text = new System.Windows.Controls.TextBox
        {
            Text = ReadLog(AppPaths.LogPath) + Environment.NewLine + ReadLog(AppPaths.AdminAuditPath),
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

    private void RestoreSelectedSection()
    {
        switch (_state.SelectedSection)
        {
            case "Clients": _ = OpenAdminEditorAsync(); break;
            case "Licenses": OpenLicensesButton_Click(this, new RoutedEventArgs()); break;
            case "Settings": ShowSettingsSection(); break;
            case "Journal": OpenJournalButton_Click(this, new RoutedEventArgs()); break;
            default: ShowSection("Support", null); break;
        }
    }
    private void MarkHandledButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRequest() is not { } request) return;
        var stored = _state.Requests.FirstOrDefault(x => x.Uid == request.Uid);
        if (stored is null) return;
        stored.IsHandled = true;
        _stateService.Save(_state);
        _logger.Info($"Request marked handled: {stored.Uid}");
        ReloadRequests();
    }

    private void MailButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRequest() is not { } request) return;
        System.Windows.MessageBox.Show(this,
            $"Папка: {_settings.MailFolder}\nТема: {request.Subject}\nMessage-ID: {request.MessageId}",
            "Данные письма", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
