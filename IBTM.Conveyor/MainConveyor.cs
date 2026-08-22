using System.Threading;
using IBTM.Device;

namespace IBTM.Conveyor;

public sealed class MainConveyor(
    IIoService io,
    OperationCancellation operations)
{
    private CancellationTokenSource? _runCancellation;
    private CancellationTokenRegistration _stopRegistration;

    public bool RunCommandOn => io.GetOutput(OutputIo.MainConveyorRun);

    public void Initialize()
    {
        Stop();
        io.SetOutput(OutputIo.MainConveyorReverse, false);
        io.SetOutput(OutputIo.MainConveyorNormalSpeed, true);
    }

    public void Run(CancellationToken cancellationToken = default)
    {
        Stop();
        _runCancellation = operations.Link(cancellationToken);
        var runToken = _runCancellation.Token;
        runToken.ThrowIfCancellationRequested();
        _stopRegistration = runToken.Register(StopMotor);
        io.SetOutput(OutputIo.MainConveyorReverse, false);
        io.SetOutput(OutputIo.MainConveyorNormalSpeed, true);
        io.SetOutput(OutputIo.MainConveyorRun, true);
        if (runToken.IsCancellationRequested)
        {
            StopMotor();
        }
        runToken.ThrowIfCancellationRequested();
    }

    public void Stop()
    {
        _stopRegistration.Dispose();
        _runCancellation?.Dispose();
        _runCancellation = null;
        StopMotor();
    }

    public void ResetSmema()
    {
        io.SetOutput(OutputIo.MainConveyorReadyToFront2, false);
        io.SetOutput(OutputIo.MainConveyorAvailableToRear, false);
    }

    private void StopMotor() =>
        io.SetOutput(OutputIo.MainConveyorRun, false);
}
