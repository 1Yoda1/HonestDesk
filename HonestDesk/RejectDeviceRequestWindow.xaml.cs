using System.Windows;

namespace HonestDesk;

public partial class RejectDeviceRequestWindow : Window
{
    public string Reason { get; private set; } = DeviceRequestRejectionReason.Fallback;

    public RejectDeviceRequestWindow() => InitializeComponent();

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        Reason = DeviceRequestRejectionReason.Normalize(ReasonBox.Text);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
