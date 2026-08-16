using System.Net;
using System.Text;
using HonestDesk.Infrastructure.Api;
using Xunit;

namespace HonestDesk.Tests;

public sealed class ServiceInstallationAccessTests
{
    [Fact]
    public async Task SettingsCanBeLoadedWithoutPasswordOrHash()
    {
        var handler = new RecordingHandler("""
            {"isEnabled":true,"hasPassword":true,"updatedAtUtc":"2026-08-15T00:00:00Z"}
            """);
        var api = new HonestLicenseAdminApiClient("https://api.test", () => "admin", handler);

        AdminServiceInstallAccessSettings settings = await api.GetServiceInstallAccessAsync(CancellationToken.None);

        Assert.True(settings.IsEnabled);
        Assert.True(settings.HasPassword);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("api/admin/service-install-access", handler.Path);
    }

    [Fact]
    public async Task EnableDisableAndPasswordChangeUseDedicatedAdminEndpoint()
    {
        var handler = new RecordingHandler("""
            {"isEnabled":false,"hasPassword":true,"updatedAtUtc":"2026-08-15T00:00:00Z"}
            """);
        var api = new HonestLicenseAdminApiClient("https://api.test", () => "admin", handler);

        AdminServiceInstallAccessSettings settings = await api.PutServiceInstallAccessAsync(
            false, "new-service-password", CancellationToken.None);

        Assert.False(settings.IsEnabled);
        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Contains("\"isEnabled\":false", handler.Body);
        Assert.Contains("\"newPassword\":\"new-service-password\"", handler.Body);
        Assert.DoesNotContain("oldPassword", handler.Body);
    }

    [Fact]
    public void AdminUiUsesPasswordBoxClearsSecretAndHandlesUpdateErrors()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string xaml = File.ReadAllText(Path.Combine(root, "HonestDesk", "AdminEditorWindow.xaml"));
        string code = File.ReadAllText(Path.Combine(root, "HonestDesk", "AdminEditorWindow.xaml.cs"));

        Assert.Contains("ServiceInstallPasswordBox", xaml);
        Assert.Contains("PasswordBox", xaml);
        Assert.Contains("ServiceInstallPasswordBox.Clear()", code);
        Assert.Contains("Не удалось сохранить сервисный доступ", code);
        Assert.DoesNotContain("PasswordHash", xaml);
    }

    private sealed class RecordingHandler(string responseJson) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri?.IsAbsoluteUri == true
                ? request.RequestUri.AbsolutePath.TrimStart('/')
                : request.RequestUri?.ToString();
            Body = request.Content == null ? string.Empty :
                await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }
}
