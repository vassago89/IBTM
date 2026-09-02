using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Device;

namespace IBTM.UI;

public enum OutputFeedbackState
{
    [Description("Waiting")]
    Waiting,

    [Description("Matched")]
    Matched,

    [Description("Timeout")]
    Timeout,
}

public partial class OutputWindow : Window
{
    private readonly IIoService _io;

    public OutputWindow(IIoService io)
    {
        _io = io;
        Rows = Enum.GetValues<OutputIo>()
            .Select(output => new OutputControlRow(
                io,
                output,
                io.GetOutputFeedback(output)))
            .ToArray();

        InitializeComponent();
        DataContext = this;
        _io.InputChanged += OnInputChanged;
        _io.OutputChanged += OnOutputChanged;
    }

    public OutputControlRow[] Rows { get; }

    protected override void OnClosed(EventArgs e)
    {
        _io.InputChanged -= OnInputChanged;
        _io.OutputChanged -= OnOutputChanged;
        base.OnClosed(e);
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        foreach (var row in Rows)
        {
            row.Refresh();
        }
    }

    private void OnInputChanged(InputIo input, bool value) =>
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var row in Rows)
            {
                if (row.UsesFeedback(input))
                {
                    row.SetInput(input, value);
                }
            }
        });

    private void OnOutputChanged(OutputIo output, bool value) =>
        Dispatcher.BeginInvoke(() => Rows[(int)output].SetOutput(value));
}

public sealed partial class OutputControlRow : ObservableObject
{
    private readonly IIoService _io;
    private readonly OutputFeedback? _feedback;
    private bool _timedOut;
    private bool _outputOn;
    private bool _onInput;
    private bool _offInput;

    public OutputControlRow(
        IIoService io,
        OutputIo output,
        OutputFeedback? feedback)
    {
        _io = io;
        _feedback = feedback;
        Output = output;
        ReadHardware();
    }

    public OutputIo Output { get; }
    public bool HasFeedback => _feedback is not null;
    public bool OutputOn => _outputOn;
    public InputIo? FeedbackInput =>
        _feedback is null
            ? null
            : OutputOn ? _feedback.OnInput : _feedback.OffInput;
    public bool InputOn =>
        _feedback is not null && (OutputOn ? _onInput : _offInput);
    public OutputFeedbackState FeedbackState =>
        _timedOut
            ? OutputFeedbackState.Timeout
            : InputOn
                ? OutputFeedbackState.Matched
                : OutputFeedbackState.Waiting;

    [RelayCommand]
    private async Task ToggleAsync()
    {
        var value = !_io.GetOutput(Output);
        ClearTimeout();

        try
        {
            _io.SetOutput(Output, value);

            if (HasFeedback)
            {
                await _io.WaitForOutputFeedbackAsync(Output, value);
            }
        }
        catch (IoTimeoutException)
        {
            MarkTimeout();
        }

    }

    private void ClearTimeout()
    {
        _timedOut = false;
        OnPropertyChanged(nameof(FeedbackState));
    }

    private void MarkTimeout()
    {
        _timedOut = true;
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
        if (_feedback is null)
        {
            return;
        }

        if (_feedback.OnInput == input)
        {
            _onInput = value;
        }
        else
        {
            _offInput = value;
        }

        OnPropertyChanged(nameof(InputOn));
        OnPropertyChanged(nameof(FeedbackState));
    }

    public bool UsesFeedback(InputIo input) =>
        _feedback is not null
        && (_feedback.OnInput == input || _feedback.OffInput == input);

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
