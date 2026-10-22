namespace HonestDesk.Infrastructure
{
    public static class YandexDiskSettings
    {
        public const string RemoteDirectory = "/HonestFlowDistr";
        public const string PublicUrl = "https://disk.360.yandex.ru/d/sngNP8yBz9weWA";
        public const string HonestFlowProjectPath = @"F:\Работа\ШАДРОВ\pizdec\HonestFlow\HonestFlow.csproj";

        private static readonly object Sync = new object();
        private static readonly YandexDiskCredentialStore Store =
            new YandexDiskCredentialStore(YandexDiskCredentialStore.DefaultPath);
        private static YandexDiskCredentials _credentials = LoadSafely();

        public static string Login => _credentials.Login;
        public static string AppPassword => _credentials.AppPassword;
        public static string CredentialPath => Store.Path;

        public static bool IsConfigured =>
            !string.IsNullOrWhiteSpace(Login)
            && !string.IsNullOrWhiteSpace(AppPassword)
            && !string.IsNullOrWhiteSpace(RemoteDirectory);

        public static YandexDiskCredentials GetCredentials()
        {
            lock (Sync)
            {
                return new YandexDiskCredentials
                {
                    Login = _credentials.Login,
                    AppPassword = _credentials.AppPassword
                };
            }
        }

        public static void SaveCredentials(YandexDiskCredentials credentials)
        {
            if (credentials == null ||
                string.IsNullOrWhiteSpace(credentials.Login) ||
                string.IsNullOrWhiteSpace(credentials.AppPassword))
            {
                throw new System.ArgumentException(
                    "Укажите логин Яндекс.Диска и пароль приложения.",
                    nameof(credentials));
            }

            var normalized = new YandexDiskCredentials
            {
                Login = credentials.Login.Trim(),
                AppPassword = credentials.AppPassword.Trim()
            };

            Store.Save(normalized);
            YandexDiskCredentials saved = Store.Load();
            if (!string.Equals(saved.Login, normalized.Login, System.StringComparison.Ordinal) ||
                !string.Equals(saved.AppPassword, normalized.AppPassword, System.StringComparison.Ordinal))
            {
                throw new System.IO.IOException("Не удалось проверить сохранённые реквизиты Яндекс.Диска.");
            }

            lock (Sync)
                _credentials = saved;
        }

        private static YandexDiskCredentials LoadSafely()
        {
            try
            {
                YandexDiskCredentials credentials = Store.Load();
                if (!string.IsNullOrWhiteSpace(credentials.Login) &&
                    !string.IsNullOrWhiteSpace(credentials.AppPassword))
                {
                    return credentials;
                }

                if (!System.IO.File.Exists(YandexDiskCredentialStore.LegacyPath))
                    return credentials;

                var legacyStore = new YandexDiskCredentialStore(
                    YandexDiskCredentialStore.LegacyPath,
                    new LegacyDpapiYandexDiskCredentialProtector());
                YandexDiskCredentials legacyCredentials = legacyStore.Load();
                if (string.IsNullOrWhiteSpace(legacyCredentials.Login) ||
                    string.IsNullOrWhiteSpace(legacyCredentials.AppPassword))
                {
                    return credentials;
                }

                Store.Save(legacyCredentials);
                ConfigEditorLogger.Info("Yandex Disk credentials migrated to HonestDesk.");
                return Store.Load();
            }
            catch (System.Exception ex)
            {
                ConfigEditorLogger.Error(
                    "Не удалось прочитать локальные реквизиты Яндекс.Диска. " +
                    "Откройте «Доступ к Яндекс.Диску» и сохраните их заново.",
                    ex);
                return new YandexDiskCredentials();
            }
        }
    }
}
