using System.Net;
using System.Text;
using HonestDesk.Infrastructure.Api;
using HonestDesk.Infrastructure.Licensing;
using Newtonsoft.Json.Linq;
using Xunit;

namespace HonestDesk.Tests;

public sealed class ClientHonestFlowAccessServiceTests
{
    [Fact]
    public async Task GetAsync_ReadsExplicitClientLevelPolicy()
    {
        var handler = new PolicyApiHandler { StoredValue = false };
        var service = CreateService(handler);

        AdminClientLicensePolicy result = await service.GetAsync("client/a", CancellationToken.None);

        Assert.False(result.IsEnabled);
        Assert.Equal("/api/admin/clients/client%2Fa/license-policy", handler.LastPath);
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetAsync_SendsRequestedPolicyAndUsesServerResponse(bool enabled)
    {
        var handler = new PolicyApiHandler { StoredValue = !enabled };
        var service = CreateService(handler);

        AdminClientLicensePolicy result = await service.SetAsync(
            "client-a", enabled, CancellationToken.None);

        Assert.Equal(enabled, result.IsEnabled);
        Assert.Equal(HttpMethod.Put, handler.LastMethod);
        Assert.Equal("/api/admin/clients/client-a/license-policy", handler.LastPath);
        Assert.Equal(enabled, JObject.Parse(handler.LastBody!)["isEnabled"]!.Value<bool>());
    }

    [Fact]
    public async Task SetAsync_ApiFailureDoesNotReturnAChangedLocalState()
    {
        var handler = new PolicyApiHandler { StoredValue = true, FailPut = true };
        var service = CreateService(handler);

        await Assert.ThrowsAsync<HonestLicenseAdminApiException>(() =>
            service.SetAsync("client-a", false, CancellationToken.None));

        Assert.True(handler.StoredValue);
    }

    private static ClientHonestFlowAccessService CreateService(HttpMessageHandler handler) =>
        new(new HonestLicenseAdminApiClient("https://example.test/", () => "admin-key", handler));

    private sealed class PolicyApiHandler : HttpMessageHandler
    {
        public bool? StoredValue { get; set; }
        public bool FailPut { get; init; }
        public HttpMethod? LastMethod { get; private set; }
        public string? LastPath { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            LastPath = request.RequestUri?.AbsolutePath;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);

            if (request.Method == HttpMethod.Put)
            {
                if (FailPut)
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("server error")
                    };
                StoredValue = JObject.Parse(LastBody!)["isEnabled"]!.Value<bool>();
            }

            string json = $"{{\"clientId\":\"client-a\",\"isEnabled\":" +
                          (StoredValue.HasValue ? StoredValue.Value.ToString().ToLowerInvariant() : "null") + "}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
