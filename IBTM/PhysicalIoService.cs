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

public sealed class PhysicalIoService : IIoService, IDisposable
{
    private readonly AlphaMotionController _alphaMotion;
    private readonly AjinController _ajin;
    private readonly IReadOnlyDictionary<InputIo, int> _inputMap;
    private readonly IReadOnlyDictionary<OutputIo, OutputHardware> _outputMap;
    private readonly MachineOptions _options;
    private readonly ApplicationLog? _log;
    // Persisted logical address boundary, not the detected AlphaMotion board size.
    private const int AlphaMotionChannelCount = AlphaMotionController.ChannelCount;
    private readonly InputIo[] _mappedInputs;
    private readonly bool[] _inputs;
    private readonly bool[] _inputScan;
    private readonly InputIo[] _changedInputs;
    private readonly uint[] _rtexInputs;
    private readonly Lock _lifecycleGate;
    // Notification history only: initial levels are not edges; recovered changes are.
    private bool _hasInputSnapshot;
    private volatile bool _ready;

    public PhysicalIoService(
        AlphaMotionController alphaMotion,
        AjinController ajin,
        IReadOnlyDictionary<InputIo, int> inputMap,
        IReadOnlyDictionary<OutputIo, OutputHardware> outputMap,
        MachineOptions options,
        ApplicationLog? log = null)
    {
        _alphaMotion = alphaMotion;
        _ajin = ajin;
        _inputMap = inputMap;
        _outputMap = outputMap;
        _options = options;
        _log = log;
        _mappedInputs = _inputMap
            .Where(mapping => mapping.Value >= 0)
            .Select(mapping => mapping.Key)
            .ToArray();
        _inputs = new bool[Enum.GetValues<InputIo>().Max(input => (int)input) + 1];
        _inputScan = new bool[Enum.GetValues<InputIo>().Max(input => (int)input) + 1];
        _changedInputs = new InputIo[_inputMap.Count];
        _rtexInputs = new uint[_ajin.RtexInputWordCount];
        _lifecycleGate = new();
    }

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
            return _options.TimeoutMilliseconds;
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
                _log?.Write(stage + " started.");
                _alphaMotion.Initialize();
                _log?.Write(stage + " completed.");
                stage = "AJIN AxlOpen initialization / DIO module validation";
                _log?.Write(stage + " started.");
                _ajin.Initialize();
                _log?.Write(stage + " completed.");
                foreach (var input in _mappedInputs)
                {
                    stage = $"Initial DI read: {input}, channel={_inputMap[input]}";
                    var value = ReadInput(_inputMap[input]);
                    _inputScan[(int)input] = value;
                    _log?.Write($"{stage}: {(value ? "ON" : "OFF")}");
                }

                stage = "Initial DI cache update / change notification";
                PublishInputScan(_hasInputSnapshot);
            }
            catch (Exception exception)
            {
                _ready = false;
                _log?.Error($"{stage} failed. Input feedback is unavailable. {exception.Message}");
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
                foreach (var channel in _mappedInputs.Select(input => _inputMap[input]).Distinct())
                {
                    _ = ReadInput(channel);
                }

                foreach (var output in _outputMap.Values)
                {
                    _ = ReadOutput(output.Number);
                    if (output.OffNumber is >= 0 and var offChannel)
                    {
                        _ = ReadOutput(offChannel);
                    }
                }
            }
            catch (Exception exception)
            {
                _ready = false;
                _log?.Error($"Control I/O readiness check failed. {exception.Message}");
                throw;
            }
        }
    }

    public bool GetInput(InputIo input)
    {
        if (!_inputMap.TryGetValue(input, out var channel) || channel < 0)
            throw new IOException($"DI {input} is unavailable: no configured input address.");
        if (!_ready)
        {
            throw new IOException($"DI {input} is unavailable: no valid input scan.");
        }

        return Volatile.Read(ref _inputs[(int)input]);
    }

    public bool GetOutput(OutputIo output)
    {
        return ReadOutput(_outputMap[output].Number);
    }

    public OutputFeedback? GetOutputFeedback(OutputIo output)
    {
        return _outputMap[output].Feedback;
    }

    public void SetOutput(OutputIo output, bool value)
    {
        var mapping = _outputMap[output];
        try
        {
            // Validate both coils before any write; a missing OFF address is not a single-coil valve.
            if (mapping.Number < 0 || mapping.OffNumber is < 0)
                throw new IOException($"DO {output} is unavailable: configure both output addresses before operation.");
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
            _log?.Error(
                $"DO {output}, channel={mapping.Number}, paired OFF={mapping.OffNumber}: write {(value ? "ON" : "OFF")} failed.",
                exception);
            throw;
        }

        _log?.Write($"DO {output}, channel={mapping.Number}: {(value ? "ON" : "OFF")}");
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
            ? _alphaMotion.ReadInput(channel)
            : _ajin.ReadRtexInput(channel - AlphaMotionChannelCount);
    }

    private bool ReadOutput(int channel)
    {
        return channel < AlphaMotionChannelCount
            ? _alphaMotion.ReadOutput(channel)
            : _ajin.ReadRtexOutput(channel - AlphaMotionChannelCount);
    }

    private void WriteOutput(int channel, bool value)
    {
        if (channel < AlphaMotionChannelCount)
        {
            _alphaMotion.WriteOutput(channel, value);
            return;
        }

        _ajin.WriteRtexOutput(channel - AlphaMotionChannelCount, value);
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
                var alphaInputs = _alphaMotion.ReadInputs();
                stage = "AJIN input read";
                _ajin.ReadRtexInputs(_rtexInputs);

                stage = "Input address mapping";
                foreach (var input in _mappedInputs)
                {
                    _inputScan[(int)input] = ReadMonitoredInput(_inputMap[input], alphaInputs);
                }

                stage = "Input cache update / change notification";
                PublishInputScan(notifyChanges: true);
            }
            catch (Exception exception)
            {
                _ready = false;
                _log?.Error(
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
        foreach (var input in _mappedInputs)
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
            _log?.Write(
                $"DI {input}, channel={_inputMap[input]}: {(_inputScan[(int)input] ? "ON" : "OFF")}");
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
