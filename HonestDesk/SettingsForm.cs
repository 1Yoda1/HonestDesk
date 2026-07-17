using HonestDesk.Models;
using HonestDesk.Services;

namespace HonestDesk;

public sealed class SettingsForm : Form
{
    private readonly TextBox _server = new();
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535 };
    private readonly CheckBox _ssl = new() { Text = "SSL" };
    private readonly TextBox _email = new();
    private readonly TextBox _password = new() { UseSystemPasswordChar = true };
    private readonly TextBox _folder = new();
    private readonly NumericUpDown _interval = new() { Minimum = 10, Maximum = 3600, Increment = 5 };
    private readonly TextBox _ruDesktopPath = new();
    private readonly CheckBox _startup = new() { Text = "Запускать вместе с Windows" };

    public AppSettings Settings { get; private set; }

    public SettingsForm(AppSettings settings)
    {
        Settings = settings;
        Text = "Настройки HonestDesk";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(560, 420);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        BuildLayout();
        LoadSettings(settings);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 3,
            RowCount = 10
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));

        AddRow(root, 0, "IMAP сервер", _server);
        AddRow(root, 1, "Порт", _port);
        root.Controls.Add(new Label { Text = "Шифрование", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        root.Controls.Add(_ssl, 1, 2);
        AddRow(root, 3, "Email", _email);
        AddRow(root, 4, "Пароль приложения", _password);
        AddRow(root, 5, "Папка заявок", _folder);
        AddRow(root, 6, "Интервал, сек.", _interval);
        AddRow(root, 7, "RuDesktop.exe", _ruDesktopPath);

        var browse = new Button { Text = "Обзор", Dock = DockStyle.Fill };
        browse.Click += (_, _) => BrowseRuDesktop();
        root.Controls.Add(browse, 2, 7);

        root.Controls.Add(_startup, 1, 8);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = "Сохранить", DialogResult = DialogResult.OK, Width = 110 };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Width = 90 };
        save.Click += (_, _) => SaveSettings();
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons, 0, 9);
        root.SetColumnSpan(buttons, 3);

        Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private static void AddRow(TableLayoutPanel root, int row, string label, Control control)
    {
        control.Dock = DockStyle.Fill;
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        root.Controls.Add(control, 1, row);
        root.SetColumnSpan(control, 2);
    }

    private void LoadSettings(AppSettings settings)
    {
        _server.Text = settings.ImapServer;
        _port.Value = settings.ImapPort;
        _ssl.Checked = settings.UseSsl;
        _email.Text = settings.Email;
        _password.Text = DpapiProtector.Unprotect(settings.ProtectedAppPassword);
        _folder.Text = settings.MailFolder;
        _interval.Value = Math.Clamp(settings.CheckIntervalSeconds, 10, 3600);
        _ruDesktopPath.Text = settings.RuDesktopPath;
        _startup.Checked = settings.StartWithWindows;
    }

    private void SaveSettings()
    {
        Settings = new AppSettings
        {
            ImapServer = _server.Text.Trim(),
            ImapPort = (int)_port.Value,
            UseSsl = _ssl.Checked,
            Email = _email.Text.Trim(),
            ProtectedAppPassword = DpapiProtector.Protect(_password.Text),
            MailFolder = _folder.Text.Trim(),
            CheckIntervalSeconds = (int)_interval.Value,
            RuDesktopPath = _ruDesktopPath.Text.Trim(),
            StartWithWindows = _startup.Checked
        };
    }

    private void BrowseRuDesktop()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Выберите RuDesktop.exe",
            Filter = "RuDesktop|rudesktop.exe|EXE файлы|*.exe|Все файлы|*.*"
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _ruDesktopPath.Text = dialog.FileName;
        }
    }
}
