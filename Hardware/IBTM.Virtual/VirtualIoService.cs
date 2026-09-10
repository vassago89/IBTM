using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.Virtual;

public sealed class VirtualIoService(
    IReadOnlyDictionary<OutputIo, OutputHardware> outputs,
    MachineOptions options) : IIoService, INotifyPropertyChanged
{
    private const int FeedbackDelayMilliseconds = 200;

    private readonly bool[] _inputs = CreateInitialInputs();
    private readonly bool[] _outputs = CreateInitialOutputs();
    private readonly int[] _feedbackVersions = new int[Enum.GetValues<OutputIo>().Max(
        output => (int)output) + 1];
    private readonly Lock _responseGate = new();
    private bool _autoResponseEnabled = true;
    private int _autoResponseVersion;
    private bool _connected = true;
    public event Action<InputIo, bool>? InputChanged;
    public event Action<OutputIo, bool>? OutputChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<Exception>? Faulted;
    public bool IsReady
    {
        get
        {
            return _connected;
        }
    }

    public int TimeoutMilliseconds
    {
        get
        {
            return options.TimeoutMilliseconds;
        }
    }

    internal event Action<OutputIo, bool>? OutputApplied;
    internal event Action? FeedbackSynchronized;

    private static bool[] CreateInitialInputs()
    {
        var inputs = new bool[Enum.GetValues<InputIo>().Max(input => (int)input) + 1];
        // Start in MANUAL using the same raw selector polarity as the machine.
        inputs[(int)InputIo.AutoMode] = true;
        // All six doors start closed; opening a door switches its raw input OFF.
        inputs[(int)InputIo.Door1Open] = true;
        inputs[(int)InputIo.Door2Open] = true;
        inputs[(int)InputIo.Door3Open] = true;
        inputs[(int)InputIo.Door4Open] = true;
        inputs[(int)InputIo.Door5Open] = true;
        inputs[(int)InputIo.Door6Open] = true;
        inputs[(int)InputIo.AirPressureHigh] = true;
        // Virtual equipment starts raised with an open NG gripper.
        inputs[(int)InputIo.NgCarrierPickupUp] = true;
        inputs[(int)InputIo.NgCarrierGripperOpen] = true;
        inputs[(int)InputIo.NgShuttleUp] = true;
        return inputs;
    }

    private static bool[] CreateInitialOutputs()
    {
        var values = new bool[Enum.GetValues<OutputIo>().Max(output => (int)output) + 1];
        values[(int)OutputIo.NgCarrierPickupUp] = true;
        values[(int)OutputIo.NgCarrierGripperOpen] = true;
        values[(int)OutputIo.NgShuttleUp] = true;
        return values;
    }

    public bool AutoResponseEnabled
    {
        get
        {
            return Volatile.Read(ref _autoResponseEnabled);
        }

        set
        {
            lock (_responseGate)
            {
                if (_autoResponseEnabled == value)
                {
                    return;
                }

                _autoResponseEnabled = value;
                var responseVersion = ++_autoResponseVersion;
                if (value)
                {
                    foreach (var (output, hardware) in outputs)
                    {
                        if (hardware.Feedback is { } feedback)
                        {
                            _ = ApplyFeedbackAsync(
                                output,
                                GetOutput(output),
                                feedback,
                                _feedbackVersions[(int)output],
                                responseVersion,
                                notifyApplied: false);
                        }
                    }
                }

                PropertyChanged?.Invoke(this, new(nameof(AutoResponseEnabled)));
            }
        }
    }

    internal int AutoResponseVersion
    {
        get
        {
            return Volatile.Read(ref _autoResponseVersion);
        }
    }

    internal void ApplyAutoResponse(int version, Action response)
    {
        lock (_responseGate)
        {
            if (!_autoResponseEnabled || version != _autoResponseVersion)
            {
                return;
            }

            response();
        }
    }

    public void Initialize()
    {
        CheckReady();
        ApplyAutoResponse(
            AutoResponseVersion,
            () =>
            {
                foreach (var feedback in outputs.Values.Select(output => output.Feedback).OfType<OutputFeedback>())
                {
                    if (GetInput(feedback.OnInput) || GetInput(feedback.OffInput))
                    {
                        continue;
                    }

                    SetInput(feedback.OffInput, true);
                }
            });
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
            Faulted?.Invoke(new InvalidOperationException("Virtual control I/O is disconnected."));
        }
    }

    public bool GetInput(InputIo input)
    {
        return _inputs[(int)input];
    }

    public bool GetOutput(OutputIo output)
    {
        return _outputs[(int)output];
    }

    public OutputFeedback? GetOutputFeedback(OutputIo output)
    {
        return outputs[output].Feedback;
    }

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
        lock (_responseGate)
        {
            var index = (int)output;
            if (_outputs[index] == value)
            {
                return;
            }

            _outputs[index] = value;
            var feedbackVersion = ++_feedbackVersions[index];
            var responseVersion = _autoResponseVersion;
            OutputChanged?.Invoke(output, value);
            if (_autoResponseEnabled && outputs[output].Feedback is { } feedback)
            {
                _ = ApplyFeedbackAsync(output, value, feedback, feedbackVersion, responseVersion);
            }
        }
    }

    private async Task ApplyFeedbackAsync(
        OutputIo output,
        bool value,
        OutputFeedback feedback,
        int version,
        int responseVersion,
        bool notifyApplied = true)
    {
        await Task.Delay(FeedbackDelayMilliseconds).ConfigureAwait(false);
        ApplyAutoResponse(
            responseVersion,
            () =>
            {
                if (_feedbackVersions[(int)output] != version)
                {
                    return;
                }

                var expected = value ? feedback.OnInput : feedback.OffInput;
                SetInput(value ? feedback.OffInput : feedback.OnInput, false);
                SetInput(expected, true);
                if (notifyApplied)
                {
                    OutputApplied?.Invoke(output, value);
                }
                else
                {
                    FeedbackSynchronized?.Invoke();
                }
            });
    }
}
