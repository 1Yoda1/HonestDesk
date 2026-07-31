using System.Windows;
using HonestDesk.Models;

namespace HonestDesk;

public partial class ClientProfileWindow : Window
{
    private readonly Action _openAdmin;

    public ClientProfileWindow(ClientConfiguration client, string matchSource, Action openAdmin)
    {
        InitializeComponent();
        _openAdmin = openAdmin;
        ClientNameText.Text = string.IsNullOrWhiteSpace(client.Name) ? "Без названия" : client.Name;
        ClientIdText.Text = $"ClientId: {Value(client.ClientId)}  ·  совпадение: {matchSource}";
        ProfileText.Text =
            $"ИНН: {Value(client.Inn)}\n" +
            $"Архитектура: {Value(client.Architecture)}\n" +
            $"Резервная копия ЛМ: {(client.HasLmDatabaseBackup ? "есть" : "нет")}";
        SecurityText.Text =
            $"RuDesktop: {(client.RuDesktop.Enabled ? "включён" : "выключен")}\n" +
            $"Пароль RuDesktop: {Presence(client.RuDesktop.Password)}\n" +
            $"Токен клиента: {Presence(client.Token)}\n" +
            $"Доступ инженера: {(client.EngineerAccess is null ? "не настроен" : "настроен")}";
        HonestFlowVersionText.Text = Version(client.Versions.HonestFlow);
        LmVersionText.Text = Version(client.Versions.LmModule);
        AtolVersionText.Text = Version(client.Versions.AtolDriver);
        EsmVersionText.Text = Version(client.Versions.ESM);
        ControllerVersionText.Text = Version(client.Versions.Controller);
        TagsText.Text = client.Tags.Count == 0 ? "Дополнительные теги не заданы" : string.Join("   ·   ", client.Tags);
    }

    private static string Value(string? value) => string.IsNullOrWhiteSpace(value) ? "не указано" : value;
    private static string Version(string? value) => string.IsNullOrWhiteSpace(value) ? "глобальная" : value;
    private static string Presence(string? value) => string.IsNullOrWhiteSpace(value) ? "не задан" : "настроен ••••••";

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void AdminButton_Click(object sender, RoutedEventArgs e)
    {
        _openAdmin();
        Close();
    }
}
