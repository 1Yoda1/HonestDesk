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
        InstallAndMaintenance,

        [Description("HonestFlow Service")]
        Service
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
            FieldInfo? field = typeof(LicenseFeature).GetField(feature.ToString());
            return field?.GetCustomAttribute<DescriptionAttribute>()?.Description
                ?? feature.ToString();
        }
    }
    public sealed class LicensedDevice
    {
        public string DeviceId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Address { get; set; }
        public bool Enabled { get; set; } = true;
        public string Comment { get; set; } = string.Empty;
        public bool ServiceEnabled { get; set; }
        public DateTimeOffset? ServiceValidUntilUtc { get; set; }

        [JsonIgnore]
        public DateTime? ServiceValidUntilDate
        {
            get => ServiceValidUntilUtc?.UtcDateTime.Date;
            set => ServiceValidUntilUtc = value is null
                ? null
                : new DateTimeOffset(DateTime.SpecifyKind(
                    value.Value.Date.AddDays(1).AddTicks(-1), DateTimeKind.Utc));
        }

        [JsonIgnore]
        public string ServiceStatus => ServiceEnabled && ServiceValidUntilUtc is { } validUntil
            ? $"Service до {validUntil:dd.MM.yyyy}"
            : "Free";
    }
    public sealed class OperatorDevice { public string DeviceId { get; set; } = string.Empty; public string Name { get; set; } = string.Empty; public bool Enabled { get; set; } = true; public string Comment { get; set; } = string.Empty; }
    public sealed class ClientLicense
    {
        [JsonIgnore]
        public string DisplayName { get; set; } = "";

        public string ClientId { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public string MinHonestFlowVersion { get; set; } = string.Empty;
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
        public string KeyId { get; set; } = string.Empty;
        public string Algorithm { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;
    }
}
