using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using IBTM.Ajin;
using IBTM.AlphaMotion;
using IBTM.Core;
using IBTM.Device;

namespace IBTM;

public sealed class PhysicalIoService(
    AlphaMotionController alphaMotion,
    AjinController ajin,
    IReadOnlyDictionary<InputIo, int> inputMap,
    IReadOnlyDictionary<OutputIo, OutputHardware> outputMap,
    MachineOptions options,
    ApplicationLog? log = null) : IIoService, IDisposable
{
    // Persisted logical address boundary, not the detected AlphaMotion board size.
    private const int AlphaMotionChannelCount = AlphaMotionController.ChannelCount;
    private static readonly InputIo[] Inputs = Enum.GetValues<InputIo>();
    private readonly bool[] _inputs = new bool[Inputs.Max(input => (int)input) + 1];
    private readonly bool[] _inputScan = new bool[Inputs.Max(input => (int)input) + 1];
    private readonly InputIo[] _changedInputs = new InputIo[Inputs.Length];
    private readonly uint[] _rtexInputs = new uint[ajin.RtexInputWordCount];
    private readonly Lock _lifecycleGate = new();
    // Notification history only: initial levels are not edges; recovered changes are.
    private bool _hasInputSnapshot;
    private volatile bool _ready;

    public event Action<InputIo, bool>? InputChanged;
    public event Action<OutputIo, bool>? OutputChanged;
    public event Action<Exception>? Faulted;
    public bool IsReady
    {
        get
        {
            return _ready;
        }
    }

    public int TimeoutMilliseconds
    {
        get
        {
            return options.TimeoutMilliseconds;
        }
    }

    public void Initialize()
    {
        lock (_lifecycleGate)
        {
            if (_ready)
            {
                return;
            }

            var stage = "AlphaMotion initialization";
            try
            {
                log?.Write(stage + " started.");
                alphaMotion.Initialize();
                log?.Write(stage + " completed.");
                stage = "AJIN AxlOpenNoReset initialization / DIO module validation";
                log?.Write(stage + " started.");
                ajin.Initialize();
                log?.Write(stage + " completed.");
                foreach (var input in Inputs)
                {
                    stage = $"Initial DI read: {input}, channel={inputMap[input]}";
                    var value = ReadInput(inputMap[input]);
                    _inputScan[(int)input] = value;
                    log?.Write($"{stage}: {(value ? "ON" : "OFF")}");
                }

                stage = "Initial DI cache update / change notification";
                PublishInputScan(_hasInputSnapshot);
            }
            catch (Exception exception)
            {
                _ready = false;
                log?.Error($"{stage} failed. Input feedback is unavailable. {exception.Message}");
                throw;
            }
        }
    }

    public void CheckReady()
    {
        lock (_lifecycleGate)
        {
            if (!_ready)
            {
                Initialize();
                return;
            }

            try
            {
                foreach (var channel in inputMap.Values.Distinct())
                {
                    _ = ReadInput(channel);
                }

                foreach (var output in outputMap.Values)
                {
                    _ = ReadOutput(output.Number);
                    if (output.OffNumber is { } offChannel)
                    {
                        _ = ReadOutput(offChannel);
                    }
                }
            }
            catch (Exception exception)
            {
                _ready = false;
                log?.Error($"Control I/O readiness check failed. {exception.Message}");
                throw;
            }
        }
    }

    public bool GetInput(InputIo input)
    {
        if (!_ready)
        {
            throw new IOException($"DI {input} is unavailable: no valid input scan.");
        }

        return Volatile.Read(ref _inputs[(int)input]);
    }

    public bool GetOutput(OutputIo output)
    {
        return ReadOutput(outputMap[output].Number);
    }

    public OutputFeedback? GetOutputFeedback(OutputIo output)
    {
        return outputMap[output].Feedback;
    }

    public void SetOutput(OutputIo output, bool value)
    {
        var mapping = outputMap[output];
        try
        {
            if (mapping.OffNumber is { } offChannel)
            {
                WriteOutput(value ? offChannel : mapping.Number, false);
                WriteOutput(value ? mapping.Number : offChannel, true);
            }
            else
            {
                WriteOutput(mapping.Number, value);
            }
        }
        catch (Exception exception)
        {
            log?.Error(
                $"DO {output}, channel={mapping.Number}, paired OFF={mapping.OffNumber}: write {(value ? "ON" : "OFF")} failed.",
                exception);
            throw;
        }

        log?.Write($"DO {output}, channel={mapping.Number}: {(value ? "ON" : "OFF")}");
        OutputChanged?.Invoke(output, value);
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            _ready = false;
        }
    }

    private bool ReadInput(int channel)
    {
        return channel < AlphaMotionChannelCount
            ? alphaMotion.ReadInput(channel)
            : ajin.ReadRtexInput(channel - AlphaMotionChannelCount);
    }

    private bool ReadOutput(int channel)
    {
        return channel < AlphaMotionChannelCount
            ? alphaMotion.ReadOutput(channel)
            : ajin.ReadRtexOutput(channel - AlphaMotionChannelCount);
    }

    private void WriteOutput(int channel, bool value)
    {
        if (channel < AlphaMotionChannelCount)
        {
            alphaMotion.WriteOutput(channel, value);
            return;
        }

        ajin.WriteRtexOutput(channel - AlphaMotionChannelCount, value);
    }

    public void RefreshInputs()
    {
        // Initialization/recovery and a scan cannot replace the cache concurrently.
        lock (_lifecycleGate)
        {
            if (!_ready)
                return;

            var stage = "AlphaMotion input read";
            try
            {
                var alphaInputs = alphaMotion.ReadInputs();
                stage = "AJIN input read";
                ajin.ReadRtexInputs(_rtexInputs);

                stage = "Input address mapping";
                foreach (var input in Inputs)
                {
                    _inputScan[(int)input] = ReadMonitoredInput(inputMap[input], alphaInputs);
                }

                stage = "Input cache update / change notification";
                PublishInputScan(notifyChanges: true);
            }
            catch (Exception exception)
            {
                _ready = false;
                log?.Error(
                    $"Input scan failed during {stage}. Inputs remain unavailable until initialization succeeds.",
                    exception);
                Faulted?.Invoke(exception);
                throw;
            }
        }
    }

    private void PublishInputScan(bool notifyChanges)
    {
        var changedCount = 0;
        foreach (var input in Inputs)
        {
            var index = (int)input;
            var value = _inputScan[index];
            if (Volatile.Read(ref _inputs[index]) == value)
            {
                continue;
            }

            Volatile.Write(ref _inputs[index], value);
            _changedInputs[changedCount++] = input;
        }

        _hasInputSnapshot = true;
        _ready = true;
        if (!notifyChanges)
            return;

        for (var index = 0; index < changedCount; index++)
        {
            var input = _changedInputs[index];
            log?.Write(
                $"DI {input}, channel={inputMap[input]}: {(_inputScan[(int)input] ? "ON" : "OFF")}");
            InputChanged?.Invoke(input, _inputScan[(int)input]);
        }
    }

    private bool ReadMonitoredInput(int channel, uint alphaInputs)
    {
        if (channel < AlphaMotionChannelCount)
        {
            return ((alphaInputs >> channel) & 1) != 0;
        }

        channel -= AlphaMotionChannelCount;
        return ((_rtexInputs[channel / 32] >> (channel % 32)) & 1) != 0;
    }
}
