using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using IBTM.Core;

namespace IBTM.Ajin;

public sealed class AjinController(AjinSettings settings, ApplicationLog? log = null) : IDisposable
{
    // Keep the existing logical address slots; a 16-point module uses only bits 0..15.
    private const int RtexChannelCountPerModule = 32;
    private readonly uint _interruptNumber = GetInterruptNumber(settings.InterruptNumber);
    private readonly int[] _inputModules = CaptureModules(settings.RtexInputModules, nameof(settings.RtexInputModules));
    private readonly int[] _outputModules = CaptureModules(settings.RtexOutputModules, nameof(settings.RtexOutputModules));
    private readonly Lock _gate = new();
    private int[] _inputCounts = [];
    private int[] _outputCounts = [];
    private bool _initialized;

    public int RtexInputWordCount => _inputModules.Length;

    public void Initialize()
    {
        lock (_gate)
        {
            if (_initialized) return;

            // Field-test existing hardware settings. Never fall back to a resetting open.
            log?.Write($"AJIN opening with AxlOpenNoReset(interrupt={_interruptNumber}); .mot loading is skipped. Motion still applies the application's pulse and acceleration units.");
            var openResult = CAXL.AxlOpenNoReset(_interruptNumber);
            log?.Write($"AJIN AxlOpenNoReset(interrupt={_interruptNumber}) returned {(AXT_FUNC_RESULT)openResult} (0x{openResult:X8}).");
            Check(openResult, nameof(CAXL.AxlOpenNoReset));
            try
            {
                ValidateModules();
                _initialized = true;
                log?.Write("AJIN initialized with AxlOpenNoReset; DIO mapping validated and no .mot file loaded.");
            }
            catch (Exception exception)
            {
                try { CAXL.AxlClose(); }
                catch (Exception cleanupError)
                {
                    exception.Data["AjinCloseError"] = cleanupError.ToString();
                    log?.Error("AJIN cleanup after initialization failure also failed.", cleanupError);
                }
                throw;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_initialized) return;
            _initialized = false;
            CAXL.AxlClose();
        }
    }

    public bool ReadRtexInput(int channel)
    {
        lock (_gate)
        {
            EnsureReady(nameof(CAXD.AxdiReadInportBit));
            var (module, offset) = GetRtexAddress(_inputModules, _inputCounts, channel, "DI");
            var value = 0U;
            Check(CAXD.AxdiReadInportBit(module, offset, ref value),
                nameof(CAXD.AxdiReadInportBit), module, offset);
            return value != 0;
        }
    }

    public void ReadRtexInputs(uint[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length != RtexInputWordCount)
            throw new ArgumentException($"Expected {RtexInputWordCount} AJIN input module slots.", nameof(values));

        lock (_gate)
        {
            EnsureReady(nameof(CAXD.AxdiReadInportWord));
            for (var index = 0; index < values.Length; index++)
            {
                // Manufacturer FormDigitalIO.timerSensor_Tick uses WORD offsets 0/1
                // for 32 DI and only offset 0 for mixed 16 DI / 16 DO modules.
                var value = ReadInputWord(_inputModules[index], 0);
                if (_inputCounts[index] == 32)
                    value |= ReadInputWord(_inputModules[index], 1) << 16;
                values[index] = value;
            }
        }
    }

    public bool ReadRtexOutput(int channel)
    {
        lock (_gate)
        {
            EnsureReady(nameof(CAXD.AxdoReadOutportBit));
            var (module, offset) = GetRtexAddress(_outputModules, _outputCounts, channel, "DO");
            var value = 0U;
            Check(CAXD.AxdoReadOutportBit(module, offset, ref value),
                nameof(CAXD.AxdoReadOutportBit), module, offset);
            return value != 0;
        }
    }

    public void WriteRtexOutput(int channel, bool value)
    {
        lock (_gate)
        {
            EnsureReady(nameof(CAXD.AxdoWriteOutportBit));
            var (module, offset) = GetRtexAddress(_outputModules, _outputCounts, channel, "DO");
            Check(CAXD.AxdoWriteOutportBit(module, offset, value ? 1U : 0U),
                nameof(CAXD.AxdoWriteOutportBit), module, offset);
        }
    }

