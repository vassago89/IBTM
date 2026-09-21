using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Storage;

namespace IBTM.UI;

public partial class OperationViewModel
{
    private const int PcbHistoryPageSize = 100;
    private readonly MachineStore _store;
    private readonly PcbHistorySettings _historySettings;
    private string _pcbHistoryDirectory;
    private int _pcbHistoryLimit;

    public ObservableCollection<PcbRecord> PcbRecords { get; }
    public IAsyncRelayCommand LoadOlderPcbsCommand { get; }
    public IRelayCommand ClosePcbDetailsCommand { get; }

    [ObservableProperty]
    public partial PcbRecord? SelectedPcb { get; set; }

    [ObservableProperty]
    public partial string? PcbHistoryError { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadOlderPcbsCommand))]
    public partial bool HasOlderPcbs { get; private set; } = true;

    private async Task LoadOlderPcbsAsync(CancellationToken cancellationToken)
    {
        PcbHistoryError = null;
        var before = PcbRecords.Count == 0 ? (long?)null : PcbRecords[^1].Number;
        var directory = _pcbHistoryDirectory;
        try
        {
            var records = await Task.Run(
                () => _store.LoadPcbs(directory, before, PcbHistoryPageSize), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (directory != _pcbHistoryDirectory)
                return;
            _pcbHistoryLimit = PcbRecords.Count + records.Count;
            _pcbHistoryLimit = Math.Max(PcbHistoryPageSize, _pcbHistoryLimit);
            foreach (var record in records)
                UpdatePcbRecord(record);
            HasOlderPcbs = records.Count == PcbHistoryPageSize;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            PcbHistoryError = $"PCB history could not be loaded: {exception.Message}";
            Trace.TraceError("{0}", exception);
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
