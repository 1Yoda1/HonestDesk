using System.Net;
using System.Text;
using HonestDesk.Infrastructure.Api;
using Newtonsoft.Json.Linq;
using Xunit;

namespace HonestDesk.Tests;

public sealed class ConnectionRequestTests
{
    [Fact]
    public async Task Website_details_are_loaded_from_admin_api()
    {
        var handler = new Handler(HttpStatusCode.OK, """
            [{"id":7,"contactName":"Иван","company":"Компания","phone":"+79990000000",
              "city":"Омск","workplaceCount":3,"comment":"Модели ККТ: АТОЛ 30Ф",
              "status":"New","createdAtUtc":"2026-11-04T00:00:00Z"}]
            """);
        var api = new HonestLicenseAdminApiClient("https://api.test", () => "test-admin", handler);
        var item = Assert.Single(await api.GetConnectionRequestsAsync("New", CancellationToken.None));
        Assert.Equal("/api/admin/connection-requests?status=New", handler.Path);
        Assert.Equal("test-admin", handler.AdminKey);
        Assert.Equal("Иван", item.ContactName);
        Assert.Equal("Модели ККТ: АТОЛ 30Ф", item.Comment);
        Assert.Equal(3, item.WorkplaceCount);
        Assert.Equal("Новая", item.StatusText);
    }

    [Fact]
    public async Task Status_write_passes_expected_server_state_and_propagates_conflict()
    {
        var handler = new Handler(HttpStatusCode.Conflict, "{\"code\":\"connection_request_changed\"}");
        var api = new HonestLicenseAdminApiClient("https://api.test", () => "test-admin", handler);
        var error = await Assert.ThrowsAsync<HonestLicenseAdminApiException>(() =>
            api.SetConnectionRequestStatusAsync(7, "Completed", "InProgress", CancellationToken.None));
        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Equal("/api/admin/connection-requests/7/status", handler.Path);
        var payload = JObject.Parse(handler.Body!);
        Assert.Equal("Completed", (string?)payload["status"]);
        Assert.Equal("InProgress", (string?)payload["expectedStatus"]);
    }

    [Fact]
    public async Task Permanent_delete_uses_admin_key_and_preserves_api_errors()
    {
        var handler = new Handler(HttpStatusCode.ServiceUnavailable, "{\"code\":\"connection_request_erasure_pending\"}");
        var api = new HonestLicenseAdminApiClient("https://api.test", () => "test-admin", handler);
        var error = await Assert.ThrowsAsync<HonestLicenseAdminApiException>(() =>
            api.DeleteConnectionRequestAsync(7, CancellationToken.None));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal(HttpMethod.Delete, handler.Method);
        Assert.Equal("/api/admin/connection-requests/7", handler.Path);
        Assert.Equal("test-admin", handler.AdminKey);
        Assert.Null(handler.Body);
    }

    private sealed class Handler(HttpStatusCode status, string json) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? AdminKey { get; private set; }
        public string? Body { get; private set; }
        public HttpMethod? Method { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.PathAndQuery;
            AdminKey = request.Headers.GetValues("X-Admin-Key").Single();
            Method = request.Method;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
