using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Serialization;
using IBTM.Core;

namespace IBTM.Device;

public sealed class OutputFeedback
{
    public OutputFeedback(InputIo onInput, InputIo? offInput = null)
    {
        OnInput = onInput;
        OffInput = offInput;
    }

    public InputIo OnInput { get; }
    public InputIo? OffInput { get; }
}

public sealed class OutputHardware
{
    public int Number { get; set; }
    public int? OffNumber { get; set; }
    [JsonIgnore]
    public OutputFeedback? Feedback { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<HomeDirection>))]
public enum HomeDirection
{
    [Description("− (Negative)")]
    Negative,

    [Description("+ (Positive)")]
    Positive,
}

public sealed class AxisHardware
{
    private double _moveUnit = 1;
    private int _movePulse = 1;

    public int Number { get; set; }
    public HomeDirection HomeDirection { get; set; } = HomeDirection.Negative;

    public double MoveUnit
    {
        get
        {
            return _moveUnit;
        }
        set
        {
            if (!double.IsFinite(value) || value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "SDK Unit must be a positive finite value.");
            _moveUnit = value;
        }
    }

    public int MovePulse
    {
        get
        {
            return _movePulse;
        }
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "SDK Pulse must be a positive integer.");
            _movePulse = value;
        }
    }
}

public abstract class HardwareSettings : Setting
{
    [JsonIgnore]
    public abstract HardwareArea Area { get; }

    public virtual IoSection? GetSection(Enum signal)
    {
        return null;
    }
}

public abstract class InputHardwareSettings : HardwareSettings
{
    public Dictionary<InputIo, int> Inputs { get; set; } = [];

    public IoStatus CreateIoStatus(IoSignals io)
    {
        return io.Select(
            Area,
            Inputs.Keys,
            this is IoHardwareSettings hardware ? hardware.Outputs.Keys : []);
    }
}

public abstract class IoHardwareSettings : InputHardwareSettings
{
    private Dictionary<OutputIo, OutputHardware> _outputs = [];

    public Dictionary<OutputIo, OutputHardware> Outputs
    {
        get
        {
            return _outputs;
        }
        set
        {
            // Loading addresses must retain the station's completion-sensor definition.
            foreach (var (signal, output) in value)
            {
                if (_outputs.TryGetValue(signal, out var definition))
                    output.Feedback = definition.Feedback;
            }
            _outputs = value;
        }
    }

    protected static OutputHardware CreateOutput(int number)
    {
        return new()
        {
            Number = number,
        };
    }

    protected static OutputHardware CreateOutput(int number, InputIo onInput, InputIo? offInput = null)
    {
        return new()
        {
            Number = number,
            Feedback = new(onInput, offInput),
        };
    }

    protected static OutputHardware CreateOutput(int number, int offNumber, InputIo onInput, InputIo offInput)
    {
        return new()
        {
            Number = number,
            OffNumber = offNumber,
            Feedback = new(onInput, offInput),
        };
    }
}

public abstract class MotionHardwareSettings : IoHardwareSettings
{
    protected MotionHardwareSettings(
        MotionGroup group,
        params (MotionAxis Axis, MachineAxis Signal, int Number)[] axes)
    {
        Group = group;
        AxisSignals = axes.ToDictionary(
            axis => axis.Axis,
            axis => axis.Signal);
        Axes = axes.ToDictionary(
            axis => axis.Signal,
            axis => new AxisHardware { Number = axis.Number });
    }

    [JsonIgnore]
    public MotionGroup Group { get; }
    [JsonIgnore]
    public IReadOnlyDictionary<MotionAxis, MachineAxis> AxisSignals { get; }
    public Dictionary<MachineAxis, AxisHardware> Axes { get; set; }

    public AxisHardware? GetAxis(MotionAxis axis)
    {
        return AxisSignals.TryGetValue(axis, out var signal) ? Axes[signal] : null;
    }
}
