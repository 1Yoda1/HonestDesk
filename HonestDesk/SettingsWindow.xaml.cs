using System.Windows;
using HonestDesk.Models;
using HonestDesk.Services;
using Microsoft.Win32;

namespace HonestDesk;

public partial class SettingsWindow : Window
{
    public AppSettings Settings { get; private set; }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Settings = settings;
        ServerBox.Text = settings.ImapServer;
        PortBox.Text = settings.ImapPort.ToString();
        SslCheck.IsChecked = settings.UseSsl;
        EmailBox.Text = settings.Email;
        PasswordBox.Password = DpapiProtector.Unprotect(settings.ProtectedAppPassword);
        FolderBox.Text = settings.MailFolder;
        RegistrationEmailBox.Text = settings.RegistrationEmail;
        RegistrationPasswordBox.Password = DpapiProtector.Unprotect(settings.ProtectedRegistrationAppPassword);
        IntervalBox.Text = settings.CheckIntervalSeconds.ToString();
        RuDesktopBox.Text = settings.RuDesktopPath;
        StartupCheck.IsChecked = settings.StartWithWindows;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535)
        {
            ShowValidation("Порт должен быть числом от 1 до 65535.");
            return;
        }
        if (!int.TryParse(IntervalBox.Text, out var interval) || interval is < 10 or > 3600)
        {
            ShowValidation("Интервал должен быть числом от 10 до 3600 секунд.");
            return;
        }

        string protectedSupportPassword = DpapiProtector.Protect(PasswordBox.Password);
        string protectedRegistrationPassword = DpapiProtector.Protect(RegistrationPasswordBox.Password);
        if (DpapiProtector.Unprotect(protectedSupportPassword) != PasswordBox.Password ||
            DpapiProtector.Unprotect(protectedRegistrationPassword) != RegistrationPasswordBox.Password)
        {
            ShowValidation(
                "Не удалось защитить и проверить пароли. " +
                "Настройки не сохранены. Запустите HonestDesk под тем же пользователем Windows.");
            return;
        }

        Settings = new AppSettings
        {
            ImapServer = ServerBox.Text.Trim(),
            ImapPort = port,
            UseSsl = SslCheck.IsChecked == true,
            Email = EmailBox.Text.Trim(),
            ProtectedAppPassword = protectedSupportPassword,
            MailFolder = FolderBox.Text.Trim(),
            RegistrationEmail = RegistrationEmailBox.Text.Trim(),
            ProtectedRegistrationAppPassword = protectedRegistrationPassword,
            CheckIntervalSeconds = interval,
            RuDesktopPath = RuDesktopBox.Text.Trim(),
            StartWithWindows = StartupCheck.IsChecked == true
        };
        DialogResult = true;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите RuDesktop.exe",
            Filter = "RuDesktop|rudesktop.exe|EXE-файлы|*.exe|Все файлы|*.*"
        };
        if (dialog.ShowDialog(this) == true)
            RuDesktopBox.Text = dialog.FileName;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowValidation(string message) =>
        System.Windows.MessageBox.Show(this, message, "Проверьте настройки", MessageBoxButton.OK, MessageBoxImage.Warning);
}
