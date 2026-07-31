using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace HonestDesk.Infrastructure
{
    public sealed class YandexDiskCredentials
    {
        public string Login { get; set; }
        public string AppPassword { get; set; }
    }

    public interface IYandexDiskCredentialProtector
    {
        byte[] Protect(byte[] plaintext);
        byte[] Unprotect(byte[] protectedData);
    }

    public sealed class DpapiYandexDiskCredentialProtector : IYandexDiskCredentialProtector
    {
        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("HonestDesk.YandexDiskCredentials.v1");

        public byte[] Protect(byte[] plaintext)
        {
            return ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        }

        public byte[] Unprotect(byte[] protectedData)
        {
            return ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser);
        }
    }

    public sealed class LegacyDpapiYandexDiskCredentialProtector : IYandexDiskCredentialProtector
    {
        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("HonestFlowConfigEditor.YandexDiskCredentials.v1");

        public byte[] Protect(byte[] plaintext)
        {
            return ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        }

        public byte[] Unprotect(byte[] protectedData)
        {
            return ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser);
        }
    }

    public sealed class YandexDiskCredentialStore
    {
        private readonly IYandexDiskCredentialProtector _protector;

        public YandexDiskCredentialStore(
            string path,
            IYandexDiskCredentialProtector protector = null)
        {
            Path = string.IsNullOrWhiteSpace(path)
                ? throw new ArgumentException("Credential path is required.", nameof(path))
                : System.IO.Path.GetFullPath(path);
            _protector = protector ?? new DpapiYandexDiskCredentialProtector();
        }

        public static string DefaultPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HonestDesk",
            "secrets",
            "yandex-disk.dat");

        public static string LegacyPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HonestFlowConfigEditor",
            "secrets",
            "yandex-disk.dat");

        public string Path { get; }

        public YandexDiskCredentials Load()
        {
            if (!File.Exists(Path))
                return new YandexDiskCredentials();

            byte[] protectedBytes = File.ReadAllBytes(Path);
            if (protectedBytes.Length == 0)
                throw new InvalidDataException("Файл реквизитов Яндекс.Диска пуст.");

            byte[] jsonBytes = _protector.Unprotect(protectedBytes);
            var credentials = JsonConvert.DeserializeObject<YandexDiskCredentials>(
                Encoding.UTF8.GetString(jsonBytes));

            return credentials ?? throw new InvalidDataException(
                "Файл реквизитов Яндекс.Диска имеет некорректный формат.");
        }

        public void Save(YandexDiskCredentials credentials)
        {
            if (credentials == null)
                throw new ArgumentNullException(nameof(credentials));

            string directory = System.IO.Path.GetDirectoryName(Path);
            Directory.CreateDirectory(directory);

            byte[] jsonBytes = Encoding.UTF8.GetBytes(
                JsonConvert.SerializeObject(credentials, Formatting.None));
            byte[] protectedBytes = _protector.Protect(jsonBytes);
            string temporaryPath = System.IO.Path.Combine(
                directory,
                ".yandex-disk-" + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                File.WriteAllBytes(temporaryPath, protectedBytes);
                if (File.Exists(Path))
                    File.Replace(temporaryPath, Path, null);
                else
                    File.Move(temporaryPath, Path);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
    }
}
