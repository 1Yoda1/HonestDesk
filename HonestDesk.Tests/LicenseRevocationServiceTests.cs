using System.Net;
using System.Text;
using HonestDesk.Infrastructure.Api;
using HonestDesk.Infrastructure.Licensing;
using Xunit;

namespace HonestDesk.Tests;

public sealed class LicenseRevocationServiceTests
{
    [Fact]
    public async Task ActiveLicenses_AreFilteredToSelectedClient_AndConcreteGrantIsRevoked()
    {
        var handler = new LicenseApiHandler();
        var service = new LicenseRevocationService(
            new HonestLicenseAdminApiClient("https://example.test/", () => "admin-key", handler));

        IReadOnlyList<AdminLicense> active = await service
            .GetActiveClientLicensesAsync("client-a", CancellationToken.None);
        AdminLicense selected = Assert.Single(active);
        Assert.Equal(31, selected.Id);

        await service.RevokeAsync(selected, CancellationToken.None);

        Assert.Equal("/api/admin/licenses/31/revoke", handler.LastPutPath);
    }

    [Fact]
    public async Task NonActiveLicense_IsRejectedBeforeHttpRequest()
    {
        var handler = new LicenseApiHandler();
        var service = new LicenseRevocationService(
            new HonestLicenseAdminApiClient("https://example.test/", () => "admin-key", handler));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RevokeAsync(
            new AdminLicense { Id = 32, Status = "Revoked" }, CancellationToken.None));

        Assert.Null(handler.LastPutPath);
    }

    [Fact]
    public async Task ReleaseDevice_UsesSingleExistingReleaseEndpoint()
    {
        var handler = new LicenseApiHandler();
        var api = new HonestLicenseAdminApiClient(
            "https://example.test/", () => "admin-key", handler);

        await api.ReleaseDeviceIdAsync(44, CancellationToken.None);

        Assert.Equal("/api/admin/devices/44/release", handler.LastPutPath);
    }

    [Fact]
    public async Task ApiFailure_DoesNotChangeSelectedLicenseLocally()
    {
        var handler = new LicenseApiHandler { FailPut = true };
        var service = new LicenseRevocationService(
            new HonestLicenseAdminApiClient("https://example.test/", () => "admin-key", handler));
        var selected = new AdminLicense { Id = 31, Status = "Active" };

        await Assert.ThrowsAsync<HonestLicenseAdminApiException>(() =>
            service.RevokeAsync(selected, CancellationToken.None));

        Assert.Equal("Active", selected.Status);
    }

    private sealed class LicenseApiHandler : HttpMessageHandler
    {
        public string? LastPutPath { get; private set; }
        public bool FailPut { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put)
            {
                LastPutPath = request.RequestUri?.AbsolutePath;
                return Task.FromResult(FailPut
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("server error")
                    }
                    : new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            string json = "[" +
                "{\"id\":31,\"clientId\":\"client-a\",\"deviceId\":\"device-a\",\"revision\":5,\"status\":\"Active\"}," +
                "{\"id\":32,\"clientId\":\"client-a\",\"deviceId\":\"device-b\",\"revision\":4,\"status\":\"Revoked\"}," +
                "{\"id\":33,\"clientId\":\"client-b\",\"deviceId\":\"device-c\",\"revision\":6,\"status\":\"Active\"}" +
                "]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
