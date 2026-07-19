namespace IBTM.Device.Adapters.Ajin;

/// <summary>Ajin 64-channel digital IO adapter.</summary>
public sealed class AjinIOService : IIOService, IDisposable
{
    private const int ChannelCount = 64;
    private const int ChannelsPerModule = 32;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly object _sync = new();
    private readonly bool[] _inputs = new bool[ChannelCount];
    private readonly bool[] _outputs = new bool[ChannelCount];
    private readonly int _inputOffset;
    private readonly int _outputOffset;

    private CancellationTokenSource? _pollCancellation;
    private Task? _pollTask;
    private bool _disposed;

    public AjinIOService(int inputOffset = 0, int outputOffset = 0)
    {
        _inputOffset = inputOffset;
        _outputOffset = outputOffset;
    }

    public event EventHandler<IoChangedEventArgs>? InputChanged;
    public event EventHandler<IoChangedEventArgs>? OutputChanged;

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            if (_pollTask is { IsCompleted: false })
            {
                return;
            }

            _pollCancellation = new CancellationTokenSource();
            _pollTask = PollAsync(_pollCancellation.Token);
        }
    }

    public bool GetInput(int channel)
    {
        ValidateChannel(channel);
        var value = 0U;
        CAXD.AxdiReadInportBit(
            (channel / ChannelsPerModule) + _inputOffset,
            channel % ChannelsPerModule,
            ref value);
        return value != 0;
    }

    public bool GetOutput(int channel)
    {
        ValidateChannel(channel);
        var value = 0U;
        CAXD.AxdoReadOutportBit(
            (channel / ChannelsPerModule) + _outputOffset,
            channel % ChannelsPerModule,
            ref value);
        return value != 0;
    }

    public void SetOutput(int channel, bool value)
    {
        ValidateChannel(channel);
        CAXD.AxdoWriteOutportBit(
            (channel / ChannelsPerModule) + _outputOffset,
            channel % ChannelsPerModule,
            value ? 1U : 0U);
    }

    public void TurnOffAll()
    {
        for (var module = 0; module < ChannelCount / ChannelsPerModule; module++)
        {
            for (var bit = 0; bit < ChannelsPerModule; bit++)
            {
                CAXD.AxdoWriteOutportBit(module + _outputOffset, bit, 0);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pollCancellation?.Cancel();

        try
        {
            _pollTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        _pollCancellation?.Dispose();
        _pollCancellation = null;
        _pollTask = null;
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            RefreshState();
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    private void RefreshState()
    {
        var inputs = ReadPorts(input: true);
        var outputs = ReadPorts(input: false);

        for (var channel = 0; channel < ChannelCount; channel++)
        {
            bool inputChanged;
            bool outputChanged;
            lock (_sync)
            {
                inputChanged = _inputs[channel] != inputs[channel];
                outputChanged = _outputs[channel] != outputs[channel];
                _inputs[channel] = inputs[channel];
                _outputs[channel] = outputs[channel];
            }

            if (inputChanged)
            {
                InputChanged?.Invoke(this, new IoChangedEventArgs(channel, inputs[channel]));
            }

            if (outputChanged)
            {
                OutputChanged?.Invoke(this, new IoChangedEventArgs(channel, outputs[channel]));
            }
        }
    }

    private bool[] ReadPorts(bool input)
    {
        var values = new bool[ChannelCount];
        var offset = input ? _inputOffset : _outputOffset;

        for (var module = 0; module < ChannelCount / ChannelsPerModule; module++)
        {
            var port = 0U;
            if (input)
            {
                CAXD.AxdiReadInportDword(module + offset, 0, ref port);
            }
            else
            {
                CAXD.AxdoReadOutportDword(module + offset, 0, ref port);
            }

            for (var bit = 0; bit < ChannelsPerModule; bit++)
            {
                values[bit + (module * ChannelsPerModule)] = ((port >> bit) & 1U) != 0;
            }
        }

        return values;
    }

    private static void ValidateChannel(int channel)
    {
        if (channel is < 0 or >= ChannelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "IO channel must be between 0 and 63.");
        }
    }
}
