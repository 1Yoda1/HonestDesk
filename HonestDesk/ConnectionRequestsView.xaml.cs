using System.Net;
using System.Windows;
using System.Windows.Controls;
using HonestDesk.Infrastructure.Api;

namespace HonestDesk;

public partial class ConnectionRequestsView : System.Windows.Controls.UserControl
{
    private readonly Func<HonestLicenseAdminApiClient> _api;
    private IReadOnlyList<AdminConnectionRequest> _items = [];
    private readonly Func<AdminConnectionRequest, bool> _confirmDelete;
    private bool _busy;
    private bool _loaded;

    public ConnectionRequestsView(Func<HonestLicenseAdminApiClient> api,
        Func<AdminConnectionRequest, bool>? confirmDelete = null)
    {
        _api = api;
        _confirmDelete = confirmDelete ?? ConfirmDelete;
        InitializeComponent();
    }

    public async Task RefreshAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try { await LoadAsync(); }
        catch (Exception ex)
        {
            _loaded = false;
            StatusText.Text = "Не удалось загрузить заявки. Показанные данные могут быть устаревшими. " + ex.Message;
        }
        finally { SetBusy(false); }
    }

    private async Task LoadAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _items = await _api().GetConnectionRequestsAsync(null, cts.Token);
        _loaded = true;
        ApplyFilter();
        StatusText.Text = $"Обновлено: {DateTime.Now:HH:mm:ss}";
    }

    private void ApplyFilter()
    {
        if (RequestsGrid is null) return;
        int? selectedId = (RequestsGrid.SelectedItem as AdminConnectionRequest)?.Id;
        string? status = (StatusFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        var visible = _items.Where(x => string.IsNullOrEmpty(status) || x.Status == status).ToList();
        RequestsGrid.ItemsSource = visible;
        RequestsGrid.SelectedItem = visible.FirstOrDefault(x => x.Id == selectedId) ?? visible.FirstOrDefault();
        SummaryText.Text = $"Показано: {visible.Count} · Новых: {_items.Count(x => x.Status == "New")}";
        ShowDetails();
    }

    private void ShowDetails()
    {
        if (DetailsText is null) return;
        var item = RequestsGrid.SelectedItem as AdminConnectionRequest;
        ActionsPanel.IsEnabled = !_busy && _loaded && item is not null;
        foreach (System.Windows.Controls.Button button in ActionsPanel.Children)
            button.IsEnabled = item is not null && (string)button.Tag != item.Status;
        DetailsText.Text = item is null ? "Выберите заявку" :
            $"Заявка №{item.Id} · {item.StatusText} · {item.CreatedAtLocal:dd.MM.yyyy HH:mm:ss}\n" +
            $"Имя: {item.ContactName}\nКомпания: {Display(item.Company)}\n" +
            $"Телефон: {item.Phone}\nEmail: {Display(item.Email)}\nГород: {Display(item.City)}\n" +
            $"Количество РМК: {item.WorkplaceCount}\nТовароучётная система: {Display(item.InventorySystem)}\n" +
            $"Источник: {Display(item.Source)}\n\n{Display(item.Comment)}";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RefreshButton.IsEnabled = !busy;
        StatusFilter.IsEnabled = !busy;
        RequestsGrid.IsEnabled = !busy;
        ShowDetails();
    }

    private async void ChangeStatus_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || RequestsGrid.SelectedItem is not AdminConnectionRequest item ||
            sender is not System.Windows.Controls.Button { Tag: string status } || status == item.Status) return;
        SetBusy(true);
        bool saved = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _api().SetConnectionRequestStatusAsync(item.Id, status, item.Status, cts.Token);
            saved = true;
            await LoadAsync();
        }
        catch (HonestLicenseAdminApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            try { await LoadAsync(); }
            catch { _loaded = false; }
            StatusText.Text = "Заявка уже изменена другим оператором. Обновите данные и проверьте её статус.";
        }
        catch (Exception ex)
        {
            _loaded = false;
            StatusText.Text = (saved
                ? "Статус сохранён на сервере, но список обновить не удалось. "
                : "Не удалось подтвердить изменение статуса. Обновите список перед повторной попыткой. ") + ex.Message;
        }
        finally { SetBusy(false); }
    }

    private async void DeleteRequest_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_loaded || RequestsGrid.SelectedItem is not AdminConnectionRequest item ||
            !_confirmDelete(item)) return;
        SetBusy(true);
        bool deleted = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _api().DeleteConnectionRequestAsync(item.Id, cts.Token);
            deleted = true;
            // Drop the local copy immediately, even if reloading the rest of the list fails.
            _items = _items.Where(x => x.Id != item.Id).ToList();
            ApplyFilter();
            await LoadAsync();
            StatusText.Text = "Заявка и связанные записи аудита удалены из рабочей базы сервера.";
        }
        catch (Exception ex)
        {
            _loaded = false;
            StatusText.Text = deleted
                ? "Заявка удалена, но список обновить не удалось. " + ex.Message
                : "Не удалось подтвердить полное удаление. Обновите список; сервер повторит очистку журнала. " + ex.Message;
        }
        finally { SetBusy(false); }
    }

    private bool ConfirmDelete(AdminConnectionRequest item) => System.Windows.MessageBox.Show(
        Window.GetWindow(this),
        $"Безвозвратно удалить заявку №{item.Id} и все её данные из базы сервера?\n\n" +
        "Контакты, комментарии и связанные записи аудита будут уничтожены. Восстановить заявку в приложении будет невозможно.",
        "Удаление персональных данных", MessageBoxButton.YesNo, MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;


    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void StatusFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();
    private void RequestsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetails();
    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
}
