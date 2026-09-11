using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using IBTM.Core;

namespace IBTM.Device;

public sealed class OutputFeedback(InputIo onInput, InputIo offInput)
{
    public InputIo OnInput { get; set; } = onInput;
    public InputIo OffInput { get; set; } = offInput;
}

public sealed class OutputHardware
{
    public int Number { get; set; }
    public int? OffNumber { get; set; }
    public OutputFeedback? Feedback { get; set; }
}

public sealed class AxisHardware
{
    private double _moveUnit = 1;
    private int _movePulse = 1;

    public int Number { get; set; }
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
    public Dictionary<OutputIo, OutputHardware> Outputs { get; set; } = [];

    protected static OutputHardware Output(int number)
    {
        return new()
        {
            Number = number,
        };
    }

    protected static OutputHardware Output(int number, InputIo onInput, InputIo offInput)
    {
        return new()
        {
            Number = number,
            Feedback = new(onInput, offInput),
        };
    }

    protected static OutputHardware Output(int number, int offNumber, InputIo onInput, InputIo offInput)
    {
        return new()
        {
            Number = number,
            OffNumber = offNumber,
            Feedback = new(onInput, offInput),
        };
    }
}

public abstract class MotionHardwareSettings(
    MotionGroup group,
    params (MotionAxis Axis, MachineAxis Signal, int Number)[] axes) : IoHardwareSettings
{
    public const double DefaultMillimetersPerUnit = 0.001;

    [JsonIgnore]
    public MotionGroup Group { get; } = group;

    [JsonIgnore]
    public IReadOnlyDictionary<MotionAxis, MachineAxis> AxisSignals { get; } = axes.ToDictionary(
        axis => axis.Axis,
        axis => axis.Signal);
    public Dictionary<MachineAxis, AxisHardware> Axes { get; set; } = axes.ToDictionary(
        axis => axis.Signal,
        axis => new AxisHardware { Number = axis.Number });
    [JsonPropertyName("MillimetersPerPulse")]
    public double MillimetersPerUnit { get; set; } = DefaultMillimetersPerUnit;

    public AxisHardware? GetAxis(MotionAxis axis)
    {
        return AxisSignals.TryGetValue(axis, out var signal) ? Axes[signal] : null;
    }
}
