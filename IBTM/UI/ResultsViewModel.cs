using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Storage;
using Microsoft.Extensions.Logging;

namespace IBTM.UI;

public partial class ResultsViewModel : ObservableObject
{
    private readonly MachineStore _store;
    private readonly PcbHistorySettings _historySettings;
    private readonly ILogger<ResultsViewModel> _log;
    private bool _shuttingDown;

    public ResultsViewModel(MachineStore store, PcbHistorySettings history,
        PcbResultsViewModel pcbDetails, ILogger<ResultsViewModel> log)
    {
        _store = store;
        _historySettings = history;
        _log = log;
        PcbDetails = pcbDetails;
        Records = [];
        RefreshHistoryCommand = new AsyncRelayCommand(RefreshHistoryAsync);
        ClearHistoryFilterCommand = new RelayCommand(ClearHistoryFilter);
        RefreshHistoryCommand.PropertyChanged += OnCommandChanged;
        HistoryDirectory = history.Directory;
    }

    public PcbResultsViewModel PcbDetails { get; }
    public ObservableCollection<PcbRecord> Records { get; }
    public IAsyncRelayCommand RefreshHistoryCommand { get; }
    public IRelayCommand ClearHistoryFilterCommand { get; }
    public bool IsIdle => !_shuttingDown && !RefreshHistoryCommand.IsRunning;

    [ObservableProperty] public partial DateTime? HistoryDate { get; set; }
    [ObservableProperty] public partial string? HistorySearch { get; set; }
    [ObservableProperty] public partial AssemblyResult? HistoryResult { get; set; }
    [ObservableProperty] public partial string HistoryDirectory { get; set; }
    [ObservableProperty] public partial PcbRecord? SelectedRecord { get; set; }
    [ObservableProperty] public partial string? Error { get; private set; }

    public void Activate()
    {
        if (HistoryDirectory != _historySettings.Directory)
            HistoryDirectory = _historySettings.Directory;
        if (IsIdle && Records.Count == 0)
            _ = RefreshHistoryCommand.ExecuteAsync(null);
    }

    public Task ShutdownAsync()
    {
        _shuttingDown = true;
        OnPropertyChanged(nameof(IsIdle));
        return CommandShutdown.CancelAndWaitAsync([RefreshHistoryCommand]);
    }

    private void OnCommandChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning))
            OnPropertyChanged(nameof(IsIdle));
    }

    public bool IsRecordVisible(PcbRecord record)
    {
        var search = HistorySearch?.Trim();
        return (HistoryDate is null || record.CreatedAt.LocalDateTime.Date == HistoryDate.Value.Date)
            && (HistoryResult is null || record.Result == HistoryResult)
            && (string.IsNullOrEmpty(search)
                || record.PcbBarcode?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
    }

    partial void OnHistoryDateChanged(DateTime? value)
    {
        ClearHiddenSelection();
    }

    partial void OnHistorySearchChanged(string? value)
    {
        ClearHiddenSelection();
    }

    partial void OnHistoryResultChanged(AssemblyResult? value)
    {
        ClearHiddenSelection();
    }

    private void ClearHistoryFilter()
    {
        HistoryDate = null;
        HistorySearch = null;
        HistoryResult = null;
    }

    private void ClearHiddenSelection()
    {
        if (SelectedRecord is not null && !IsRecordVisible(SelectedRecord))
            SelectedRecord = null;
    }

    partial void OnHistoryDirectoryChanged(string value)
    {
        RefreshHistoryCommand.Cancel();
        ClearHistory();
        Error = null;
    }

    private void ClearHistory()
    {
        Records.Clear();
        SelectedRecord = null;
    }

    private async Task RefreshHistoryAsync(CancellationToken token)
    {
        if (_shuttingDown)
            return;
        var selected = SelectedRecord;
        ClearHistory();
        Error = null;
        var directory = HistoryDirectory;
        try
        {
            var records = await Task.Run(() => _store.LoadPcbs(directory), token);
            if (token.IsCancellationRequested)
                return;
            foreach (var record in records)
                Records.Add(record);
            if (selected is not null)
                SelectedRecord = Records.FirstOrDefault(record => record.Number == selected.Number
                    && record.DatabaseFile == selected.DatabaseFile && IsRecordVisible(record));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested)
                Error = exception.Message;
            _log.LogError(exception, "PCB history load failed for {Directory}.", directory);
        }
    }
}
