using System.Net;
using System.Text;
using HonestDesk.Infrastructure.Api;
using HonestDesk.Infrastructure.Licensing;
using Newtonsoft.Json;
using Xunit;

namespace HonestDesk.Tests;

public sealed class OperatorDeviceAdministrationServiceTests
{
    [Fact]
    public async Task Load_UsesServerLicensesAsSourceOfTruth_AndKeepsHistory()
    {
        var handler = new OperatorApiHandler();
        var service = CreateService(handler);

        IReadOnlyList<PublishedOperatorDevice> items = await service.GetAsync(CancellationToken.None);

        Assert.Equal(2, items.Count);
        PublishedOperatorDevice active = Assert.Single(items, x => x.LicenseId == 10);
        Assert.Equal("Client A", active.ClientName);
        Assert.Equal("Operator PC", active.DeviceName);
        Assert.Equal("Active", active.DeviceStatus);
        Assert.True(active.CanRevoke);
        PublishedOperatorDevice history = Assert.Single(items, x => x.LicenseId == 12);
        Assert.Equal("Revoked", history.LicenseStatus);
        Assert.False(history.CanRevoke);
    }

    [Fact]
    public async Task Revoke_UsesExistingLicenseEndpoint_WithoutRemovingHistoryLocally()
    {
        var handler = new OperatorApiHandler();
        var service = CreateService(handler);
        var item = new PublishedOperatorDevice { LicenseId = 10, LicenseStatus = "Active" };

        await service.RevokeAsync(item, CancellationToken.None);

        Assert.Contains(handler.Requests, x => x.Method == HttpMethod.Put &&
                                               x.Path == "/api/admin/licenses/10/revoke");
    }

    [Fact]
    public async Task Disable_UsesOrdinaryDeviceLifecycle_AndPreservesMetadata()
    {
        var handler = new OperatorApiHandler();
        var service = CreateService(handler);
        var item = new PublishedOperatorDevice
        {
            LicenseId = 10,
            LicenseStatus = "Active",
            DeviceDatabaseId = 20,
            DeviceName = "Operator PC",
            Address = "Shop address",
            Comment = "Keep me",
            DeviceStatus = "Active"
        };

        await service.SetDeviceEnabledAsync(item, enabled: false, CancellationToken.None);

        RequestRecord request = Assert.Single(handler.Requests, x => x.Path == "/api/admin/devices/20");
        Assert.Contains("\"status\":\"Disabled\"", request.Body);
        Assert.Contains("\"address\":\"Shop address\"", request.Body);
        Assert.Contains("\"comment\":\"Keep me\"", request.Body);
    }

    [Fact]
    public async Task HistoricalGrant_CannotMutateCurrentDeviceOrBeRevokedAgain()
    {
        var service = CreateService(new OperatorApiHandler());
        var history = new PublishedOperatorDevice
        {
            LicenseId = 12,
            LicenseStatus = "Revoked",
            DeviceDatabaseId = 20,
            DeviceStatus = "Active"
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RevokeAsync(history, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetDeviceEnabledAsync(history, enabled: false, CancellationToken.None));
    }

    private static OperatorDeviceAdministrationService CreateService(HttpMessageHandler handler) =>
        new(new HonestLicenseAdminApiClient("https://example.test/", () => "admin-key", handler));

    private sealed class OperatorApiHandler : HttpMessageHandler
    {
        public List<RequestRecord> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? "";
            string body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RequestRecord(request.Method, path, body));

            if (request.Method == HttpMethod.Get && path == "/api/admin/licenses")
                return Json(new[]
                {
                    new { id = 10, clientId = "client-a", clientName = "Client A", deviceId = "device-a", revision = 3, status = "Active", issuedAtUtc = DateTimeOffset.UtcNow, validUntilUtc = DateTimeOffset.UtcNow.AddDays(30) },
                    new { id = 11, clientId = "client-a", clientName = "Client A", deviceId = "device-a", revision = 2, status = "Superseded", issuedAtUtc = DateTimeOffset.UtcNow, validUntilUtc = DateTimeOffset.UtcNow.AddDays(30) },
                    new { id = 12, clientId = "client-a", clientName = "Client A", deviceId = "device-a", revision = 1, status = "Revoked", issuedAtUtc = DateTimeOffset.UtcNow, validUntilUtc = DateTimeOffset.UtcNow.AddDays(30) }
                });
            if (request.Method == HttpMethod.Get && path == "/api/admin/devices")
                return Json(new[]
                {
                    new { id = 20, clientId = "client-a", clientName = "Client A", deviceId = "device-a", name = "Operator PC", address = "Shop address", comment = "Keep me", status = "Active", deviceIdReleased = false, registeredAtUtc = DateTimeOffset.UtcNow }
                });
            if (request.Method == HttpMethod.Get && path.StartsWith("/api/admin/licenses/", StringComparison.Ordinal))
            {
                int id = int.Parse(path.Split('/').Last());
                bool isOperator = id != 11;
                return Json(new
                {
                    id,
                    clientId = "client-a",
                    deviceId = "device-a",
                    revision = id == 10 ? 3 : id == 11 ? 2 : 1,
                    status = id == 10 ? "Active" : id == 11 ? "Superseded" : "Revoked",
                    grantJson = JsonConvert.SerializeObject(new
                    {
                        schemaVersion = 1,
                        revision = 1,
                        clientId = "client-a",
                        deviceId = "device-a",
                        clientEnabled = true,
                        deviceEnabled = true,
                        operatorDevice = isOperator,
                        issuedAtUtc = DateTimeOffset.UtcNow,
                        validUntilUtc = DateTimeOffset.UtcNow.AddDays(30)
                    })
                });
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonConvert.SerializeObject(value), Encoding.UTF8, "application/json")
        };
    }

    private sealed record RequestRecord(HttpMethod Method, string Path, string Body);
}
