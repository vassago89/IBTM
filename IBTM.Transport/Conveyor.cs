using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.Transport;

public sealed class Conveyor : IDisposable
{
    public const int UpstreamBoardAvailableInputChannel = 1;
    public const int UpstreamMachineReadyOutputChannel = 2;
    public const int DownstreamMachineReadyInputChannel = 40;
    public const int DownstreamBoardAvailableOutputChannel = 41;

    private readonly IConveyorServo _servo;
    private readonly IIoService _io;
    private readonly ConveyorSettings _settings;
    private readonly SemaphoreSlim _transferGate = new(1, 1);

    public Conveyor(
        IConveyorServo servo,
        IIoService io,
        ConveyorSettings settings)
    {
        _servo = servo;
        _io = io;
        _settings = settings;
    }

    public void Initialize() => _servo.Initialize();

    public async Task ReceiveAsync(
        CarrierJigPositioner destination,
        CancellationToken cancellationToken)
    {
        await destination.WaitUntilEmptyAsync(cancellationToken);
        await _io.WaitForInputAsync(
            UpstreamBoardAvailableInputChannel,
            true,
            cancellationToken);
        await _transferGate.WaitAsync(cancellationToken);

        try
        {
            _io.SetOutput(UpstreamMachineReadyOutputChannel, true);
            _servo.Run(_settings.Velocity);
            await destination.WaitUntilPresentAsync(cancellationToken);
        }
        finally
        {
            _servo.Stop();
            _io.SetOutput(UpstreamMachineReadyOutputChannel, false);
            _transferGate.Release();
        }
    }

    public async Task TransferAsync(
        CarrierJigPositioner source,
        CarrierJigPositioner destination,
        CancellationToken cancellationToken)
    {
        await destination.WaitUntilEmptyAsync(cancellationToken);
        await _transferGate.WaitAsync(cancellationToken);

        try
        {
            _servo.Run(_settings.Velocity);
            await source.ReleaseAsync(cancellationToken);
            await destination.WaitUntilPresentAsync(cancellationToken);
        }
        finally
        {
            _servo.Stop();
            _transferGate.Release();
        }
    }

    public async Task SendAsync(
        CarrierJigPositioner source,
        CancellationToken cancellationToken)
    {
        _io.SetOutput(DownstreamBoardAvailableOutputChannel, true);

        try
        {
            await _io.WaitForInputAsync(
                DownstreamMachineReadyInputChannel,
                true,
                cancellationToken);
            await _transferGate.WaitAsync(cancellationToken);

            try
            {
                _servo.Run(_settings.Velocity);
                await source.ReleaseAsync(cancellationToken);
            }
            finally
            {
                _servo.Stop();
                _transferGate.Release();
            }
        }
        finally
        {
            _io.SetOutput(DownstreamBoardAvailableOutputChannel, false);
        }
    }

    public void Stop()
    {
        _servo.Stop();
        ResetSmema();
    }

    public void EmergencyStop()
    {
        _servo.EmergencyStop();
        ResetSmema();
    }

    public void Dispose() => _transferGate.Dispose();

    private void ResetSmema()
    {
        _io.SetOutput(UpstreamMachineReadyOutputChannel, false);
        _io.SetOutput(DownstreamBoardAvailableOutputChannel, false);
    }

}
