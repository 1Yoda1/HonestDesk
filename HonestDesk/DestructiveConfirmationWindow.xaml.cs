using System.Windows;

namespace HonestDesk;

public partial class DestructiveConfirmationWindow : Window
{
    public DestructiveConfirmationWindow(string title, string message, string confirmText)
    {
        InitializeComponent();
        Title = title;
        HeadingText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
