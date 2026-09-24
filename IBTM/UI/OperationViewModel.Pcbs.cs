using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Storage;
using Microsoft.Extensions.Logging;

namespace IBTM.UI;

public partial class OperationViewModel
{
    private const int PcbHistoryPageSize = 100;
    private readonly MachineStore _store;
    private readonly PcbHistorySettings _historySettings;
    private readonly ILogger<OperationViewModel> _log;
    private string _pcbHistoryDirectory;
    private int _pcbHistoryLimit;
    private bool _pcbHistoryLoaded;

    public ObservableCollection<PcbRecord> PcbRecords { get; }
    public IAsyncRelayCommand LoadOlderPcbsCommand { get; }
    public IAsyncRelayCommand RetryPcbSaveCommand { get; }
    public IRelayCommand ClosePcbDetailsCommand { get; }
    public PcbDetailsViewModel PcbDetails { get; }

    [ObservableProperty]
    public partial PcbRecord? SelectedPcb { get; set; }

    partial void OnSelectedPcbChanged(PcbRecord? value)
    {
        PcbDetails.Record = value;
    }

    private void OnPcbImageSaved(long number)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnPcbImageSaved(number));
            return;
        }
        if (SelectedPcb?.Number == number)
            PcbDetails.RefreshImages();
    }

    [ObservableProperty]
    public partial string? PcbHistoryError { get; private set; }

    private async Task RetryPcbSaveAsync()
    {
        try
        {
            await Machine.PcbHistory.FlushAsync();
        }
        catch (Exception exception)
        {
            // The manager retains queued data and exposes SaveError directly to the view.
            _log.LogError(exception, "PCB save retry failed.");
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadOlderPcbsCommand))]
    public partial bool HasOlderPcbs { get; private set; }

    private async Task LoadOlderPcbsAsync(CancellationToken cancellationToken)
    {
        PcbHistoryError = null;
        var before = _pcbHistoryLoaded && PcbRecords.Count > 0 ? PcbRecords[^1].Number : (long?)null;
        var directory = _pcbHistoryDirectory;
        try
        {
            var records = await Task.Run(
                () => _store.LoadPcbs(directory, before, PcbHistoryPageSize), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (directory != _pcbHistoryDirectory)
                return;
            _pcbHistoryLimit = Math.Max(PcbHistoryPageSize,
                before.HasValue ? PcbRecords.Count + records.Count : PcbRecords.Count);
            foreach (var record in records)
                UpdatePcbRecord(record);
            HasOlderPcbs = records.Count == PcbHistoryPageSize;
            _pcbHistoryLoaded = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            PcbHistoryError = $"PCB history could not be loaded: {exception.Message}";
            _log.LogError(exception, "PCB history load failed for {Directory}.", directory);
        }
    }

    private void OnPcbSaved(PcbRecord record)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnPcbSaved(record));
            return;
        }
        UpdatePcbRecord(record);
    }

    private void UpdatePcbRecord(PcbRecord record)
    {
        var index = 0;
        while (index < PcbRecords.Count && PcbRecords[index].Number > record.Number)
            index++;
        var selected = SelectedPcb?.Number == record.Number;
        if (index < PcbRecords.Count && PcbRecords[index].Number == record.Number)
        {
            if (PcbRecords[index].UpdatedAt > record.UpdatedAt)
                return;
            PcbRecords[index] = record;
        }
        else
        {
            PcbRecords.Insert(index, record);
        }
        if (selected)
            SelectedPcb = record;
        if (PcbRecords.Count > _pcbHistoryLimit)
        {
            PcbRecords.RemoveAt(PcbRecords.Count - 1);
            HasOlderPcbs = true;
        }
    }

    private void ClosePcbDetails()
    {
        SelectedPcb = null;
    }
}
