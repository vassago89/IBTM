using System.ComponentModel;

namespace IBTM.Device;

public enum AxisCondition
{
    [Description("Ready")] Ready,
    [Description("Moving")] Moving,
    [Description("Servo Off")] ServoOff,
    [Description("Home Required")] HomeRequired,
    [Description("Negative Limit")] NegativeLimit,
    [Description("Positive Limit")] PositiveLimit,
    [Description("Alarm")] Alarm,
    [Description("Emergency")] Emergency,
    [Description("Unavailable")] Unavailable,
}

// Display feedback only. Motion admission continues to read the device directly.
public sealed class AxisStatus : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public AxisState? State { get; private set; }
    public bool ServoOn => State is { ServoOn: true };
    public AxisCondition Condition => GetCondition(State);
    public static AxisCondition GetCondition(AxisState? state) => state switch
    {
        null => AxisCondition.Unavailable,
        { Emergency: true } => AxisCondition.Emergency,
        { Alarm: true } => AxisCondition.Alarm,
        { NegativeLimit: true } => AxisCondition.NegativeLimit,
        { PositiveLimit: true } => AxisCondition.PositiveLimit,
        { ServoOn: false } => AxisCondition.ServoOff,
        { Homed: false } => AxisCondition.HomeRequired,
        { InPosition: false } => AxisCondition.Moving,
        _ => AxisCondition.Ready,
    };

    internal void Update(AxisState? state)
    {
        if (State == state) return;
        State = state;
        PropertyChanged?.Invoke(this, new(nameof(State)));
        PropertyChanged?.Invoke(this, new(nameof(ServoOn)));
        PropertyChanged?.Invoke(this, new(nameof(Condition)));
    }
}
