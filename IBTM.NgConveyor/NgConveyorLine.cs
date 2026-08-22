using IBTM.Device;

namespace IBTM.NgConveyor;

public sealed class NgConveyorLine(IIoService io)
{
    public bool RunCommandOn => io.GetOutput(OutputIo.NgConveyorRun);

    public void Stop() =>
        io.SetOutput(OutputIo.NgConveyorRun, false);
}
