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
    public int Number { get; set; }
    public double Minimum { get; set; }
    public double Maximum { get; set; }
}

public abstract class HardwareSettings : Setting
{
    [JsonIgnore]
    public abstract HardwareArea Area { get; }

    public virtual IoSection? GetSection(Enum signal) => null;
}

public abstract class InputHardwareSettings : HardwareSettings
{
    public Dictionary<InputIo, int> Inputs { get; set; } = [];

    public IoStatus CreateIoStatus(IoSignals io) => io.Select(
        Area,
        Inputs.Keys,
        this is IoHardwareSettings hardware ? hardware.Outputs.Keys : []);
}

public abstract class IoHardwareSettings : InputHardwareSettings
{
    public Dictionary<OutputIo, OutputHardware> Outputs { get; set; } = [];

    protected static OutputHardware Output(int number) => new()
    {
        Number = number,
    };

    protected static OutputHardware Output(
        int number,
        InputIo onInput,
        InputIo offInput) => new()
        {
            Number = number,
            Feedback = new(onInput, offInput),
        };

    protected static OutputHardware Output(
        int number,
        int offNumber,
        InputIo onInput,
        InputIo offInput) => new()
        {
            Number = number,
            OffNumber = offNumber,
            Feedback = new(onInput, offInput),
        };
}

public abstract class MotionHardwareSettings(
    MotionGroup group,
    params (MotionAxis Axis, MachineAxis Signal, int Number, double Maximum)[] axes) : IoHardwareSettings
{
    public const double DefaultMillimetersPerPulse = 0.01;

    [JsonIgnore]
    public MotionGroup Group { get; } = group;
    [JsonIgnore]
    public IReadOnlyDictionary<MotionAxis, MachineAxis> AxisSignals { get; } =
        axes.ToDictionary(axis => axis.Axis, axis => axis.Signal);
    public Dictionary<MachineAxis, AxisHardware> Axes { get; set; } =
        axes.ToDictionary(axis => axis.Signal,
            axis => new AxisHardware { Number = axis.Number, Maximum = axis.Maximum });
    public double MillimetersPerPulse { get; set; } =
        DefaultMillimetersPerPulse;

    public AxisHardware? GetAxis(MotionAxis axis) =>
        AxisSignals.TryGetValue(axis, out var signal) ? Axes[signal] : null;
}
