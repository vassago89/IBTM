using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using System.Windows;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;

namespace IBTM.UI;

public partial class LogWindowViewModel : ObservableObject, IDisposable
{
    private readonly ListCollectionView _entries;
    private string _pausedText = "";
    private long _clearedThroughSequence;

    [ObservableProperty]
    public partial bool IsPaused { get; set; }
    [ObservableProperty]
    public partial string SelectedText { get; set; } = "";

    [ObservableProperty]
    public partial string? ClipboardError { get; set; }

    public LogWindowViewModel(ApplicationLog log)
    {
        ClearCommand = new RelayCommand(Clear);
        CopyCommand = new RelayCommand(Copy);
        CopyAllCommand = new RelayCommand(CopyAll);

        Log = log;
        // Register on the UI thread before creating the bound collection view.
        BindingOperations.EnableCollectionSynchronization(log.Entries, log.SyncRoot);
        _entries = new ListCollectionView(log.Entries)
        {
            Filter = IsVisible,
            SortDescriptions = { new(nameof(LogEntry.Sequence), ListSortDirection.Descending) },
        };
        ((INotifyCollectionChanged)_entries).CollectionChanged += OnEntriesChanged;
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

    public ApplicationLog Log { get; }

    private bool IsVisible(object item)
    {
        return ((LogEntry)item).Sequence > _clearedThroughSequence;
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

    public IRelayCommand ClearCommand { get; }

    private void Clear()
    {
        _clearedThroughSequence = Log.LatestSequence;
        _pausedText = "";
        _entries.Refresh();
        ClipboardError = null;
        OnPropertyChanged(nameof(Text));
    }

    public IRelayCommand CopyCommand { get; }

    private void Copy()
    {
        CopyText(SelectedText.Length > 0 ? SelectedText : Text);
    }

    public IRelayCommand CopyAllCommand { get; }

    private void CopyAll()
    {
        CopyText(Text);
    }

    private void CopyText(string text)
    {
        try
        {
            if (text.Length > 0)
                Clipboard.SetText(text);
            ClipboardError = null;
        }
        catch (ExternalException exception)
        {
            ClipboardError = $"Clipboard is unavailable: {exception.Message}";
        }
    }

    public void Dispose()
    {
        ((INotifyCollectionChanged)_entries).CollectionChanged -= OnEntriesChanged;
        _entries.DetachFromSourceCollection();
    }
}
