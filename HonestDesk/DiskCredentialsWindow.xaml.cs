using System.Windows;
using HonestDesk.Infrastructure;

namespace HonestDesk;

public partial class DiskCredentialsWindow : Window
{
    public DiskCredentialsWindow()
    {
        InitializeComponent();
        YandexDiskCredentials credentials = YandexDiskSettings.GetCredentials();
        LoginBox.Text = credentials.Login;
        PasswordBox.Password = credentials.AppPassword;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            YandexDiskSettings.SaveCredentials(new YandexDiskCredentials
            {
                Login = LoginBox.Text,
                AppPassword = PasswordBox.Password
            });
            ErrorText.Text = "";
            DialogResult = true;
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
