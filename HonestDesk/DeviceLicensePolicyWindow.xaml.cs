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

        bool serviceEnabled = ServiceCheck.IsChecked == true;
        DateTimeOffset? serviceValidUntilUtc = serviceEnabled && ServiceDatePicker.SelectedDate is { } selectedDate
            ? new DateTimeOffset(DateTime.SpecifyKind(selectedDate.Date.AddDays(1).AddTicks(-1), DateTimeKind.Utc))
            : null;
        if (serviceEnabled && serviceValidUntilUtc <= DateTimeOffset.UtcNow)
        {
            System.Windows.MessageBox.Show(this, "Для HonestFlow Service укажите будущую дату окончания доступа.", "HonestDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Policy = new(
            ViewCheck.IsChecked == true,
            InstallCheck.IsChecked == true,
            VersionBox.Text.Trim(),
            grace,
            days,
            serviceEnabled,
            serviceValidUntilUtc);
        DialogResult = true;
    }

    private void ServiceCheck_Changed(object sender, RoutedEventArgs e) =>
        ServiceDatePicker.IsEnabled = ServiceCheck.IsChecked == true;

    private void ServiceOneMonth_Click(object sender, RoutedEventArgs e) =>
        ServiceDatePicker.SelectedDate = DateTime.Today.AddMonths(1);

    private void ServiceThreeMonths_Click(object sender, RoutedEventArgs e) =>
        ServiceDatePicker.SelectedDate = DateTime.Today.AddMonths(3);

    private void ServiceTwelveMonths_Click(object sender, RoutedEventArgs e) =>
        ServiceDatePicker.SelectedDate = DateTime.Today.AddMonths(12);

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}