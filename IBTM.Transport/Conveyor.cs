using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.Transport;

public sealed class Conveyor(
    IConveyorServo servo,
    IIoService io,
    ConveyorSettings settings) : IDisposable
{
    private readonly SemaphoreSlim _transferGate = new(1, 1);

    public void Initialize() => servo.Initialize();

    public AxisState GetAxisState() => servo.GetAxisState();

    public void ResetAlarm() => servo.ResetAlarm();

    public async Task ReceiveAsync(
        CarrierJigPositioner destination,
        CancellationToken cancellationToken)
    {
        await destination.WaitUntilEmptyAsync(cancellationToken);
        await io.WaitForInputAsync(
            InputIo.MainLaneUpstreamBoardAvailable,
            true,
            cancellationToken);
        await _transferGate.WaitAsync(cancellationToken);

        try
        {
            io.SetOutput(OutputIo.MainLaneUpstreamMachineReady, true);
            servo.Run(settings.Velocity);
            await destination.WaitUntilPresentAsync(cancellationToken);
            servo.Stop();
            await io.WaitForInputAsync(
                InputIo.MainLaneUpstreamBoardAvailable,
                false,
                cancellationToken);
        }
        finally
        {
            servo.Stop();
            io.SetOutput(OutputIo.MainLaneUpstreamMachineReady, false);
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
            servo.Run(settings.Velocity);
            await source.ReleaseAsync(cancellationToken);
            await destination.WaitUntilPresentAsync(cancellationToken);
        }
        finally
        {
            servo.Stop();
            _transferGate.Release();
        }
    }

    public async Task SendAsync(
        CarrierJigPositioner source,
        CancellationToken cancellationToken)
    {
        io.SetOutput(OutputIo.MainLaneDownstreamBoardAvailable, true);

        try
        {
            await io.WaitForInputAsync(
                InputIo.MainLaneDownstreamMachineReady,
                true,
                cancellationToken);
            await _transferGate.WaitAsync(cancellationToken);

            try
            {
                servo.Run(settings.Velocity);
                await source.ReleaseAsync(cancellationToken);
                io.SetOutput(OutputIo.MainLaneDownstreamBoardAvailable, false);
                await io.WaitForInputAsync(
                    InputIo.MainLaneDownstreamMachineReady,
                    false,
                    cancellationToken);
            }
            finally
            {
                servo.Stop();
                _transferGate.Release();
            }
        }
        finally
        {
            io.SetOutput(OutputIo.MainLaneDownstreamBoardAvailable, false);
        }
    }

    public void Stop()
    {
        servo.Stop();
        ResetSmema();
    }

    public void EmergencyStop()
    {
        servo.EmergencyStop();
        ResetSmema();
    }

    public void Dispose() => _transferGate.Dispose();

    private void ResetSmema()
    {
        io.SetOutput(OutputIo.MainLaneUpstreamMachineReady, false);
        io.SetOutput(OutputIo.MainLaneDownstreamBoardAvailable, false);
    }
}
