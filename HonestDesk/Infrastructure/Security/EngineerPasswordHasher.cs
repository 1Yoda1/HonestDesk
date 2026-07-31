using System;
using System.Security.Cryptography;
using HonestDesk.Models;

namespace HonestDesk.Infrastructure.Security
{
    public sealed class EngineerPasswordHasher
    {
        public const string Algorithm = "PBKDF2-SHA256";
        public const int DefaultIterations = 210000;
        private const int SaltSize = 16;
        private const int HashSize = 32;

        public EngineerAccessSettings Create(string password)
        {
            if (string.IsNullOrWhiteSpace(password))
                throw new ArgumentException("Пароль инженера обязателен.", nameof(password));

            byte[] salt = new byte[SaltSize];
            RandomNumberGenerator.Fill(salt);
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                DefaultIterations,
                HashAlgorithmName.SHA256,
                HashSize);
            return new EngineerAccessSettings
            {
                Algorithm = Algorithm,
                Iterations = DefaultIterations,
                SaltBase64 = Convert.ToBase64String(salt),
                PasswordHashBase64 = Convert.ToBase64String(hash)
            };
        }

        public bool Verify(EngineerAccessSettings settings, string password)
        {
            if (settings == null ||
                !string.Equals(settings.Algorithm, Algorithm, StringComparison.Ordinal) ||
                settings.Iterations < 100000 ||
                string.IsNullOrWhiteSpace(password))
            {
                return false;
            }

            try
            {
                byte[] salt = Convert.FromBase64String(settings.SaltBase64);
                byte[] expected = Convert.FromBase64String(settings.PasswordHashBase64);
                byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
                    password,
                    salt,
                    settings.Iterations,
                    HashAlgorithmName.SHA256,
                    expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
