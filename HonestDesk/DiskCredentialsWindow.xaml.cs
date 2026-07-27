using System.Windows;
using HonestFlowConfigEditor.Infrastructure;

namespace HonestDesk;

public partial class DiskCredentialsWindow : Window
{
    public DiskCredentialsWindow()
    {
        InitializeComponent();
        LoginBox.Text = YandexDiskSettings.GetCredentials().Login;
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
            PasswordBox.Clear();
            DialogResult = true;
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
