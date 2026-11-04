using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HonestDesk.Infrastructure.Api;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace HonestDesk.Tests;

public sealed class ConnectionRequestViewTests
{
    [Fact]
    public void View_requires_server_confirmation_and_recovers_from_errors_and_conflicts()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { await ExerciseViewAsync(); }
                catch (Exception ex) { failure = ex; }
                finally { dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF workflow timed out");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task ExerciseViewAsync()
    {
        var app = new App();
        app.InitializeComponent();
        var handler = new Server();
        var api = new HonestLicenseAdminApiClient("https://synthetic.test", () => "synthetic-key", handler);
        bool confirmDeletion = false;
        var view = new ConnectionRequestsView(() => api, _ => confirmDeletion);
        var grid = (DataGrid)view.FindName("RequestsGrid");
        var actions = (WrapPanel)view.FindName("ActionsPanel");
        var refresh = (Button)view.FindName("RefreshButton");
        var details = (TextBox)view.FindName("DetailsText");
        var filter = (ComboBox)view.FindName("StatusFilter");
        Assert.False(actions.IsEnabled);
        await view.RefreshAsync();
        Assert.Single(grid.Items.Cast<AdminConnectionRequest>());
        Assert.Contains("АТОЛ 30Ф", details.Text);
        Assert.True(actions.IsEnabled);

        await RenderSnapshotsAsync(view);

        handler.PendingWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ((Button)actions.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("New", ((AdminConnectionRequest)grid.SelectedItem).Status);
        Assert.False(actions.IsEnabled);
        handler.PendingWrite.SetResult(true);
        await WaitAsync(() => refresh.IsEnabled);
        Assert.Equal("InProgress", ((AdminConnectionRequest)grid.SelectedItem).Status);

        handler.FailWrite = true;
        ((Button)actions.Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => refresh.IsEnabled);
        Assert.Equal("InProgress", ((AdminConnectionRequest)grid.SelectedItem).Status);
        Assert.False(actions.IsEnabled);
        Assert.Contains("Не удалось подтвердить", ((TextBlock)view.FindName("StatusText")).Text);
        handler.FailWrite = false;
        await view.RefreshAsync();
        Assert.True(actions.IsEnabled);

        handler.Status = "Rejected"; // Another operator changed the server record.
        ((Button)actions.Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => refresh.IsEnabled);
        Assert.Equal("Rejected", ((AdminConnectionRequest)grid.SelectedItem).Status);
        Assert.Contains("другим оператором", ((TextBlock)view.FindName("StatusText")).Text);
        filter.SelectedIndex = 3;
        Assert.Empty(grid.Items.Cast<object>());
        Assert.False(actions.IsEnabled);
        filter.SelectedIndex = 0;
        Assert.Single(grid.Items.Cast<object>());
        var deleteButton = (Button)view.FindName("DeleteButton");
        deleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(0, handler.DeleteCalls);
        Assert.Single(grid.Items.Cast<object>());

        confirmDeletion = true;
        handler.FailDelete = true;
        deleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => refresh.IsEnabled);
        Assert.Single(grid.Items.Cast<object>());
        Assert.False(actions.IsEnabled);
        handler.FailDelete = false;
        await view.RefreshAsync();

        handler.PendingDelete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        deleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Single(grid.Items.Cast<object>()); // No local success before the server confirms.
        Assert.False(actions.IsEnabled);
        handler.FailNextRead = true; // The DELETE succeeds, but reloading the remaining list fails.
        handler.PendingDelete.SetResult(true);
        await WaitAsync(() => refresh.IsEnabled);
        Assert.True(handler.Deleted);
        Assert.Empty(grid.Items.Cast<object>());
        Assert.DoesNotContain("АТОЛ", details.Text);
        Assert.DoesNotContain("Контактное лицо", details.Text);
        Assert.Contains("Заявка удалена", ((TextBlock)view.FindName("StatusText")).Text);
        await view.RefreshAsync();
        Assert.Empty(grid.Items.Cast<object>());
        Assert.False(actions.IsEnabled);
        app.Shutdown();
    }

    private static async Task WaitAsync(Func<bool> done)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!done()) await Task.Delay(5, cts.Token);
    }

    private static async Task RenderSnapshotsAsync(ConnectionRequestsView view)
    {
        string? directory = Environment.GetEnvironmentVariable("HONESTDESK_UI_SNAPSHOT_DIRECTORY");
        var host = new Window
        {
            Content = view, ShowInTaskbar = false, ShowActivated = false,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Left = -20000, Top = -20000, Width = 870, Height = 660
        };
        host.Show();
        try
        {
            foreach ((int width, int height) in new[] { (870, 660), (1190, 820) })
            {
                host.Width = width;
                host.Height = height;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                view.UpdateLayout();
                Assert.True(((DataGrid)view.FindName("RequestsGrid")).ActualHeight >= 100);
                Assert.True(((WrapPanel)view.FindName("ActionsPanel")).ActualHeight > 20);
                if (string.IsNullOrEmpty(directory)) continue;
                Directory.CreateDirectory(directory);
                foreach (double scale in new[] { 1d, 1.25d, 1.5d })
                {
                    var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale),
                        96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    bitmap.Render(view);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(directory, $"connections-{width}-{scale:0.00}.png"));
                    encoder.Save(stream);
                }
            }
        }
        finally { host.Content = null; host.Close(); }
    }

    private sealed class Server : HttpMessageHandler
    {
        public string Status { get; set; } = "New";
        public bool FailWrite { get; set; }
        public bool FailDelete { get; set; }
        public bool FailNextRead { get; set; }
        public bool Deleted { get; private set; }
        public int DeleteCalls { get; private set; }
        public TaskCompletionSource<bool>? PendingDelete { get; set; }
        public TaskCompletionSource<bool>? PendingWrite { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Delete)
            {
                DeleteCalls++;
                if (FailDelete) return Response(HttpStatusCode.ServiceUnavailable, new { code = "synthetic_failure" });
                if (PendingDelete is not null) { await PendingDelete.Task.WaitAsync(token); PendingDelete = null; }
                Deleted = true;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Put)
            {
                if (PendingWrite is not null) { await PendingWrite.Task.WaitAsync(token); PendingWrite = null; }
                if (FailWrite) return Response(HttpStatusCode.ServiceUnavailable, new { code = "synthetic_failure" });
                var payload = JObject.Parse(await request.Content!.ReadAsStringAsync(token));
                if ((string?)payload["expectedStatus"] != Status)
                    return Response(HttpStatusCode.Conflict, new { code = "connection_request_changed" });
                Status = (string)payload["status"]!;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (FailNextRead)
            {
                FailNextRead = false;
                return Response(HttpStatusCode.ServiceUnavailable, new { code = "synthetic_failure" });
            }
            if (Deleted) return Response(HttpStatusCode.OK, Array.Empty<object>());
            return Response(HttpStatusCode.OK, new[] { new {
                id = 1, status = Status, createdAtUtc = "2026-11-04T00:00:00Z",
                contactName = "Контактное лицо с длинным именем", company = "Компания с длинным названием",
                phone = "+7 (999) 000-00-00", city = "Омск", workplaceCount = 3,
                comment = "Модели ККТ: АТОЛ 30Ф\n\nНужна поддержка нескольких торговых точек.", source = "honestflow.ru"
            } });
        }
        private static HttpResponseMessage Response(HttpStatusCode status, object value) => new(status)
        { Content = new StringContent(JsonConvert.SerializeObject(value), Encoding.UTF8, "application/json") };
    }
}
