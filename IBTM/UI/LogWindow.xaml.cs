using System;
using System.Runtime.InteropServices;
using System.Windows;
using IBTM.Core;

namespace IBTM.UI;

public partial class LogWindow : Window
{
    private readonly LogWindowViewModel _viewModel;

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
        if (LogText.IsKeyboardFocusWithin && LogText.SelectionLength > 0)
            _viewModel.IsPaused = true;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        CopyText(LogText.SelectionLength > 0 ? LogText.SelectedText : LogText.Text);
    }

    private void OnCopyAll(object sender, RoutedEventArgs e)
    {
        CopyText(LogText.Text);
    }

    private void CopyText(string text)
    {
        try
        {
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