    private void ValidateModules()
    {
        uint status = 0;
        Check(CAXD.AxdInfoIsDIOModule(ref status), nameof(CAXD.AxdInfoIsDIOModule));
        if (status != (uint)AXT_EXISTENCE.STATUS_EXIST)
            throw new IOException("AJIN DIO modules were not detected.");

        var moduleCount = 0;
        Check(CAXD.AxdInfoGetModuleCount(ref moduleCount), nameof(CAXD.AxdInfoGetModuleCount));
        log?.Write($"AJIN DIO module count={moduleCount}; input modules=[{string.Join(",", _inputModules)}], output modules=[{string.Join(",", _outputModules)}].");
        if (moduleCount <= 0)
            throw new IOException($"AJIN reported an invalid DIO module count: {moduleCount}.");

        var counts = new Dictionary<int, (int Inputs, int Outputs)>();
        foreach (var module in _inputModules.Concat(_outputModules).Distinct().Order())
        {
            if (module >= moduleCount)
                throw new IOException($"AJIN configured DIO module={module} is unavailable; detected {moduleCount} modules (0..{moduleCount - 1}).");

            int board = 0, position = 0, inputs = 0, outputs = 0;
            uint type = 0;
            Check(CAXD.AxdInfoGetModule(module, ref board, ref position, ref type),
                nameof(CAXD.AxdInfoGetModule), module);
            Check(CAXD.AxdInfoGetInputCount(module, ref inputs), nameof(CAXD.AxdInfoGetInputCount), module);
            Check(CAXD.AxdInfoGetOutputCount(module, ref outputs), nameof(CAXD.AxdInfoGetOutputCount), module);
            counts.Add(module, (inputs, outputs));
            log?.Write($"AJIN DIO module={module}, board={board}, position={position}, type={(AXT_MODULE)type} (0x{type:X}), DI={inputs}, DO={outputs}.");
        }

        _inputCounts = GetChannelCounts(_inputModules, counts, input: true);
        _outputCounts = GetChannelCounts(_outputModules, counts, input: false);
        log?.Write("AJIN DIO mapping validated. Input scan uses WORD offset 0 for 16 DI and offsets 0/1 for 32 DI.");
    }

    private static int[] GetChannelCounts(int[] modules,
        Dictionary<int, (int Inputs, int Outputs)> counts, bool input)
    {
        var result = new int[modules.Length];
        for (var index = 0; index < modules.Length; index++)
        {
            var count = input ? counts[modules[index]].Inputs : counts[modules[index]].Outputs;
            if (count is not (16 or 32))
                throw new IOException($"AJIN configured {(input ? "input" : "output")} module={modules[index]} has {count} {(input ? "DI" : "DO")} points; the RTEX mapping requires 16 or 32 points per module.");
            result[index] = count;
        }
        return result;
    }

    private static uint ReadInputWord(int module, int offset)
    {
        uint value = 0;
        Check(CAXD.AxdiReadInportWord(module, offset, ref value), nameof(CAXD.AxdiReadInportWord), module, offset);
        return value & 0xFFFF;
    }

    private void EnsureReady(string operation)
    {
        if (!_initialized)
            throw new IOException($"{operation} cannot run: AJIN initialization and DIO module validation have not completed.");
    }

    private static int[] CaptureModules(int[] modules, string name)
    {
        ArgumentNullException.ThrowIfNull(modules, name);
        if (modules.Any(module => module < 0))
            throw new ArgumentException("AJIN DIO module numbers must be nonnegative.", name);
        return (int[])modules.Clone();
    }

    private static uint GetInterruptNumber(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(AjinSettings.InterruptNumber));
        return (uint)value;
    }

    private static (int Module, int Offset) GetRtexAddress(
        int[] modules, int[] counts, int channel, string direction)
    {
        if (channel < 0 || channel / RtexChannelCountPerModule >= modules.Length)
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "The RTEX channel is outside the configured module slots.");
        var index = channel / RtexChannelCountPerModule;
        var module = modules[index];
        var offset = channel % RtexChannelCountPerModule;
        if (offset >= counts[index])
            throw new IOException($"AJIN RTEX {direction} channel={channel} maps to module={module}, offset={offset}, but the module has only {counts[index]} {direction} points.");
        return (module, offset);
    }

    internal static void Check(uint result, string operation, int? module = null, int? offset = null)
    {
        if (result != (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS)
        {
            throw new IOException(
                $"{operation}{(module is null ? "" : $" (module={module}{(offset is null ? "" : $", offset={offset}")})")} failed with Ajin result {(AXT_FUNC_RESULT)result} (0x{result:X8}).");
        }
    }
}
