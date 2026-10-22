using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;
using HonestDesk.Models;
using Newtonsoft.Json;

namespace HonestDesk.Infrastructure
{
    public static class ConfigManager
    {
        private static readonly string BasePath = AppDomain.CurrentDomain.BaseDirectory;
        public static string IpsEncryptedPath => Path.Combine(BasePath, "ips_encrypted.json");
        public static string VersionsPath => Path.Combine(BasePath, "versions.json");
        public static string SupportMailEncryptedPath => Path.Combine(BasePath, "support_mail_encrypted.json");

        // ========== Шифрование/дешифрование (XOR 0xAA) ==========
        public static string Obfuscate(string plainText)
        {
            var bytes = Encoding.UTF8.GetBytes(plainText);
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)(bytes[i] ^ 0xAA);
            return Convert.ToBase64String(bytes);
        }

        public static string Deobfuscate(string cipherText)
        {
            var bytes = Convert.FromBase64String(cipherText);
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)(bytes[i] ^ 0xAA);
            return Encoding.UTF8.GetString(bytes);
        }

        // ========== Локальное сохранение ==========
        public static void SaveIps(List<IPData> ips)
        {
            ConfigEditorLogger.Info($"Saving ips_encrypted.json to {IpsEncryptedPath}. Items: {ips?.Count ?? 0}");
            var json = JsonConvert.SerializeObject(ips, Newtonsoft.Json.Formatting.Indented);
            var encryptedJson = Obfuscate(json);
            File.WriteAllText(IpsEncryptedPath, encryptedJson);
        }

        public static void SaveVersions(VersionsData versions)
        {
            ConfigEditorLogger.Info($"Saving versions.json to {VersionsPath}");
            var json = JsonConvert.SerializeObject(versions, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(VersionsPath, json);
        }

        public static void SaveSupportMail(SupportMailSettings settings)
        {
            ConfigEditorLogger.Info($"Saving support_mail_encrypted.json to {SupportMailEncryptedPath}");
            var json = JsonConvert.SerializeObject(settings ?? new SupportMailSettings(), Newtonsoft.Json.Formatting.Indented);
            var encryptedJson = Obfuscate(json);
            File.WriteAllText(SupportMailEncryptedPath, encryptedJson);
        }

        public static void SaveBoth(List<IPData> ips, VersionsData versions)
        {
            SaveIps(ips);
            SaveVersions(versions);
        }

        public static void OpenOutputFolder()
        {
            try
            {
                Process.Start("explorer.exe", BasePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось открыть папку:\n{ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        public static void OpenYandexDisk()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = YandexDiskSettings.PublicUrl,
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Не удалось открыть браузер:\n{ex.Message}\n\nОткройте вручную:\n{YandexDiskSettings.PublicUrl}",
                    "Ошибка",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
    }
}
