using System.Collections.Generic;
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
    public AxisDirection Direction { get; set; } = AxisDirection.Positive;
    public double Minimum { get; set; }
    public double Maximum { get; set; }
}

public abstract class HardwareSettings : Setting
{
    [JsonIgnore]
    public abstract HardwareArea Area { get; }
}

public abstract class InputHardwareSettings : HardwareSettings
{
    public Dictionary<InputIo, int> Inputs { get; set; } = [];
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

public abstract class MotionHardwareSettings : IoHardwareSettings
{
    public const double DefaultMillimetersPerPulse = 0.01;

    public Dictionary<MachineAxis, AxisHardware> Axes { get; set; } = [];
    public double MillimetersPerPulse { get; set; } =
        DefaultMillimetersPerPulse;

    protected static AxisHardware Axis(
        int number,
        double maximum = 200) => new()
        {
            Number = number,
            Maximum = maximum,
        };
}
