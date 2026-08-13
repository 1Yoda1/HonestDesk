using System.Security.Cryptography;
using System.Text;
using HonestDesk.Infrastructure.Licensing;
using HonestDesk.Models.Licensing;
using Newtonsoft.Json;
using Xunit;

namespace HonestDesk.Tests;

public sealed class ClientPolicyGrantSemanticsTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, true)]
    public void Publisher_DerivesClientEnabledFromPolicy(
        bool? policyEnabled,
        bool expectedClientEnabled)
    {
        string privateKeyPath = Path.Combine(
            Path.GetTempPath(), "honestdesk-policy-key-" + Guid.NewGuid().ToString("N") + ".pem");
        try
        {
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(privateKeyPath, key.ExportPkcs8PrivateKeyPem());
            var manifest = new LicenseManifest
            {
                SchemaVersion = 1,
                Revision = 10,
                IssuedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                ValidUntilUtc = DateTimeOffset.UtcNow.AddDays(1),
                Clients =
                [
                    new ClientLicense
                    {
                        ClientId = "client-a",
                        Enabled = !expectedClientEnabled,
                        MinHonestFlowVersion = "3.0.0",
                        OfflineGraceHours = 24,
                        Devices =
                        [
                            new LicensedDevice
                            {
                                DeviceId = "device-a",
                                Enabled = true
                            }
                        ]
                    }
                ]
            };
            var policies = new Dictionary<string, bool?>
            {
                ["client-a"] = policyEnabled
            };

            LicenseGrantPublication publication = Assert.Single(
                new LicenseGrantPublicationBuilder().Build(
                    manifest, "test-key", privateKeyPath, policies));
            LicenseGrant grant = JsonConvert.DeserializeObject<LicenseGrant>(
                Encoding.UTF8.GetString(publication.GrantBytes))!;

            Assert.Equal(expectedClientEnabled, grant.ClientEnabled);
        }
        finally
        {
            if (File.Exists(privateKeyPath))
                File.Delete(privateKeyPath);
        }
    }

    [Fact]
    public void LicenseManager_HasOnlyHonestFlowAccessClientLevelToggle()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(
            Path.Combine(root, "HonestDesk", "LicenseManagerWindow.xaml"));

        Assert.Contains("x:Name=\"HonestFlowAccessButton\"", xaml);
        Assert.DoesNotContain("LicenseEnabledCheck", xaml);
        Assert.DoesNotContain("ClientEnabled в device-grants", xaml);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "HonestDesk.slnx")))
            current = current.Parent;
        return current?.FullName
            ?? throw new DirectoryNotFoundException("HonestDesk repository root was not found.");
    }
}
