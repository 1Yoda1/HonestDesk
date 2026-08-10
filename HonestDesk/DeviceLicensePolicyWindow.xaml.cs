using System.Windows;
using HonestDesk.Infrastructure.Licensing;

namespace HonestDesk;

public partial class DeviceLicensePolicyWindow : Window
{
    public DeviceLicensePolicy? Policy { get; private set; }
    public DeviceLicensePolicyWindow() => InitializeComponent();

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (!Version.TryParse(VersionBox.Text.Trim(), out _) ||
            !int.TryParse(GraceBox.Text, out int grace) || grace < 0 ||
            !int.TryParse(DaysBox.Text, out int days) || days <= 0 ||
            (ViewCheck.IsChecked != true && InstallCheck.IsChecked != true))
        {
            System.Windows.MessageBox.Show(this, "Проверьте версию, сроки и выберите хотя бы одну функцию.", "HonestDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Policy = new(ViewCheck.IsChecked == true, InstallCheck.IsChecked == true, VersionBox.Text.Trim(), grace, days);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
