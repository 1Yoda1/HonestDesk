using System.Windows;
using System.Windows.Input;
using HonestDesk.Services;

namespace HonestDesk;

public partial class AdminAccessWindow : Window
{
    private readonly AdminAccessService _service;
    private readonly bool _setup;

    public AdminAccessWindow(AdminAccessService service)
    {
        InitializeComponent();
        _service = service;
        _setup = !service.IsConfigured;
        ConfirmationPanel.Visibility = _setup ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Text = _setup ? "Настройка администратора" : "Административный доступ";
        SubtitleText.Text = _setup ? "Создайте пароль для защищённого модуля" : "Разблокируйте редактор конфигурации";
        UnlockButton.Content = _setup ? "Создать доступ" : "Разблокировать";
        HintText.Text = _setup
            ? "Первичную настройку выполняет администратор Windows. Минимум 10 символов; хэш защищается DPAPI."
            : "После пяти ошибок доступ блокируется на 15 минут. Сессия автоматически закроется через 10 минут.";
        Loaded += (_, _) => PasswordBox.Focus();
    }

    private void Unlock()
    {
        ErrorText.Text = "";
        if (_setup && PasswordBox.Password != ConfirmPasswordBox.Password)
        {
            ErrorText.Text = "Пароли не совпадают.";
            return;
        }

        AdminAccessResult result = _setup
            ? _service.Configure(PasswordBox.Password)
            : _service.Verify(PasswordBox.Password);
        PasswordBox.Clear();
        ConfirmPasswordBox.Clear();
        if (!result.Success)
        {
            ErrorText.Text = result.Error;
            return;
        }
        DialogResult = true;
    }

    private void UnlockButton_Click(object sender, RoutedEventArgs e) => Unlock();
    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void PasswordBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Enter) Unlock(); }
}
