using System.ComponentModel;

namespace IBTM.Device;

public enum AxisCondition
{
    [Description("Ready")]
    Ready,
    [Description("Moving")]
    Moving,
    [Description("Not In Position")]
    NotInPosition,
    [Description("Servo Off")]
    ServoOff,
    [Description("Home Required")]
    HomeRequired,
    [Description("Negative Limit")]
    NegativeLimit,
    [Description("Positive Limit")]
    PositiveLimit,
    [Description("Alarm")]
    Alarm,
    [Description("Emergency")]
    Emergency,
    [Description("Unavailable")]
    Unavailable,
}

// Display feedback only. Motion admission continues to read the device directly.
public sealed class AxisStatus : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public AxisState? State
    {
        get;
        internal set
        {
            if (field == value)
                return;
            field = value;
            PropertyChanged?.Invoke(this, new(nameof(State)));
            PropertyChanged?.Invoke(this, new(nameof(Condition)));
        }
    }

    public AxisCondition Condition => GetCondition(State);

    public static AxisCondition GetCondition(AxisState? state)
    {
        switch (state)
        {
            case null:
                return AxisCondition.Unavailable;
            case { Emergency: true }:
                return AxisCondition.Emergency;
            case { Alarm: true }:
                return AxisCondition.Alarm;
            case { NegativeLimit: true }:
                return AxisCondition.NegativeLimit;
            case { PositiveLimit: true }:
                return AxisCondition.PositiveLimit;
            case { ServoOn: false }:
                return AxisCondition.ServoOff;
            case { Homed: false }:
                return AxisCondition.HomeRequired;
            case { InMotion: true }:
                return AxisCondition.Moving;
            case { InPosition: false }:
                return AxisCondition.NotInPosition;
            default:
                return AxisCondition.Ready;
        }
    }
}
