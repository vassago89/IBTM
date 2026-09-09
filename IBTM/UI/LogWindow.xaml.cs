using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using IBTM.Core;

namespace IBTM.UI;

public partial class LogWindow : Window
{
    private const int MaximumDisplayCharacters = 2 * 1024 * 1024;
    private readonly ApplicationLog _log;
    private readonly DispatcherTimer _refresh;
    private long _lastSequence;

    public LogWindow(ApplicationLog log)
    {
        _log = log;
        InitializeComponent();
        FilePathBox.Text = log.FilePath ?? "File logging is disabled in this session.";
        _refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => RefreshLog(), Dispatcher);
        RefreshLog();
    }

    protected override void OnClosed(EventArgs e)
    {
        _refresh.Stop();
        base.OnClosed(e);
    }

    private void RefreshLog()
    {
        StatusText.Text = _log.FileError is { } error ? $"FILE ERROR: {error}"
            : _log.FilePath is null ? "Recent messages are shown here; file logging is disabled."
            : "Recent messages are shown here; the log file contains the full session history.";
        if (PauseBox.IsChecked == true) return;
        var entries = _log.ReadAfter(_lastSequence);
        if (entries.Length == 0) return;
        var followTop = LogBox.SelectionLength == 0 && LogBox.VerticalOffset <= 2;
        var previousOffset = LogBox.VerticalOffset;
        var previousExtent = LogBox.ExtentHeight;
        var selectionStart = LogBox.SelectionStart;
        var selectionLength = LogBox.SelectionLength;
        var prefix = string.Join(Environment.NewLine, entries.Reverse().Select(entry => entry.Text)) + Environment.NewLine;
        LogBox.Text = prefix + LogBox.Text;
        _lastSequence = entries[^1].Sequence;
        if (LogBox.Text.Length > MaximumDisplayCharacters)
        {
            var end = LogBox.Text.LastIndexOf('\n', MaximumDisplayCharacters);
            LogBox.Text = LogBox.Text[..(end < 0 ? MaximumDisplayCharacters : end)];
        }
        if (followTop) LogBox.ScrollToHome();
        else
        {
            var start = Math.Min(selectionStart + prefix.Length, LogBox.Text.Length);
            LogBox.Select(start, Math.Min(selectionLength, LogBox.Text.Length - start));
            LogBox.UpdateLayout();
            LogBox.ScrollToVerticalOffset(previousOffset + Math.Max(0, LogBox.ExtentHeight - previousExtent));
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = LogBox.SelectionLength > 0 ? LogBox.SelectedText : LogBox.Text;
            if (text.Length > 0) Clipboard.SetText(text);
        }
        catch (ExternalException exception)
        {
            StatusText.Text = $"Clipboard is unavailable: {exception.Message}";
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _lastSequence = _log.LatestSequence;
        LogBox.Clear();
    }
}
