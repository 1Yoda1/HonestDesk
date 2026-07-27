using System.Collections.ObjectModel;
using System.Windows;
using HonestFlowConfigEditor.Models.Licensing;

namespace HonestDesk;

public partial class OperatorDevicesWindow : Window
{
    private readonly ObservableCollection<OperatorDevice> _devices;
    public IReadOnlyList<OperatorDevice> Devices => _devices.ToList();

    public OperatorDevicesWindow(IEnumerable<OperatorDevice> devices)
    {
        InitializeComponent();
        _devices = new ObservableCollection<OperatorDevice>((devices ?? []).Select(Clone));
        DevicesGrid.ItemsSource = _devices;
    }

    private static OperatorDevice Clone(OperatorDevice value) => new()
    {
        DeviceId = value.DeviceId,
        Name = value.Name,
        Enabled = value.Enabled,
        Comment = value.Comment
    };

    private void AddButton_Click(object sender, RoutedEventArgs e) =>
        _devices.Add(new OperatorDevice { Enabled = true });

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesGrid.SelectedItem is OperatorDevice device) _devices.Remove(device);
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        DevicesGrid.CommitEdit();
        if (_devices.Any(x => string.IsNullOrWhiteSpace(x.DeviceId)) ||
            _devices.GroupBy(x => x.DeviceId.Trim(), StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
        {
            System.Windows.MessageBox.Show(this, "DeviceId обязателен и должен быть уникальным.",
                "Проверьте устройства", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        foreach (var device in _devices) device.DeviceId = device.DeviceId.Trim();
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
