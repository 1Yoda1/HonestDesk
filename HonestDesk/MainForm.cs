using HonestDesk.Models;
using HonestDesk.Services;

namespace HonestDesk;

public sealed class MainForm : Form
{
    private readonly SettingsService _settingsService = new();
    private readonly StateService _stateService = new();
    private readonly AppLogger _logger = new();
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
    private HelpRequest? _lastNotifiedRequest;

    public MainForm()
    {
        _settings = _settingsService.Load();
        _state = _stateService.Load();

        Text = "HonestDesk";
        MinimumSize = new Size(980, 620);
        StartPosition = FormStartPosition.CenterScreen;
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
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 42));

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var check = new Button { Text = "Проверить сейчас", Width = 140 };
        var settings = new Button { Text = "Настройки", Width = 110 };
        check.Click += async (_, _) => await CheckNowAsync();
        settings.Click += (_, _) => ShowSettings();
        toolbar.Controls.Add(check);
        toolbar.Controls.Add(settings);
        toolbar.Controls.Add(_status);
        toolbar.Controls.Add(_lastCheck);

        ConfigureGrid();
        root.Controls.Add(toolbar, 0, 0);
        root.Controls.Add(_grid, 0, 1);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        bottom.Controls.Add(_details, 0, 0);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(8), WrapContents = false };
        AddAction(actions, "Подключиться", ConnectSelected);
        AddAction(actions, "Скопировать ID", CopySelectedId);
        AddAction(actions, "Открыть RuDesktop", OpenRuDesktop);
        AddAction(actions, "Пометить обработанной", MarkHandled);
        AddAction(actions, "Открыть письмо", OpenMailInfo);
        bottom.Controls.Add(actions, 1, 0);

        root.Controls.Add(bottom, 0, 2);
        Controls.Add(root);
    }

    private static void AddAction(FlowLayoutPanel panel, string text, Action action)
    {
        var button = new Button { Text = text, Width = 165, Height = 34, Margin = new Padding(0, 0, 0, 8) };
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

    private void ConnectSelected()
    {
        var request = SelectedRequest();
        if (request is null)
        {
            return;
        }

        var service = new RuDesktopService(_logger);
        if (!service.ConnectOrCopy(_settings.RuDesktopPath, request.RuDesktopId))
        {
            MessageBox.Show(this, "RuDesktop.exe не найден. Укажите путь в настройках.", "HonestDesk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        var service = new RuDesktopService(_logger);
        if (!service.Open(_settings.RuDesktopPath))
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



