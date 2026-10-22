using System.Windows;
using HonestDesk.Infrastructure.Api;

namespace HonestDesk;

public partial class LicenseRevocationWindow : Window
{
    public AdminLicense? SelectedLicense { get; private set; }

    public LicenseRevocationWindow(string clientName, IReadOnlyList<AdminLicense> licenses)
    {
        InitializeComponent();
        DescriptionText.Text =
            $"Отозвать лицензию HonestFlow для {clientName}?\n\n" +
            "Выберите устройство. Доступ по выбранной лицензии будет прекращён. " +
            "Клиент, устройства и история останутся в системе.";
        LicensesGrid.ItemsSource = licenses;
        LicensesGrid.SelectedItem = licenses.FirstOrDefault();
    }

    private void Revoke_Click(object sender, RoutedEventArgs e)
    {
        if (LicensesGrid.SelectedItem is not AdminLicense selected) return;
        SelectedLicense = selected;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
