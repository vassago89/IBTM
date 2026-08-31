using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.Virtual;

public sealed class VirtualIoService(
    IReadOnlyDictionary<OutputIo, OutputHardware> outputs,
    MachineOptions options) : IIoService
{
    private const int FeedbackDelayMilliseconds = 200;

    private readonly bool[] _inputs = new bool[
        Enum.GetValues<InputIo>().Max(input => (int)input) + 1];
    private readonly bool[] _outputs = new bool[
        Enum.GetValues<OutputIo>().Max(output => (int)output) + 1];
    private readonly int[] _feedbackVersions = new int[
        Enum.GetValues<OutputIo>().Max(output => (int)output) + 1];
    private bool _connected = true;
    public event Action<InputIo, bool>? InputChanged;
    public event Action<OutputIo, bool>? OutputChanged;
    public event Action? Faulted;
    public bool IsReady => _connected;
    public int TimeoutMilliseconds => options.TimeoutMilliseconds;
    internal event Action<OutputIo, bool>? OutputApplied;

    public void Initialize()
    {
        CheckReady();
        foreach (var feedback in outputs.Values
                     .Select(output => output.Feedback)
                     .OfType<OutputFeedback>())
        {
            if (GetInput(feedback.OnInput)
                || GetInput(feedback.OffInput))
            {
                continue;
            }

            SetInput(feedback.OffInput, true);
        }
    }

    public void CheckReady()
    {
        if (!_connected)
        {
            throw new InvalidOperationException("Virtual control I/O is disconnected.");
        }
    }

    public void SetConnected(bool connected)
    {
        if (_connected == connected)
        {
            return;
        }

        _connected = connected;
        if (!connected)
        {
            Faulted?.Invoke();
        }
    }

    public bool GetInput(InputIo input) => _inputs[(int)input];

    public bool GetOutput(OutputIo output) => _outputs[(int)output];

    public OutputFeedback? GetOutputFeedback(OutputIo output) =>
        outputs[output].Feedback;

    public void SetInput(InputIo input, bool value)
    {
        var index = (int)input;
        if (_inputs[index] == value)
        {
            return;
        }

        _inputs[index] = value;
        InputChanged?.Invoke(input, value);
    }

    public void SetOutput(OutputIo output, bool value)
    {
        var index = (int)output;
        if (_outputs[index] == value)
        {
            return;
        }

        _outputs[index] = value;
        OutputChanged?.Invoke(output, value);
        var feedbackVersion = Interlocked.Increment(
            ref _feedbackVersions[index]);
        if (outputs[output].Feedback is { } feedback)
        {
            _ = ApplyFeedbackAsync(
                output,
                value,
                feedback,
                feedbackVersion);
        }
    }

    private async Task ApplyFeedbackAsync(
        OutputIo output,
        bool value,
        OutputFeedback feedback,
        int version)
    {
        await Task.Delay(FeedbackDelayMilliseconds).ConfigureAwait(false);
        if (_feedbackVersions[(int)output] != version)
        {
            return;
        }

        var expected = value ? feedback.OnInput : feedback.OffInput;
        SetInput(value ? feedback.OffInput : feedback.OnInput, false);
        SetInput(expected, true);
        OutputApplied?.Invoke(output, value);
    }
}
