using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using IBTM.Core;

namespace IBTM.UI;

public partial class LogWindow : Window
{
    private readonly LogWindowViewModel _viewModel;
    private TextBox? _selectedTextBox;

    public LogWindow(ApplicationLog log)
    {
        _viewModel = new LogWindowViewModel(log);
        InitializeComponent();
        DataContext = _viewModel;
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private void OnTextSelectionChanged(object sender, RoutedEventArgs e)
    {
        var textBox = (TextBox)sender;
        if (textBox.SelectionLength > 0)
            _selectedTextBox = textBox;
        else if (_selectedTextBox == textBox)
            _selectedTextBox = null;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            string text;
            if (_selectedTextBox is { IsVisible: true, SelectionLength: > 0 })
            {
                text = _selectedTextBox.SelectedText;
            }
            else
            {
                var entries = LogList.SelectedItems.Count > 0 ? LogList.SelectedItems : LogList.Items;
                text = string.Join(
                    Environment.NewLine,
                    entries.Cast<LogEntry>().OrderByDescending(entry => entry.Sequence).Select(entry => entry.Text));
            }
            if (text.Length > 0)
                Clipboard.SetText(text);
            _viewModel.ClipboardError = null;
        }
        catch (ExternalException exception)
        {
            _viewModel.ClipboardError = $"Clipboard is unavailable: {exception.Message}";
        }
    }
}
