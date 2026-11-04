using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using HonestDesk.Infrastructure.Api;
using HonestDesk.Infrastructure.Releases;
using HonestDesk.Models;
using HonestDesk.Services;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace HonestDesk;

public partial class DistributionsWindow : Window
{
    private static readonly string[] Components =
        ["LmModule", "AtolDriver", "ESM", "Controller", "HonestFlow", "HonestFlowInstaller"];
    private static readonly string[] Architectures = ["any", "x64", "x86", "arm64"];

    private readonly AdminAccessService _access;
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly HonestLicenseAdminApiClient _api;
    private IReadOnlyList<AdminAsset> _assets = [];
    private IReadOnlyList<AdminVersion> _versions = [];
    private IReadOnlyList<AdminClient> _clients = [];
    private CancellationTokenSource? _uploadCancellation;
    private bool _loading;

    public DistributionsWindow(
        AdminAccessService access,
        AppSettings settings,
        SettingsService settingsService)
    {
        InitializeComponent();
        _access = access;
        _settings = settings;
        _settingsService = settingsService;
        _api = new HonestLicenseAdminApiClient(
            settings.ApiBaseUrl, () => DpapiProtector.Unprotect(settings.ProtectedAdminApiKey));

        ComponentBox.ItemsSource = Components;
        ComponentBox.SelectedItem = "LmModule";
        ArchitectureBox.ItemsSource = Architectures;
        ArchitectureBox.SelectedItem = "any";
        KeyPathBox.Text = string.IsNullOrWhiteSpace(settings.ReleaseSigningKeyPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HonestFlowReleaseSigning", "release.hfrelkey")
            : settings.ReleaseSigningKeyPath;
        Loaded += async (_, _) => await RefreshCatalogAsync();
        Closed += (_, _) => _uploadCancellation?.Cancel();
    }

    private async Task RefreshCatalogAsync()
    {
        if (_loading) return;
        _loading = true;
        HeaderStatusText.Text = "Обновляем каталог…";
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            Task<IReadOnlyList<AdminAsset>> assetsTask = _api.GetAssetsAsync(null, cancellation.Token);
            Task<IReadOnlyList<AdminVersion>> versionsTask = _api.GetVersionsAsync(cancellation.Token);
            Task<IReadOnlyList<AdminClient>> clientsTask = _api.GetClientsAsync(cancellation.Token, includeInactive: false);
            await Task.WhenAll(assetsTask, versionsTask, clientsTask);
            _assets = await assetsTask;
            _versions = await versionsTask;
            _clients = await clientsTask;

            Dictionary<string, string> current = _versions
                .Where(x => !string.IsNullOrWhiteSpace(x.Application))
                .ToDictionary(x => Normalize(x.Application), x => x.CurrentVersion ?? "",
                    StringComparer.OrdinalIgnoreCase);
            AssetsGrid.ItemsSource = _assets
                .OrderBy(x => x.Component).ThenByDescending(x => x.Version).ThenBy(x => x.Architecture)
                .Select(x => new DistributionAssetRow(x,
                    current.TryGetValue(Normalize(x.Component), out string? version) &&
                    string.Equals(version, x.Version, StringComparison.Ordinal)))
                .ToList();
            ClientBox.ItemsSource = _clients
                .OrderBy(x => x.Name).ThenBy(x => x.ClientId)
                .Select(x => new ClientChoice(x)).ToList();
            if (ClientBox.Items.Count > 0 && ClientBox.SelectedIndex < 0)
                ClientBox.SelectedIndex = 0;
            HeaderStatusText.Text = $"Файлов: {_assets.Count}; global-версий: {_versions.Count}";
            _access.Audit($"Release catalog loaded; assets={_assets.Count}; versions={_versions.Count}");
        }
        catch (Exception ex)
        {
            HeaderStatusText.Text = "Не удалось загрузить каталог";
            _access.Audit("Release catalog load failed");
            MessageBox.Show(this, ex.Message, "Дистрибутивы", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _loading = false; }
    }

    private async Task RefreshResolutionAsync()
    {
        if (ClientBox.SelectedItem is not ClientChoice choice) return;
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            IReadOnlyList<AdminComponentOverride> overrides = await _api.GetComponentOverridesAsync(
                choice.Client.ClientId ?? "", cancellation.Token);
            Dictionary<string, string> overrideMap = overrides
                .Where(x => !string.IsNullOrWhiteSpace(x.Component) && !string.IsNullOrWhiteSpace(x.RequiredVersion))
                .ToDictionary(x => Normalize(x.Component), x => x.RequiredVersion!, StringComparer.OrdinalIgnoreCase);
            string architecture = NormalizeArchitecture(choice.Client.Architecture);
            var rows = new List<DistributionResolutionRow>();
            foreach (AdminVersion global in _versions.OrderBy(x => x.Application))
            {
                string component = global.Application ?? "";
                overrideMap.TryGetValue(Normalize(component), out string? componentOverride);
                string effective = componentOverride ?? global.CurrentVersion ?? "";
                AdminAsset? asset = _assets.Where(x =>
                        string.Equals(x.Component, component, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(x.Version, effective, StringComparison.Ordinal) &&
                        (string.Equals(x.Architecture, architecture, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(x.Architecture, "any", StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(x => string.Equals(x.Architecture, architecture,
                        StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .FirstOrDefault();
                rows.Add(new DistributionResolutionRow(
                    component, global.CurrentVersion ?? "—", componentOverride ?? "—", effective,
                    asset?.Architecture ?? "—", asset?.FileName ?? "—",
                    asset is null ? "—" : BuildDownloadUrl(component, effective),
                    AssetStatus(asset)));
            }
            ResolutionGrid.ItemsSource = rows;
        }
        catch (Exception ex)
        {
            ResolutionGrid.ItemsSource = null;
            HeaderStatusText.Text = "Не удалось проверить выдачу: " + ex.Message;
        }
    }

    private async void UploadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_uploadCancellation is not null) return;
        char[] password = KeyPasswordBox.Password.ToCharArray();
        KeyPasswordBox.Clear();
        try
        {
            string component = ComponentBox.SelectedItem?.ToString() ?? "";
            string architecture = ArchitectureBox.SelectedItem?.ToString() ?? "";
            string keyPath = KeyPathBox.Text.Trim();
            _settings.ReleaseSigningKeyPath = keyPath;
            _settingsService.Save(_settings);
            var request = new ReleasePublicationRequest(
                keyPath, password, FilePathBox.Text.Trim(), component,
                VersionBox.Text.Trim(), architecture);

            _uploadCancellation = new CancellationTokenSource();
            SetUploadBusy(true);
            var progress = new Progress<ReleasePublicationProgress>(value =>
            {
                UploadProgress.Value = value.Percent;
                UploadStatusText.Text = value.Status;
            });
            var publisher = new ReleasePublicationService(_api);
            ReleasePublicationResult result = await publisher.PublishAsync(
                request, progress, _uploadCancellation.Token);
            _access.Audit($"Release published; component={component}; version={request.Version}; " +
                          $"architecture={architecture}; keyId={result.KeyId}; skipped={result.UploadSkipped}");
            await RefreshCatalogAsync();
            bool globalUpdated = false;
            if (SetCurrentCheck.IsChecked == true)
                globalUpdated = await SetGlobalVersionAsync(component, request.Version, askConfirmation: false);
            MessageBox.Show(this, result.UploadSkipped
                    ? globalUpdated
                        ? "Идентичный выпуск уже был опубликован. Global-версия обновлена."
                        : "Идентичный выпуск уже был опубликован. Каталог актуален."
                    : globalUpdated
                        ? "Дистрибутив опубликован и назначен global-версией."
                        : "Дистрибутив успешно опубликован.",
                "Дистрибутивы", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            UploadStatusText.Text = "Загрузка отменена. Уже подтверждённые части останутся во временной multipart-сессии до очистки сервером.";
        }
        catch (CryptographicException)
        {
            UploadStatusText.Text = "Не удалось открыть release-ключ. Проверьте файл и пароль.";
            MessageBox.Show(this, UploadStatusText.Text, "Дистрибутивы",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            UploadStatusText.Text = "Публикация не завершена: " + ex.Message;
            _access.Audit("Release publication failed");
            MessageBox.Show(this, ex.Message, "Дистрибутивы", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(password.AsSpan()));
            _uploadCancellation?.Dispose();
            _uploadCancellation = null;
            SetUploadBusy(false);
        }
    }

    private async void SetGlobalButton_Click(object sender, RoutedEventArgs e)
    {
        if (AssetsGrid.SelectedItem is not DistributionAssetRow row) return;
        if (!row.IsSigned)
        {
            MessageBox.Show(this, "Назначать global можно только для подписанных выпусков Cloud.ru.",
                "Дистрибутивы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await SetGlobalVersionAsync(row.Component, row.Version, askConfirmation: true);
    }

    private async Task<bool> SetGlobalVersionAsync(
        string component, string version, bool askConfirmation)
    {
        string[] missingArchitectures = string.Equals(component, "HonestFlowInstaller", StringComparison.OrdinalIgnoreCase)
            ? []
            : _clients.Select(x => NormalizeArchitecture(x.Architecture)).Distinct()
                .Where(architecture => !_assets.Any(x =>
                    string.Equals(x.Component, component, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Version, version, StringComparison.Ordinal) &&
                    (string.Equals(x.Architecture, architecture, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(x.Architecture, "any", StringComparison.OrdinalIgnoreCase))))
                .OrderBy(x => x).ToArray();
        if (missingArchitectures.Length > 0)
        {
            MessageBox.Show(this,
                "Global-версия не изменена. Для активных клиентов не хватает файлов архитектур: " +
                string.Join(", ", missingArchitectures) + ".\n\n" +
                "Загрузите недостающие варианты и повторите назначение.",
                "Неполный выпуск", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (askConfirmation && MessageBox.Show(this,
                $"Назначить {component} {version} глобальной версией?\n\n" +
                "Клиенты без override начнут получать эту версию.",
                "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return false;
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _api.PutVersionAsync(component, version, cancellation.Token);
            _access.Audit($"Global release changed; component={component}; version={version}");
            await RefreshCatalogAsync();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Дистрибутивы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void SetUploadBusy(bool busy)
    {
        UploadButton.IsEnabled = !busy;
        CancelUploadButton.IsEnabled = busy;
        ComponentBox.IsEnabled = !busy;
        ArchitectureBox.IsEnabled = !busy;
        VersionBox.IsEnabled = !busy;
        SetCurrentCheck.IsEnabled = !busy;
        if (!busy && UploadProgress.Value < 100) UploadProgress.Value = 0;
    }

    private static string Normalize(string? value) =>
        (value ?? "").Replace("-", "").Replace("_", "").ToUpperInvariant();

    private static string NormalizeArchitecture(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "x86" or "x32" or "win32" => "x86",
        "x64" or "win64" or "amd64" => "x64",
        "arm64" => "arm64",
        _ => "any"
    };

    private string BuildDownloadUrl(string component, string version) =>
        new Uri(new Uri(_settings.ApiBaseUrl.TrimEnd('/') + "/"),
            $"api/assets/{Uri.EscapeDataString(component)}/{Uri.EscapeDataString(version)}/download").ToString();

    private static string AssetStatus(AdminAsset? asset)
    {
        if (asset is null) return "Файл не найден";
        if (string.IsNullOrWhiteSpace(asset.ReleaseKeyId) || string.IsNullOrWhiteSpace(asset.ObjectKey) ||
            string.IsNullOrWhiteSpace(asset.ManifestBase64) || string.IsNullOrWhiteSpace(asset.ReleaseSignatureBase64))
            return "Нет подписи";
        return "Готов к выдаче";
    }

    private void BrowseFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите дистрибутив",
            Filter = "Дистрибутивы (*.exe;*.msi;*.zip)|*.exe;*.msi;*.zip|Все файлы (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) FilePathBox.Text = dialog.FileName;
    }

    private void BrowseKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите закрытый release-ключ",
            Filter = "Release key (*.hfrelkey)|*.hfrelkey|Все файлы (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) KeyPathBox.Text = dialog.FileName;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshCatalogAsync();
    private async void ClientBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => await RefreshResolutionAsync();
    private void AssetsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SetGlobalButton.IsEnabled = AssetsGrid.SelectedItem is DistributionAssetRow;
    private void CancelUploadButton_Click(object sender, RoutedEventArgs e) => _uploadCancellation?.Cancel();

    private sealed record ClientChoice(AdminClient Client)
    {
        public string Display => $"{Client.Name ?? Client.ClientId} · {Client.ClientId} · {Client.Architecture ?? "any"}";
    }

    private sealed class DistributionAssetRow
    {
        public DistributionAssetRow(AdminAsset asset, bool current)
        {
            Source = asset;
            IsCurrent = current;
        }
        public AdminAsset Source { get; }
        public string Component => Source.Component ?? "";
        public string Version => Source.Version ?? "";
        public string Architecture => Source.Architecture ?? "";
        public string FileName => Source.FileName ?? "";
        public string DisplaySize => Source.DisplaySize;
        public string Storage => Source.StorageCaption;
        public string Signature => Source.SignatureCaption;
        public bool IsCurrent { get; }
        public bool IsSigned => !string.IsNullOrWhiteSpace(Source.ObjectKey) &&
                                !string.IsNullOrWhiteSpace(Source.ReleaseKeyId);
        public string CurrentStatus => IsCurrent ? "GLOBAL" : "В каталоге";
    }

    private sealed record DistributionResolutionRow(
        string Component,
        string GlobalVersion,
        string OverrideVersion,
        string EffectiveVersion,
        string AssetArchitecture,
        string FileName,
        string DownloadUrl,
        string Status);
}
