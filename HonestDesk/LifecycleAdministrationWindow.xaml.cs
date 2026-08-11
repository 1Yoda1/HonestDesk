using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using HonestDesk.Infrastructure.Api;
using HonestDesk.Models;
using HonestDesk.Services;

namespace HonestDesk;

public partial class LifecycleAdministrationWindow : Window
{
    private readonly AdminAccessService _access;
    private readonly AppSettings _settings = new SettingsService().Load();
    private readonly ObservableCollection<AdminClient> _clients = [];
    private readonly ObservableCollection<AdminDevice> _devices = [];
    private readonly ICollectionView _clientsView;
    private readonly ICollectionView _devicesView;
    private IReadOnlyList<AdminLicense> _licenses = [];
    private IReadOnlyList<AdminDeviceRequest> _requests = [];
    private bool _busy;

    public LifecycleAdministrationWindow(AdminAccessService access)
    {
        InitializeComponent();
        _access = access;
        _clientsView = CollectionViewSource.GetDefaultView(_clients);
        _devicesView = CollectionViewSource.GetDefaultView(_devices);
        _clientsView.Filter = value => value is AdminClient client &&
            AdminLifecycleVisibility.ClientIsVisible(client, ShowInactiveClientsCheck.IsChecked == true);
        _devicesView.Filter = value => value is AdminDevice device &&
            AdminLifecycleVisibility.DeviceIsVisible(device, ShowDeletedDevicesCheck.IsChecked == true);
        ClientsGrid.ItemsSource = _clientsView;
        DevicesGrid.ItemsSource = _devicesView;
        Loaded += async (_, _) => await LoadAsync();
    }

    private HonestLicenseAdminApiClient CreateApi() => new(_settings.ApiBaseUrl,
        () => DpapiProtector.Unprotect(_settings.ProtectedAdminApiKey));

    private async Task LoadAsync()
    {
        if (_busy) return;
        SetBusy(true, "Загрузка данных с HonestLicenseServer…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            HonestLicenseAdminApiClient api = CreateApi();
            Task<IReadOnlyList<AdminClient>> clientsTask = api.GetClientsAsync(cts.Token, includeInactive: true);
            Task<IReadOnlyList<AdminDevice>> devicesTask = api.GetDevicesAsync(null, cts.Token, includeDeleted: true);
            Task<IReadOnlyList<AdminLicense>> licensesTask = api.GetLicensesAsync(null, cts.Token);
            Task<IReadOnlyList<AdminDeviceRequest>> requestsTask = api.GetAllDeviceRequestsAsync(cts.Token);
            await Task.WhenAll(clientsTask, devicesTask, licensesTask, requestsTask);
            _clients.Clear();
            foreach (AdminClient client in (await clientsTask).OrderBy(x => x.Name)) _clients.Add(client);
            _devices.Clear();
            foreach (AdminDevice device in (await devicesTask).OrderBy(x => x.ClientName).ThenBy(x => x.Name)) _devices.Add(device);
            _licenses = await licensesTask;
            _requests = await requestsTask;
            _clientsView.Refresh();
            _devicesView.Refresh();
            StatusText.Text = $"Загружено клиентов: {_clients.Count}; устройств: {_devices.Count}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка загрузки: " + ex.Message;
            _access.Audit("Lifecycle administration load failed");
        }
        finally { SetBusy(false); }
    }

