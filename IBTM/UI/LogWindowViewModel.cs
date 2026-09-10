using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;

namespace IBTM.UI;

public partial class LogWindowViewModel : ObservableObject, IDisposable
{
    private readonly ApplicationLog _log;
    private readonly ListCollectionView _entries;
    private string _pausedText = "";
    private long _clearedThroughSequence;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Status))]
    private string? _clipboardError;

    public LogWindowViewModel(ApplicationLog log)
    {
        _log = log;
        // Register on the UI thread before creating the bound collection view.
        BindingOperations.EnableCollectionSynchronization(log.Entries, log.SyncRoot);
        _entries = new ListCollectionView(log.Entries)
        {
            Filter = IsVisible,
            SortDescriptions = { new(nameof(LogEntry.Sequence), ListSortDirection.Descending) },
        };
        ((INotifyCollectionChanged)_entries).CollectionChanged += OnEntriesChanged;
        log.PropertyChanged += OnLogChanged;
    }

    public string Text
    {
        get
        {
            return IsPaused
                ? _pausedText
                : string.Join(Environment.NewLine, _entries.Cast<LogEntry>().Select(entry => entry.Text));
        }
    }

    public string FilePath
    {
        get
        {
            return _log.FilePath ?? "File logging is disabled in this session.";
        }
    }

    public string Status
    {
        get
        {
            if (ClipboardError is not null)
                return ClipboardError;
            if (_log.FileError is { } error)
                return $"FILE ERROR: {error}";
            if (_log.FilePath is null)
                return "Recent messages are shown here; file logging is disabled.";

            return "Recent messages are shown here; the log file contains the full session history.";
        }
    }

    private bool IsVisible(object item)
    {
        return ((LogEntry)item).Sequence > _clearedThroughSequence;
    }

    private void OnLogChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ApplicationLog.FileError))
            OnPropertyChanged(nameof(Status));
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (!IsPaused)
            OnPropertyChanged(nameof(Text));
    }

    partial void OnIsPausedChanged(bool value)
    {
        _pausedText = value
            ? string.Join(Environment.NewLine, _entries.Cast<LogEntry>().Select(entry => entry.Text))
            : "";
        OnPropertyChanged(nameof(Text));
    }

    [RelayCommand]
    private void Clear()
    {
        _clearedThroughSequence = _log.LatestSequence;
        _pausedText = "";
        _entries.Refresh();
        ClipboardError = null;
        OnPropertyChanged(nameof(Text));
    }

    public void Dispose()
    {
        _log.PropertyChanged -= OnLogChanged;
        ((INotifyCollectionChanged)_entries).CollectionChanged -= OnEntriesChanged;
        _entries.DetachFromSourceCollection();
    }
}
