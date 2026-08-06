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
        if (!_setup)
            HintText.Text = "\u041f\u043e\u0441\u043b\u0435 \u043f\u044f\u0442\u0438 \u043e\u0448\u0438\u0431\u043e\u043a \u0434\u043e\u0441\u0442\u0443\u043f \u0431\u043b\u043e\u043a\u0438\u0440\u0443\u0435\u0442\u0441\u044f \u043d\u0430 15 \u043c\u0438\u043d\u0443\u0442. \u0414\u043e\u0441\u0442\u0443\u043f \u0434\u0435\u0439\u0441\u0442\u0432\u0443\u0435\u0442 \u0434\u043e \u0437\u0430\u043a\u0440\u044b\u0442\u0438\u044f \u043f\u0440\u0438\u043b\u043e\u0436\u0435\u043d\u0438\u044f.";
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
