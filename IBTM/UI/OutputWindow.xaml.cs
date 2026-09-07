using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public enum OutputFeedbackState
{
    [Description("Not matched")]
    NotMatched,

    [Description("Matched")]
    Matched,

    [Description("Waiting")]
    Waiting,

    [Description("Input conflict")]
    Conflict,

    [Description("Timeout")]
    Timeout,
}

public partial class OutputWindow : Window, INotifyPropertyChanged
{
    private readonly IIoService _io;
    private bool _closing;
    private bool _shutdownCompleted;
    private string _searchText = string.Empty;
    private KeyValuePair<HardwareArea?, string> _selectedArea;

    public OutputWindow(
        IIoService io,
        IReadOnlyDictionary<OutputIo, HardwareArea> outputAreas)
    {
        _io = io;
        Rows = Enum.GetValues<OutputIo>()
            .Select(output => new OutputControlRow(
                io,
                output,
                outputAreas[output],
                io.GetOutputFeedback(output)))
            .ToArray();
        Areas =
        [
            new(null, "All Units"),
            .. outputAreas.Values.Distinct().Order().Select(area =>
                new KeyValuePair<HardwareArea?, string>(area, area.GetDescription())),
        ];
        _selectedArea = Areas[0];
        FilteredRows = CollectionViewSource.GetDefaultView(Rows);
        FilteredRows.SortDescriptions.Add(new(
            nameof(OutputControlRow.Area),
            ListSortDirection.Ascending));
        FilteredRows.SortDescriptions.Add(new(
            nameof(OutputControlRow.Section),
            ListSortDirection.Ascending));
        FilteredRows.SortDescriptions.Add(new(
            nameof(OutputControlRow.Output),
            ListSortDirection.Ascending));
        FilteredRows.GroupDescriptions.Add(new PropertyGroupDescription(
            nameof(OutputControlRow.Area)));
        FilteredRows.GroupDescriptions.Add(new PropertyGroupDescription(
            nameof(OutputControlRow.Section)));

        bool Matches(Enum? signal) => signal is not null
            && (signal.GetDescription().Contains(
                    _searchText,
                    StringComparison.OrdinalIgnoreCase)
                || signal.ToString().Contains(
                    _searchText,
                    StringComparison.OrdinalIgnoreCase));

        FilteredRows.Filter = item =>
        {
            var row = (OutputControlRow)item;
            return (_selectedArea.Key is null || row.Area == _selectedArea.Key)
                && (Matches(row.Output)
                    || Matches(row.OnInput)
                    || Matches(row.OffInput));
        };

        InitializeComponent();
        DataContext = this;
        _io.InputChanged += OnInputChanged;
        _io.OutputChanged += OnOutputChanged;
        foreach (var row in Rows)
        {
            row.Refresh();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public OutputControlRow[] Rows { get; }
    public ICollectionView FilteredRows { get; }
    public KeyValuePair<HardwareArea?, string>[] Areas { get; }
    public string SearchText
    {
        get => _searchText;
        set
        {
            _searchText = value;
            FilteredRows.Refresh();
            PropertyChanged?.Invoke(this, new(nameof(SearchText)));
        }
    }
    public KeyValuePair<HardwareArea?, string> SelectedArea
    {
        get => _selectedArea;
        set
        {
            _selectedArea = value;
            FilteredRows.Refresh();
            PropertyChanged?.Invoke(this, new(nameof(SelectedArea)));
        }
    }

    public async Task ShutdownAsync()
    {
        var pending = CommandShutdown.Capture(
            Rows.Select(row => row.ToggleCommand).ToArray());
        IsEnabled = false;
        try
        {
            foreach (var row in Rows)
            {
                row.ToggleCommand.Cancel();
            }
        }
        finally
        {
            await CommandShutdown.WaitAsync(pending);
        }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_shutdownCompleted)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        base.OnClosing(e);
        if (_closing)
        {
            return;
        }

        _closing = true;
        try
        {
            await ShutdownAsync();
            _shutdownCompleted = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
        catch (Exception exception)
        {
            _closing = false;
            IsEnabled = true;
            MessageBox.Show(this, exception.Message, "Output Shutdown Failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _io.InputChanged -= OnInputChanged;
        _io.OutputChanged -= OnOutputChanged;
        base.OnClosed(e);
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        foreach (var row in Rows)
        {
            row.Refresh();
        }
    }

    private void OnInputChanged(InputIo input, bool _) =>
        Dispatcher.BeginInvoke(() =>
        {
            var value = _io.GetInput(input);
            foreach (var row in Rows)
            {
                row.SetInput(input, value);
            }
        });

    private void OnOutputChanged(OutputIo output, bool _) =>
        Dispatcher.BeginInvoke(() => Rows[(int)output].SetOutput(_io.GetOutput(output)));
}

public sealed partial class OutputControlRow : ObservableObject
{
    private readonly IIoService _io;
    private readonly OutputFeedback? _feedback;
    private bool _timedOut;
    private bool _waitingForFeedback;
    private bool _outputOn;
    private bool _onInput;
    private bool _offInput;

    public OutputControlRow(
        IIoService io,
        OutputIo output,
        HardwareArea area,
        OutputFeedback? feedback)
    {
        _io = io;
        _feedback = feedback;
        Output = output;
        Area = area;
        Section = output.GetIoSection();
    }

    public OutputIo Output { get; }
    public HardwareArea Area { get; }
    public IoSection? Section { get; }
    public bool HasFeedback => _feedback is not null;
    public bool OutputOn => _outputOn;
    public InputIo? OnInput => _feedback?.OnInput;
    public InputIo? OffInput => _feedback?.OffInput;
    public bool OnInputOn => _onInput;
    public bool OffInputOn => _offInput;
    private bool ExpectedInputOn =>
        _feedback is not null && (OutputOn ? _onInput : _offInput);
    public OutputFeedbackState FeedbackState =>
        _timedOut
            ? OutputFeedbackState.Timeout
            : _onInput && _offInput
                ? OutputFeedbackState.Conflict
                : ExpectedInputOn
                    ? OutputFeedbackState.Matched
                    : _waitingForFeedback
                        ? OutputFeedbackState.Waiting
                        : OutputFeedbackState.NotMatched;

    [RelayCommand]
    private async Task ToggleAsync(CancellationToken cancellationToken)
    {
        var value = !_io.GetOutput(Output);
        ClearTimeout();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _io.SetOutput(Output, value);

            if (HasFeedback)
            {
                SetWaiting(true);
                await _io.WaitForOutputFeedbackAsync(
                    Output, value, cancellationToken);
            }
        }
        catch (IoTimeoutException)
        {
            _timedOut = true;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetWaiting(false);
        }
    }

    private void ClearTimeout()
    {
        _timedOut = false;
        OnPropertyChanged(nameof(FeedbackState));
    }

    private void SetWaiting(bool value)
    {
        _waitingForFeedback = value;
        OnPropertyChanged(nameof(FeedbackState));
    }

    public void Refresh()
    {
        _timedOut = false;
        ReadHardware();
        OnPropertyChanged(string.Empty);
    }

    public void SetOutput(bool value)
    {
        _outputOn = value;
        OnPropertyChanged(string.Empty);
    }

    public void SetInput(InputIo input, bool value)
    {
        if (_feedback?.OnInput == input)
        {
            _onInput = value;
        }
        else if (_feedback?.OffInput == input)
        {
            _offInput = value;
        }
        else
        {
            return;
        }

        OnPropertyChanged(input == _feedback.OnInput
            ? nameof(OnInputOn)
            : nameof(OffInputOn));
        OnPropertyChanged(nameof(FeedbackState));
    }

    private void ReadHardware()
    {
        _outputOn = _io.GetOutput(Output);
        if (_feedback is null)
        {
            return;
        }

        _onInput = _io.GetInput(_feedback.OnInput);
        _offInput = _io.GetInput(_feedback.OffInput);
    }
}
