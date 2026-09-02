using System.Security.Cryptography;
using System.Text;
using HonestDesk.Infrastructure.Licensing;
using HonestDesk.Models.Licensing;
using Newtonsoft.Json;
using Xunit;

namespace HonestDesk.Tests;

public sealed class PerDeviceServiceEntitlementTests
{
    [Fact]
    public void Builder_issues_service_only_for_enabled_device_with_its_own_expiry()
    {
        DateTimeOffset firstExpiry = DateTimeOffset.UtcNow.AddMonths(1);
        DateTimeOffset secondExpiry = DateTimeOffset.UtcNow.AddMonths(3);
        var manifest = CreateManifest(
            new LicensedDevice { DeviceId = "pc-1", Enabled = true, ServiceEnabled = true, ServiceValidUntilUtc = firstExpiry },
            new LicensedDevice { DeviceId = "pc-2", Enabled = true },
            new LicensedDevice { DeviceId = "pc-3", Enabled = true, ServiceEnabled = true, ServiceValidUntilUtc = secondExpiry });

        Dictionary<string, LicenseGrant> grants = BuildGrants(manifest);

        Assert.Contains(LicenseFeature.Service, grants["pc-1"].Features);
        Assert.Equal(firstExpiry.ToUniversalTime(), grants["pc-1"].ValidUntilUtc);
        Assert.DoesNotContain(LicenseFeature.Service, grants["pc-2"].Features);
        Assert.Equal(manifest.ValidUntilUtc, grants["pc-2"].ValidUntilUtc);
        Assert.Contains(LicenseFeature.Service, grants["pc-3"].Features);
        Assert.Equal(secondExpiry.ToUniversalTime(), grants["pc-3"].ValidUntilUtc);
    }

    [Fact]
    public void Service_template_is_not_inherited_and_legacy_features_remain()
    {
        List<LicenseFeature> features = DeviceServiceGrantSemantics.CreateDeviceFeatures(
            [LicenseFeature.ViewAndRepair, LicenseFeature.Service], serviceEnabled: false);

        Assert.Contains(LicenseFeature.ViewAndRepair, features);
        Assert.DoesNotContain(LicenseFeature.Service, features);
    }

    [Fact]
    public void Operator_device_never_receives_service()
    {
        var manifest = CreateManifest(new LicensedDevice
        {
            DeviceId = "operator-id", Enabled = true, ServiceEnabled = true,
            ServiceValidUntilUtc = DateTimeOffset.UtcNow.AddDays(30)
        });
        manifest.OperatorDevices.Add(new OperatorDevice { DeviceId = "operator-id", Enabled = true });

        LicenseGrant grant = Assert.Single(BuildGrants(manifest).Values);

        Assert.True(grant.OperatorDevice);
        Assert.DoesNotContain(LicenseFeature.Service, grant.Features);
    }

    [Fact]
    public void New_or_legacy_device_defaults_to_free_and_old_manifest_deserializes()
    {
        var device = new LicensedDevice();
        Assert.False(device.ServiceEnabled);
        Assert.Null(device.ServiceValidUntilUtc);

        LicenseManifest legacy = JsonConvert.DeserializeObject<LicenseManifest>(
            "{\"schemaVersion\":1,\"clients\":[{\"clientId\":\"client\",\"minHonestFlowVersion\":\"3.0.0\",\"devices\":[{\"deviceId\":\"pc\"}]}]}")!;
        LicensedDevice restored = Assert.Single(Assert.Single(legacy.Clients).Devices);
        Assert.False(restored.ServiceEnabled);
        Assert.Null(restored.ServiceValidUntilUtc);
    }

    [Fact]
    public void Published_grant_restores_device_service_state_and_disable_keeps_device_enabled()
    {
        var device = new LicensedDevice { Enabled = true };
        var serviceGrant = new LicenseGrant
        {
            Features = [LicenseFeature.Service],
            ValidUntilUtc = DateTimeOffset.UtcNow.AddDays(30)
        };
        DeviceServiceGrantSemantics.RestorePublishedState(device, serviceGrant);
        Assert.True(device.ServiceEnabled);
        Assert.Equal(serviceGrant.ValidUntilUtc, device.ServiceValidUntilUtc);

        DeviceServiceGrantSemantics.RestorePublishedState(device, new LicenseGrant { Features = [] });
        Assert.False(device.ServiceEnabled);
        Assert.Null(device.ServiceValidUntilUtc);
        Assert.True(device.Enabled);
    }

    [Fact]
    public void Service_requires_future_expiry_and_renewal_changes_grant_revision_and_expiry()
    {
        var expired = CreateManifest(new LicensedDevice
        {
            DeviceId = "pc", ServiceEnabled = true,
            ServiceValidUntilUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        Assert.Throws<InvalidDataException>(() => BuildGrants(expired));

        var first = CreateManifest(new LicensedDevice
        {
            DeviceId = "pc", ServiceEnabled = true,
            ServiceValidUntilUtc = DateTimeOffset.UtcNow.AddMonths(1)
        });
        var renewed = CreateManifest(new LicensedDevice
        {
            DeviceId = "pc", ServiceEnabled = true,
            ServiceValidUntilUtc = DateTimeOffset.UtcNow.AddMonths(12)
        });
        renewed.Revision = first.Revision + 1;

        LicenseGrant firstGrant = Assert.Single(BuildGrants(first).Values);
        LicenseGrant renewedGrant = Assert.Single(BuildGrants(renewed).Values);
        Assert.True(renewedGrant.Revision > firstGrant.Revision);
        Assert.True(renewedGrant.ValidUntilUtc > firstGrant.ValidUntilUtc);
    }

    private static LicenseManifest CreateManifest(params LicensedDevice[] devices) => new()
    {
        SchemaVersion = 1,
        Revision = 10,
        IssuedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        ValidUntilUtc = DateTimeOffset.UtcNow.AddDays(90),
        Clients =
        [
            new ClientLicense
            {
                ClientId = "client-a",
                Enabled = true,
                MinHonestFlowVersion = "3.0.0",
                OfflineGraceHours = 24,
                Features = [LicenseFeature.ViewAndRepair, LicenseFeature.Service],
                Devices = devices.ToList()
            }
        ]
    };

    private static Dictionary<string, LicenseGrant> BuildGrants(LicenseManifest manifest)
    {
        string keyPath = Path.Combine(Path.GetTempPath(), "honestdesk-service-key-" + Guid.NewGuid().ToString("N") + ".pem");
        try
        {
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
            return new LicenseGrantPublicationBuilder().Build(
                    manifest,
                    "test-key",
                    keyPath,
                    new Dictionary<string, bool?> { ["client-a"] = true })
                .Select(item => JsonConvert.DeserializeObject<LicenseGrant>(Encoding.UTF8.GetString(item.GrantBytes))!)
                .ToDictionary(grant => grant.DeviceId, StringComparer.Ordinal);
        }
        finally
        {
            if (File.Exists(keyPath))
                File.Delete(keyPath);
        }
    }
}