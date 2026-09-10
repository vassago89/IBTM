namespace IBTM.Device;

// Presentation metadata only. MachineController owns the explicit command routing.
public sealed record TeachingOutput(
    OutputIo Signal,
    HardwareArea Owner,
    bool RequiresHandler = true,
    bool HoldToRun = false);
