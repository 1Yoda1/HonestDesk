using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace HonestDesk.Models.Licensing
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum LicenseFeature
    {
        [Description("Просмотр состояния и ремонт точки")]
        ViewAndRepair,

        [Description("Установка и обслуживание точки")]
        InstallAndMaintenance
    }
    public static class LicenseFeatureCatalog
    {
        public static IReadOnlyList<LicenseFeature> ConfigurableFeatures { get; } =
            Array.AsReadOnly(new[]
            {
                LicenseFeature.ViewAndRepair,
                LicenseFeature.InstallAndMaintenance
            });

        public static string GetDisplayName(LicenseFeature feature)
        {
            FieldInfo field = typeof(LicenseFeature).GetField(feature.ToString());
            return field?.GetCustomAttribute<DescriptionAttribute>()?.Description
                ?? feature.ToString();
        }
    }
    public sealed class LicensedDevice { public string DeviceId { get; set; } public string Name { get; set; } public string Address { get; set; } public bool Enabled { get; set; } = true; public string Comment { get; set; } }
    public sealed class OperatorDevice { public string DeviceId { get; set; } public string Name { get; set; } public bool Enabled { get; set; } = true; public string Comment { get; set; } }
    public sealed class ClientLicense
    {
        [JsonIgnore]
        public string DisplayName { get; set; } = "";

        public string ClientId { get; set; }
        public bool Enabled { get; set; } = true;
        public string MinHonestFlowVersion { get; set; }
        public int OfflineGraceHours { get; set; }
        public List<LicenseFeature> Features { get; set; } = new();
        public List<LicensedDevice> Devices { get; set; } = new();
    }
    public sealed class LicenseManifest
    {
        public int SchemaVersion { get; set; } = 1;
        public long Revision { get; set; }
        public DateTimeOffset IssuedAtUtc { get; set; }
        public DateTimeOffset ValidUntilUtc { get; set; }
        public List<OperatorDevice> OperatorDevices { get; set; } = new();
        public List<ClientLicense> Clients { get; set; } = new();
    }
    public sealed class LicenseSignatureEnvelope
    {
        public const string AlgorithmName = "ECDSA_P256_SHA256";
        public string KeyId { get; set; }
        public string Algorithm { get; set; }
        public string Signature { get; set; }
    }
}
