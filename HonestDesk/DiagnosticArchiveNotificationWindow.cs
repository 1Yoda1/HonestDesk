using HonestDesk.Infrastructure.Api;
using System.Windows;
using System.Windows.Controls;
using WpfButton = System.Windows.Controls.Button;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace HonestDesk;

public enum DiagnosticArchiveNotificationAction
{
    Later,
    Download,
    Delete
}

public sealed class DiagnosticArchiveNotificationWindow : Window
{
    public DiagnosticArchiveNotificationWindow(AdminDiagnosticArchive archive)
    {
        Title = "Новая диагностика";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var download = new WpfButton { Content = "Скачать и открыть", Padding = new Thickness(16, 9, 16, 9), MinWidth = 145 };
        download.SetResourceReference(StyleProperty, "PrimaryButton");
        download.Click += (_, _) => CloseWith(DiagnosticArchiveNotificationAction.Download);
        var delete = new WpfButton { Content = "Удалить", Padding = new Thickness(16, 9, 16, 9), MinWidth = 100, Margin = new Thickness(10, 0, 0, 0) };
        delete.SetResourceReference(StyleProperty, "SoftButton");
        delete.Click += (_, _) => CloseWith(DiagnosticArchiveNotificationAction.Delete);
        var later = new WpfButton { Content = "Позже", Padding = new Thickness(16, 9, 16, 9), MinWidth = 90, Margin = new Thickness(10, 0, 0, 0) };
        later.SetResourceReference(StyleProperty, "SoftButton");
        later.Click += (_, _) => CloseWith(DiagnosticArchiveNotificationAction.Later);

        Content = new Border
        {
            Padding = new Thickness(26),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "Получена диагностика", FontSize = 22, FontWeight = FontWeights.SemiBold },
                    new TextBlock
                    {
                        Margin = new Thickness(0, 12, 0, 20),
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 21,
                        Text = $"Клиент: {archive.ClientName}\n" +
                               $"Компьютер: {archive.DeviceName}\n" +
                               $"Адрес: {Display(archive.Address)}\n" +
                               $"Отправлено: {archive.CreatedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm}\n" +
                               $"Архив: {archive.FileName} ({archive.DisplaySize})\n\n" +
                               $"Если ничего не делать, файл будет удалён {archive.ExpiresAtUtc.ToLocalTime():dd.MM.yyyy HH:mm}. История останется."
                    },
                    new StackPanel
                    {
                        Orientation = WpfOrientation.Horizontal,
                        HorizontalAlignment = WpfHorizontalAlignment.Right,
                        Children = { download, delete, later }
                    }
                }
            }
        };
    }

    public DiagnosticArchiveNotificationAction Action { get; private set; } = DiagnosticArchiveNotificationAction.Later;

    private void CloseWith(DiagnosticArchiveNotificationAction action)
    {
        Action = action;
        DialogResult = true;
    }

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "не указан" : value;
}
