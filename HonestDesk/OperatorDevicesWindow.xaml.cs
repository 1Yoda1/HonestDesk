using System.Collections.ObjectModel;
using System.Windows;
using HonestDesk.Infrastructure;
using HonestDesk.Infrastructure.Api;
using HonestDesk.Infrastructure.Licensing;
using HonestDesk.Models;
using HonestDesk.Models.Licensing;
using HonestDesk.Services;

namespace HonestDesk;

public partial class OperatorDevicesWindow : Window
{
    private readonly ObservableCollection<OperatorDevice> _draftDevices;
    private readonly ObservableCollection<PublishedOperatorDevice> _publishedDevices = [];
    private readonly AdminAccessService _access;
    private readonly AppSettings _settings = new SettingsService().Load();
    private bool _busy;

    public IReadOnlyList<OperatorDevice> Devices => _draftDevices.ToList();

    public OperatorDevicesWindow(IEnumerable<OperatorDevice> devices, AdminAccessService access)
    {
        InitializeComponent();
        _access = access;
        _draftDevices = new ObservableCollection<OperatorDevice>((devices ?? []).Select(Clone));
        DraftDevicesGrid.ItemsSource = _draftDevices;
        PublishedDevicesGrid.ItemsSource = _publishedDevices;
        Loaded += async (_, _) => await LoadPublishedAsync();
    }

    private HonestLicenseAdminApiClient CreateApi() => new(
        _settings.ApiBaseUrl,
        () => DpapiProtector.Unprotect(_settings.ProtectedAdminApiKey));

    private async Task LoadPublishedAsync()
    {
        if (_busy) return;
        SetBusy(true, "Загрузка операторских лицензий с HonestLicenseServer…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            IReadOnlyList<PublishedOperatorDevice> items =
                await new OperatorDeviceAdministrationService(CreateApi()).GetAsync(cts.Token);
            _publishedDevices.Clear();
            foreach (PublishedOperatorDevice item in items) _publishedDevices.Add(item);
            PublishedStatusText.Text = items.Count == 0
                ? "Опубликованные операторские лицензии не найдены."
                : $"Загружено операторских лицензий: {items.Count}.";
            _access.Audit($"Operator devices loaded from server; grants={items.Count}");
        }
        catch (Exception ex)
        {
            PublishedStatusText.Text = "Не удалось загрузить операторские устройства: " + ex.Message;
            _access.Audit("Operator devices load failed");
        }
        finally { SetBusy(false); }
    }

    private async Task SetDeviceEnabledAsync(PublishedOperatorDevice item, bool enabled)
    {
        string action = enabled ? "Включить устройство" : "Отключить устройство";
        var confirmation = new DestructiveConfirmationWindow(
            action,
            $"{action} «{item.DeviceName}» для клиента {item.ClientName}?\n\nИстория устройства и лицензий сохранится.",
            action) { Owner = this };
        if (confirmation.ShowDialog() != true) return;
        await ExecuteMutationAsync(
            enabled ? "Включение устройства…" : "Отключение устройства…",
            (service, token) => service.SetDeviceEnabledAsync(item, enabled, token),
            $"Operator device status changed; license={item.LicenseId}; enabled={enabled}");
    }

    private async Task RevokeAsync(PublishedOperatorDevice item)
    {
        var confirmation = new DestructiveConfirmationWindow(
            "Отозвать лицензию",
            $"Отозвать операторскую лицензию для устройства «{item.DeviceName}»?\n" +
            $"Клиент: {item.ClientName}\n\nУстройство и история лицензий останутся в системе.",
            "Отозвать лицензию") { Owner = this };
        if (confirmation.ShowDialog() != true) return;
        await ExecuteMutationAsync(
            "Отзыв операторской лицензии…",
            (service, token) => service.RevokeAsync(item, token),
            $"Operator license revoked; license={item.LicenseId}");
    }

    private async Task ExecuteMutationAsync(
        string progress,
        Func<OperatorDeviceAdministrationService, CancellationToken, Task> action,
        string audit)
    {
        SetBusy(true, progress);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            await action(new OperatorDeviceAdministrationService(CreateApi()), cts.Token);
            _access.Audit(audit);
            SetBusy(false);
            await LoadPublishedAsync();
        }
        catch (Exception ex)
        {
            PublishedStatusText.Text = "Операция не выполнена: " + ex.Message;
            _access.Audit(audit + "; failed");
            SetBusy(false);
        }
    }

    private static OperatorDevice Clone(OperatorDevice value) => new()
    {
        DeviceId = value.DeviceId,
        Name = value.Name,
        Enabled = value.Enabled,
        Comment = value.Comment
    };

    private void AddDraftButton_Click(object sender, RoutedEventArgs e) =>
        _draftDevices.Add(new OperatorDevice { Enabled = true });

    private void RemoveDraftButton_Click(object sender, RoutedEventArgs e)
    {
        if (DraftDevicesGrid.SelectedItem is OperatorDevice device) _draftDevices.Remove(device);
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        DraftDevicesGrid.CommitEdit();
        if (_draftDevices.Any(x => string.IsNullOrWhiteSpace(x.DeviceId)) ||
            _draftDevices.GroupBy(x => x.DeviceId.Trim(), StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
        {
            PublishedStatusText.Text = "DeviceId обязателен и должен быть уникальным.";
            return;
        }

        foreach (var device in _draftDevices)
            device.DeviceId = device.DeviceId.Trim();

        DialogResult = true;
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        PublishedDevicesGrid.IsEnabled = !busy;
        if (status is not null) PublishedStatusText.Text = status;
        UpdateActions();
    }

    private void UpdateActions()
    {
        PublishedOperatorDevice? selected = PublishedDevicesGrid.SelectedItem as PublishedOperatorDevice;
        EnableDeviceButton.IsEnabled = !_busy && selected?.CanEnable == true;
        DisableDeviceButton.IsEnabled = !_busy && selected?.CanDisable == true;
        RevokeGrantButton.IsEnabled = !_busy && selected?.CanRevoke == true;
    }

    private async void ReloadButton_Click(object sender, RoutedEventArgs e) => await LoadPublishedAsync();
    private void PublishedDevicesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateActions();
    private async void EnableDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy && PublishedDevicesGrid.SelectedItem is PublishedOperatorDevice item) await SetDeviceEnabledAsync(item, true);
    }
    private async void DisableDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy && PublishedDevicesGrid.SelectedItem is PublishedOperatorDevice item) await SetDeviceEnabledAsync(item, false);
    }
    private async void RevokeGrantButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy && PublishedDevicesGrid.SelectedItem is PublishedOperatorDevice item) await RevokeAsync(item);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
