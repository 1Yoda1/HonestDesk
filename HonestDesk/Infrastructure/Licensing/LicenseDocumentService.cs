using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HonestDesk.Models.Licensing;
using Newtonsoft.Json;

namespace HonestDesk.Infrastructure.Licensing
{
    public sealed class LicenseDocumentService
    {
        public LicenseManifest Open(string jsonPath, string signaturePath, string publicKeyPath)
        {
            byte[] json = File.ReadAllBytes(jsonPath), signature = File.ReadAllBytes(signaturePath);
            if (!Verify(json, signature, publicKeyPath, out string error)) throw new CryptographicException("Подпись недействительна: " + error);
            return JsonConvert.DeserializeObject<LicenseManifest>(Encoding.UTF8.GetString(json)) ?? throw new InvalidDataException("licenses.json пуст.");
        }
        public void SaveSigned(LicenseManifest manifest, string jsonPath, LicenseEditorSettings settings, DateTimeOffset nowUtc)
        {
            if (string.IsNullOrWhiteSpace(settings.PrivateKeyPath) || !File.Exists(settings.PrivateKeyPath)) throw new InvalidOperationException("Закрытый ключ не настроен или недоступен.");
            if (string.IsNullOrWhiteSpace(settings.KeyId)) throw new InvalidOperationException("KeyId не задан.");
            if (settings.ValidityDays <= 0) throw new InvalidOperationException("ValidityDays должен быть больше нуля.");
            Validate(manifest); manifest.Revision = checked(manifest.Revision + 1); manifest.IssuedAtUtc = nowUtc.ToUniversalTime(); manifest.ValidUntilUtc = manifest.IssuedAtUtc.AddDays(settings.ValidityDays);
            byte[] json = new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(manifest, Formatting.Indented));
            byte[] sig = Sign(json, settings.KeyId, File.ReadAllText(settings.PrivateKeyPath)); SafeReplacePair(jsonPath, json, jsonPath + ".sig", sig);
        }
        public bool Verify(byte[] json, byte[] signatureFile, string publicKeyPath, out string error)
        {
            error = string.Empty; try {
                if (string.IsNullOrWhiteSpace(publicKeyPath) || !File.Exists(publicKeyPath)) throw new InvalidOperationException("Публичный ключ не настроен.");
                var e = JsonConvert.DeserializeObject<LicenseSignatureEnvelope>(Encoding.UTF8.GetString(signatureFile));
                if (e == null || e.Algorithm != LicenseSignatureEnvelope.AlgorithmName) throw new InvalidDataException("Неподдерживаемый формат подписи.");
                using ECDsa key = ECDsa.Create(); string text = File.ReadAllText(publicKeyPath).Trim();
                if (text.StartsWith("-----BEGIN", StringComparison.Ordinal)) key.ImportFromPem(text); else key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(text), out _);
                bool ok = key.KeySize == 256 && key.VerifyData(json, Convert.FromBase64String(e.Signature), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence); if (!ok) error = "SignatureMismatch"; return ok;
            } catch (Exception ex) { error = ex.Message; return false; }
        }
        public static void Validate(LicenseManifest m)
        {
            if (m == null || m.Revision < 0) throw new InvalidDataException("Manifest или Revision некорректны."); var clients = m.Clients ?? new();
            var operatorDevices = m.OperatorDevices ?? new();
            if (operatorDevices.Any(d => d == null || string.IsNullOrWhiteSpace(d.DeviceId))) throw new InvalidDataException("DeviceId операторского устройства обязателен.");
            if (operatorDevices.GroupBy(d => d.DeviceId.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) throw new InvalidDataException("DeviceId операторских устройств должны быть уникальны.");
            if (clients.Any(c => string.IsNullOrWhiteSpace(c.ClientId))) throw new InvalidDataException("ClientId обязателен.");
            if (clients.GroupBy(c => c.ClientId, StringComparer.Ordinal).Any(g => g.Count() > 1)) throw new InvalidDataException("ClientId должен быть уникален.");
            if (clients.SelectMany(c => c.Devices ?? new()).Any(d => d?.Address != null && d.Address.Trim().Length > 500)) throw new InvalidDataException("Адрес точки не может быть длиннее 500 символов.");
            if (clients.SelectMany(c => c.Devices ?? new()).Any(d => d?.ServiceEnabled == true &&
                    (d.ServiceValidUntilUtc is null || d.ServiceValidUntilUtc <= DateTimeOffset.UtcNow)))
                throw new InvalidDataException("Для HonestFlow Service укажите будущую дату окончания доступа.");
            foreach (var c in clients) { if (c.OfflineGraceHours < 0 || !Version.TryParse(c.MinHonestFlowVersion, out _)) throw new InvalidDataException($"Параметры клиента {c.ClientId} некорректны."); if ((c.Devices ?? new()).Where(d => !string.IsNullOrWhiteSpace(d.DeviceId)).GroupBy(d => d.DeviceId, StringComparer.Ordinal).Any(g => g.Count() > 1)) throw new InvalidDataException($"DeviceId клиента {c.ClientId} должен быть уникален."); }
        }
        private static byte[] Sign(byte[] json, string keyId, string pem) { using ECDsa key = ECDsa.Create(); key.ImportFromPem(pem); if (key.KeySize != 256) throw new CryptographicException("Требуется ECDSA P-256 key."); byte[] s = key.SignData(json, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence); return new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(new LicenseSignatureEnvelope { KeyId = keyId, Algorithm = LicenseSignatureEnvelope.AlgorithmName, Signature = Convert.ToBase64String(s) }, Formatting.Indented)); }
        private static void SafeReplacePair(string jp, byte[] json, string sp, byte[] sig)
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(jp));
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), jt = jp + ".tmp-" + Guid.NewGuid().ToString("N"), st = sp + ".tmp-" + Guid.NewGuid().ToString("N");
            string jb = jp + ".bak-" + stamp, sb = sp + ".bak-" + stamp; bool hadJson = File.Exists(jp), hadSig = File.Exists(sp);
            File.WriteAllBytes(jt, json); File.WriteAllBytes(st, sig); if (hadJson) File.Copy(jp, jb, true); if (hadSig) File.Copy(sp, sb, true);
            try { File.Move(jt, jp, true); File.Move(st, sp, true); }
            catch { if (hadJson) File.Copy(jb, jp, true); else if (File.Exists(jp)) File.Delete(jp); if (hadSig) File.Copy(sb, sp, true); else if (File.Exists(sp)) File.Delete(sp); throw; }
            finally { if (File.Exists(jt)) File.Delete(jt); if (File.Exists(st)) File.Delete(st); }
        }
    }
}