    private async Task RestoreDeviceAsync(AdminDevice device)
    {
        if (!IsDeletedBound(device))
        {
            StatusText.Text = device.DeviceIdReleased
                ? "DeviceId уже освобождён; эту историческую запись нельзя восстановить."
                : "Восстановить можно только устройство со статусом Deleted.";
            return;
        }
        if (System.Windows.MessageBox.Show(this, DeviceConfirmation(device,
                "Устройство станет Active. Лицензии автоматически не восстанавливаются."),
                "Восстановить устройство", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await ExecuteAsync("Восстановление устройства…",
            (api, token) => api.RestoreDeviceAsync(device.Id, token),
            $"Device restored; id={device.Id}");
    }

    private async Task ReleaseDeviceAsync(AdminDevice device, bool forTransfer)
    {
        if (!IsDeletedBound(device))
        {
            StatusText.Text = device.DeviceIdReleased
                ? "DeviceId уже освобождён."
                : "Освободить DeviceId можно только у устройства со статусом Deleted.";
            return;
        }
        string consequence = forTransfer
            ? "DeviceId станет доступен другому клиенту через обычную регистрацию. История останется у исходного клиента."
            : "DeviceId станет свободным. Историческая запись, лицензии и аудит сохранятся.";
        if (System.Windows.MessageBox.Show(this, DeviceConfirmation(device, consequence),
                forTransfer ? "Освободить для переноса" : "Освободить DeviceId",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await ExecuteAsync("Освобождение DeviceId…",
            (api, token) => api.ReleaseDeviceIdAsync(device.Id, token),
            $"DeviceId released; id={device.Id}; transfer={forTransfer}");
    }

    private async Task ExecuteAsync(string progress,
        Func<HonestLicenseAdminApiClient, CancellationToken, Task> action, string audit)
    {
        SetBusy(true, progress);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            HonestLicenseAdminApiClient api = CreateApi();
            await action(api, cts.Token);
            _access.Audit(audit);
            SetBusy(false);
            await LoadAsync();
        }
        catch (HonestLicenseAdminApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            StatusText.Text = "Операция отклонена сервером из-за конфликта DeviceId: " + ex.ResponseBody;
            _access.Audit(audit + "; conflict");
            SetBusy(false);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Операция не выполнена: " + ex.Message;
            _access.Audit(audit + "; failed");
            SetBusy(false);
        }
    }

    private static bool IsDeletedBound(AdminDevice device) =>
        string.Equals(device.Status, "Deleted", StringComparison.OrdinalIgnoreCase) && !device.DeviceIdReleased;

    private static string DeviceConfirmation(AdminDevice device, string consequence) =>
        $"Устройство: {device.Name}\nКлиент: {device.ClientName} ({device.ClientId})\nDeviceId: {device.DeviceId}\n\n{consequence}";

    private void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        IsEnabled = !busy;
        if (text is not null) StatusText.Text = text;
    }

    private void VisibilityFilter_Changed(object sender, RoutedEventArgs e)
    {
        _clientsView?.Refresh();
        _devicesView?.Refresh();
    }

    private async void ReloadButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void EnableClientButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || ClientsGrid.SelectedItem is not AdminClient client) return;
        if (client.IsActive) { StatusText.Text = "Клиент уже активен."; return; }
        if (System.Windows.MessageBox.Show(this,
                $"Включить клиента «{client.Name}» ({client.ClientId})? Это не восстанавливает лицензии устройств.",
                "Включить клиента", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await ExecuteAsync("Включение клиента…", (api, token) => api.UpdateClientAsync(client.ClientId!, new
        {
            name = client.Name ?? client.ClientId,
            client.Inn,
            architecture = client.Architecture ?? "x64",
            isActive = true,
            client.HasLmDatabaseBackup
        }, token), $"Client enabled; client={client.ClientId}");
    }

    private async void RestoreDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy && DevicesGrid.SelectedItem is AdminDevice device) await RestoreDeviceAsync(device);
    }

    private async void ReleaseDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy && DevicesGrid.SelectedItem is AdminDevice device) await ReleaseDeviceAsync(device, false);
    }

    private async void ReleaseForTransferButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy && DevicesGrid.SelectedItem is AdminDevice device) await ReleaseDeviceAsync(device, true);
    }

    private void DevicesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DevicesGrid.SelectedItem is not AdminDevice device)
        {
            LicenseHistoryGrid.ItemsSource = null;
            RegistrationHistoryGrid.ItemsSource = null;
            return;
        }
        LicenseHistoryGrid.ItemsSource = _licenses.Where(x => SameDevice(x.ClientId, x.DeviceId, device));
        RegistrationHistoryGrid.ItemsSource = _requests.Where(x => SameDevice(x.ClientId, x.DeviceId, device));
    }

    private static bool SameDevice(string? clientId, string? deviceId, AdminDevice selected) =>
        string.Equals(clientId, selected.ClientId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(deviceId, selected.DeviceId, StringComparison.OrdinalIgnoreCase);
}
