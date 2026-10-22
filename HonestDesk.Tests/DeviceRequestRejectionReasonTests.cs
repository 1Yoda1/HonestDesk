using Xunit;
using System.Net;
using System.Text;
using HonestDesk.Infrastructure.Api;

namespace HonestDesk.Tests;

public sealed class DeviceRequestRejectionReasonTests
{
    [Fact]
    public void EmptyReason_UsesNeutralUserFacingFallback() =>
        Assert.Equal("Заявка отклонена оператором.",
            DeviceRequestRejectionReason.Normalize("   "));

    [Fact]
    public void EnteredReason_IsTrimmedAndPreservedForApiRequest() =>
        Assert.Equal("Уточните номер дома.",
            DeviceRequestRejectionReason.Normalize("  Уточните номер дома.  "));

    [Fact]
    public async Task RejectWithReason_SendsOptionalCommentToExistingApiContract()
    {
        var handler = new CapturingHandler();
        var api = new HonestLicenseAdminApiClient("https://example.test/", () => "admin-key", handler);

        await api.RejectDeviceRequestAsync(42, "Причина отказа", CancellationToken.None);

        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Equal("/api/admin/device-requests/42/reject", handler.Path);
        Assert.Contains("\"comment\":\"Причина отказа\"", handler.Body);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri?.AbsolutePath;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
