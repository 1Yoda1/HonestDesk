using HonestDesk.Models;
using HonestDesk.Services;

namespace HonestDesk;

public sealed class MainForm : Form
{
    private readonly SettingsService _settingsService = new();
    private readonly StateService _stateService = new();
    private readonly AppLogger _logger = new();
    private readonly RuDesktopService _ruDesktopService;
    private readonly RemoteClientConfigService _remoteClientConfigService;
    private readonly MaxInboxImportService _maxInboxImportService;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly BindingSource _bindingSource = new();
    private readonly DataGridView _grid = new();
    private readonly Label _status = new() { AutoSize = true };
    private readonly Label _lastCheck = new() { AutoSize = true };
    private readonly TextBox _details = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly NotifyIcon _notifyIcon = new();
    private AppSettings _settings;
    private AppState _state;
    private bool _checking;
    private bool _connecting;
    private HelpRequest? _lastNotifiedRequest;

    public MainForm()
    {
        _ruDesktopService = new RuDesktopService(_logger);
        _remoteClientConfigService = new RemoteClientConfigService(_logger);
        _maxInboxImportService = new MaxInboxImportService(_logger);
        _settings = _settingsService.Load();
        _state = _stateService.Load();

        Text = "HonestDesk";
        MinimumSize = new Size(980, 620);
        ClientSize = new Size(1280, 760);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = AppTheme.Canvas;
        ForeColor = AppTheme.Text;
        Font = AppTheme.UiFont();
        BuildLayout();
        BuildTray();
        ConfigureTimer();
        ReloadGrid();

        _logger.Info("HonestDesk started");
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (string.IsNullOrWhiteSpace(_settings.Email))
        {
            ShowSettings();
        }
        else
        {
            HideToTray();
            await CheckNowAsync();
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized)
        {
            HideToTray();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _notifyIcon.Visible = false;
        base.OnFormClosing(e);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(20), BackColor = AppTheme.Canvas };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 42));

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = AppTheme.Surface, Padding = new Padding(12, 10, 12, 8), BorderStyle = BorderStyle.FixedSingle };
        var check = new Button { Text = "Проверить сейчас", Width = 156, Height = 40, Margin = new Padding(0, 0, 10, 0) };
        var settings = new Button { Text = "Настройки", Width = 118, Height = 40, Margin = new Padding(0, 0, 20, 0) };
        AppTheme.StylePrimaryButton(check);
        AppTheme.StyleSecondaryButton(settings);
        _status.ForeColor = AppTheme.Success;
        _status.Font = AppTheme.UiFont(9.5f, FontStyle.Bold);
        _status.Margin = new Padding(0, 11, 18, 0);
        _lastCheck.ForeColor = AppTheme.Muted;
        _lastCheck.Margin = new Padding(0, 11, 0, 0);
        check.Click += async (_, _) => await CheckNowAsync();
        settings.Click += (_, _) => ShowSettings();
        toolbar.Controls.Add(check);
        toolbar.Controls.Add(settings);
        toolbar.Controls.Add(_status);
        toolbar.Controls.Add(_lastCheck);

        ConfigureGrid();
        root.Controls.Add(toolbar, 0, 0);
        root.Controls.Add(_grid, 0, 1);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(0, 14, 0, 0), BackColor = AppTheme.Canvas };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        bottom.Controls.Add(_details, 0, 0);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12), WrapContents = false, BackColor = AppTheme.Surface, BorderStyle = BorderStyle.FixedSingle };
        AddAction(actions, "Подключиться", ConnectSelected, true);
        AddAction(actions, "Скопировать ID", CopySelectedId);
        AddAction(actions, "Открыть RuDesktop", OpenRuDesktop);
        AddAction(actions, "Пометить обработанной", MarkHandled);
        AddAction(actions, "Открыть письмо", OpenMailInfo);
        bottom.Controls.Add(actions, 1, 0);

        root.Controls.Add(bottom, 0, 2);
        Controls.Add(root);
    }

    private static void AddAction(FlowLayoutPanel panel, string text, Action action, bool primary = false)
    {
        var button = new Button { Text = text, Width = 176, Height = 38, Margin = new Padding(0, 0, 0, 9) };
        if (primary) AppTheme.StylePrimaryButton(button); else AppTheme.StyleSecondaryButton(button);
        button.Click += (_, _) => action();
        panel.Controls.Add(button);
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.AutoGenerateColumns = false;
        _grid.BackgroundColor = AppTheme.Surface;
        _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.GridColor = AppTheme.Border;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _grid.ColumnHeadersHeight = 42;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = AppTheme.Primary;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        _grid.ColumnHeadersDefaultCellStyle.Font = AppTheme.UiFont(9.5f, FontStyle.Bold);
        _grid.RowHeadersVisible = false;
        _grid.RowTemplate.Height = 38;
        _grid.DefaultCellStyle.BackColor = AppTheme.Surface;
        _grid.DefaultCellStyle.ForeColor = AppTheme.Text;
        _grid.DefaultCellStyle.SelectionBackColor = AppTheme.SurfaceAlt;
        _grid.DefaultCellStyle.SelectionForeColor = AppTheme.Primary;
        _grid.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(249, 251, 255);
        _grid.DataSource = _bindingSource;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Время", DataPropertyName = nameof(HelpRequest.CreatedAt), Width = 150 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Клиент", DataPropertyName = nameof(HelpRequest.ClientName), Width = 190 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ИНН", DataPropertyName = nameof(HelpRequest.InnMasked), Width = 110 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Адрес", DataPropertyName = nameof(HelpRequest.Address), Width = 190 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Компьютер", DataPropertyName = nameof(HelpRequest.MachineName), Width = 140 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "RuDesktop ID", DataPropertyName = nameof(HelpRequest.RuDesktopId), Width = 130 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Проблема", DataPropertyName = nameof(HelpRequest.ProblemType), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Обработана", DataPropertyName = nameof(HelpRequest.IsHandled), Width = 90 });
        _grid.SelectionChanged += (_, _) => ShowSelectedDetails();
        _grid.CellDoubleClick += (_, _) => ConnectSelected();
        _details.BackColor = AppTheme.Surface;
        _details.ForeColor = AppTheme.Text;
        _details.BorderStyle = BorderStyle.FixedSingle;
        _details.Font = AppTheme.UiFont(10);
        _details.Padding = new Padding(10);
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => ShowFromTray());
        menu.Items.Add("Проверить сейчас", null, async (_, _) => await CheckNowAsync());
        menu.Items.Add("Настройки", null, (_, _) => ShowSettings());
        menu.Items.Add("Выход", null, (_, _) => ExitApplication());

        _notifyIcon.Icon = SystemIcons.Application;
        _notifyIcon.Text = "HonestDesk";
        _notifyIcon.Visible = true;
        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) => ShowFromTray();
        _notifyIcon.BalloonTipClicked += (_, _) => OpenRequest(_lastNotifiedRequest);
    }

    private void ConfigureTimer()
    {
        _timer.Tick += async (_, _) => await CheckNowAsync();
        ResetTimer();
    }

    private void ResetTimer()
    {
        _timer.Stop();
        _timer.Interval = Math.Max(10, _settings.CheckIntervalSeconds) * 1000;
        _timer.Start();
    }

    private async Task CheckNowAsync()
    {
        if (_checking)
        {
            return;
        }

        _checking = true;
        _status.Text = "Статус: проверка почты...";

        try
        {
            var poller = new MailPoller(_logger);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = await poller.CheckAsync(_settings, _state, cts.Token);
            _status.Text = "Статус: " + result.Status;
            _lastCheck.Text = "Последняя проверка: " + DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss");

            if (result.NewRequests.Count > 0)
            {
                foreach (var request in result.NewRequests)
                {
                    _logger.Info($"New or repaired request: {request.ClientName}, {request.ProblemType}");
                    ShowNotification(request);
                }
            }

            var maxRequests = _maxInboxImportService.Import(_state);
            foreach (var request in maxRequests)
            {
                _logger.Info($"New MAX request: {request.ClientName}, {request.ProblemType}");
                ShowNotification(request);
            }

            _stateService.Save(_state);
            ReloadGrid();
        }
        finally
        {
            _checking = false;
        }
    }

    private void ShowNotification(HelpRequest request)
    {
        _lastNotifiedRequest = request;
        var title = string.IsNullOrWhiteSpace(request.ClientName) ? "Новая заявка HonestFlow" : request.ClientName;
        var text = string.IsNullOrWhiteSpace(request.ProblemType) ? "Запрос помощи" : request.ProblemType;
        _notifyIcon.ShowBalloonTip(10000, title, text, ToolTipIcon.Info);
    }

    private void ReloadGrid()
    {
        _bindingSource.DataSource = _state.Requests.OrderByDescending(x => x.CreatedAt).ToList();
        ShowSelectedDetails();
    }

    private HelpRequest? SelectedRequest()
    {
        return _bindingSource.Current as HelpRequest;
    }

    private void ShowSelectedDetails()
    {
        var request = SelectedRequest();
        if (request is null)
        {
            _details.Text = "";
            return;
        }

        _details.Text = $"Клиент: {request.ClientName}{Environment.NewLine}" +
                        $"ИНН: {request.InnMasked}{Environment.NewLine}" +
                        $"Адрес: {request.Address}{Environment.NewLine}" +
                        $"Компьютер: {request.MachineName}{Environment.NewLine}" +
                        $"Пользователь Windows: {request.WindowsUser}{Environment.NewLine}" +
                        $"RuDesktop ID: {request.RuDesktopId}{Environment.NewLine}" +
                        $"Версия HonestFlow: {request.HonestFlowVersion}{Environment.NewLine}" +
                        $"Тип проблемы: {request.ProblemType}{Environment.NewLine}" +
                        $"Время заявки: {request.CreatedAt:dd.MM.yyyy HH:mm:ss zzz}{Environment.NewLine}" +
                        $"Тема письма: {request.Subject}{Environment.NewLine}" +
                        $"Message-ID: {request.MessageId}{Environment.NewLine}{Environment.NewLine}" +
                        request.Message;
    }

    private async void ConnectSelected()
    {
        if (_connecting)
            return;

        var request = SelectedRequest();
        if (request is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.RuDesktopId))
        {
            MessageBox.Show(this, "В заявке отсутствует RuDesktop ID.", "HonestDesk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _connecting = true;
        try
        {
            _status.Text = "Статус: получение настроек RuDesktop...";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            ClientPasswordResult passwordResult = await _remoteClientConfigService.GetRuDesktopPasswordAsync(
                request.ClientName,
                cts.Token);
            if (!passwordResult.Success)
            {
                _status.Text = "Статус: подключение не выполнено";
                MessageBox.Show(this, passwordResult.ErrorMessage, "HonestDesk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            RuDesktopConnectResult result = _ruDesktopService.Connect(
                _settings.RuDesktopPath,
                request.RuDesktopId,
                passwordResult.Password);
            _status.Text = result == RuDesktopConnectResult.Started
                ? "Статус: подключение RuDesktop запущено"
                : "Статус: не удалось запустить подключение";

            string? error = result switch
            {
                RuDesktopConnectResult.ExecutableNotFound => "RuDesktop.exe не найден. Укажите путь в настройках.",
                RuDesktopConnectResult.InvalidPeerId => "В заявке указан некорректный RuDesktop ID.",
                RuDesktopConnectResult.PasswordMissing => "Для клиента не задан пароль RuDesktop.",
                RuDesktopConnectResult.StartFailed => "Не удалось запустить подключение RuDesktop. Подробности записаны в журнал.",
                _ => null
            };
            if (error is not null)
                MessageBox.Show(this, error, "HonestDesk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _connecting = false;
        }
    }

    private void CopySelectedId()
    {
        var request = SelectedRequest();
        if (request is null || string.IsNullOrWhiteSpace(request.RuDesktopId))
        {
            return;
        }

        Clipboard.SetText(request.RuDesktopId);
        _logger.Info($"RuDesktop ID copied manually: {request.RuDesktopId}");
    }

    private void OpenRuDesktop()
    {
        if (!_ruDesktopService.Open(_settings.RuDesktopPath))
        {
            MessageBox.Show(this, "RuDesktop.exe не найден. Укажите путь в настройках.", "HonestDesk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void MarkHandled()
    {
        var request = SelectedRequest();
        if (request is null)
        {
            return;
        }

        var stored = _state.Requests.FirstOrDefault(x => x.Uid == request.Uid);
        if (stored is not null)
        {
            stored.IsHandled = true;
            _stateService.Save(_state);
            _logger.Info($"Request marked handled: {stored.Uid}");
            ReloadGrid();
        }
    }

    private void OpenMailInfo()
    {
        var request = SelectedRequest();
        if (request is null)
        {
            return;
        }

        MessageBox.Show(this,
            $"Открытие конкретного письма через IMAP URL в MVP не реализовано.{Environment.NewLine}{Environment.NewLine}Папка: {_settings.MailFolder}{Environment.NewLine}Тема: {request.Subject}{Environment.NewLine}Message-ID: {request.MessageId}",
            "Письмо", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ShowSettings()
    {
        using var form = new SettingsForm(_settings);
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            _settings = form.Settings;
            _settingsService.Save(_settings);
            ResetTimer();
            _logger.Info("Settings saved");
        }
    }

    private void OpenRequest(HelpRequest? request)
    {
        ShowFromTray();
        if (request is null)
        {
            return;
        }

        for (var i = 0; i < _bindingSource.Count; i++)
        {
            if (_bindingSource[i] is HelpRequest item && item.Uid == request.Uid)
            {
                _bindingSource.Position = i;
                break;
            }
        }
    }

    private void HideToTray()
    {
        Hide();
        WindowState = FormWindowState.Minimized;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _notifyIcon.Visible = false;
        _timer.Stop();
        Application.Exit();
    }
}
