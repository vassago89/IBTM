namespace IBTM.Device.Abstractions;

public sealed class IoChangedEventArgs(int channel, bool value) : EventArgs
{
    public int Channel { get; } = channel;
    public bool Value { get; } = value;
}

public interface IIOService
{
    event EventHandler<IoChangedEventArgs>? InputChanged;
    event EventHandler<IoChangedEventArgs>? OutputChanged;

    void Initialize();
    bool GetInput(int channel);
    bool GetOutput(int channel);
    void SetOutput(int channel, bool value);
    void TurnOffAll();
}
