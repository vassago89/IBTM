namespace IBTM.Device.Simulation;

/// <summary>Thread-safe 64-channel digital IO simulator.</summary>
public sealed class VirtualIoService : IIOService
{
    private const int ChannelCount = 64;

    private readonly object _sync = new();
    private readonly bool[] _inputs = new bool[ChannelCount];
    private readonly bool[] _outputs = new bool[ChannelCount];

    public event EventHandler<IoChangedEventArgs>? InputChanged;
    public event EventHandler<IoChangedEventArgs>? OutputChanged;

    public void Initialize()
    {
    }

    public bool GetInput(int channel)
    {
        ValidateChannel(channel);
        lock (_sync)
        {
            return _inputs[channel];
        }
    }

    public bool GetOutput(int channel)
    {
        ValidateChannel(channel);
        lock (_sync)
        {
            return _outputs[channel];
        }
    }

    public void SetOutput(int channel, bool value)
    {
        ValidateChannel(channel);
        lock (_sync)
        {
            if (_outputs[channel] == value)
            {
                return;
            }

            _outputs[channel] = value;
        }

        OutputChanged?.Invoke(this, new IoChangedEventArgs(channel, value));
    }

    public void SetInput(int channel, bool value)
    {
        ValidateChannel(channel);
        lock (_sync)
        {
            if (_inputs[channel] == value)
            {
                return;
            }

            _inputs[channel] = value;
        }

        InputChanged?.Invoke(this, new IoChangedEventArgs(channel, value));
    }

    public void TurnOffAll()
    {
        for (var channel = 0; channel < ChannelCount; channel++)
        {
            SetOutput(channel, false);
        }
    }

    private static void ValidateChannel(int channel)
    {
        if (channel is < 0 or >= ChannelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "IO channel must be between 0 and 63.");
        }
    }
}
