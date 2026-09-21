using System.Windows;

namespace IBTM.UI;

public partial class WarningDialog : Window
{
    public WarningDialog(string heading, string message, string details, string actionLabel)
    {
        Heading = heading;
        Message = message;
        Details = details;
        ActionLabel = actionLabel;
        InitializeComponent();
        DataContext = this;
    }

    public string Heading { get; }
    public string Message { get; }
    public string Details { get; }
    public string ActionLabel { get; }

    public static bool Confirm(Window? owner, string heading, string message, string details, string actionLabel)
    {
        var dialog = new WarningDialog(heading, message, details, actionLabel) { Owner = owner };
        return dialog.ShowDialog() == true;
    }

    private void OnDialogLoaded(object sender, RoutedEventArgs e)
    {
        CancelButton.Focus();
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
