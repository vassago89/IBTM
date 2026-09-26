using System.Collections.Generic;
using System.Linq;
using IBTM.Device;

namespace IBTM.UI;

public sealed class TeachingIoGroup
{
    public TeachingIoGroup(
        InputHardwareSettings hardware,
        IEnumerable<OutputIo> outputs,
        IoSignals io,
        MachineController machine)
    {
        Area = hardware.Area;
        var signals = outputs.Select(output => io.Outputs[output]).OrderBy(row => row.Signal).ToArray();
        Sensors = hardware.Inputs.Keys.Select(input => io.Inputs[input])
            .Except(signals.SelectMany(row => row.Feedback))
            .OrderBy(row => row.Signal)
            .ToArray();
        Outputs = signals
            .Where(signal => signal.Signal != OutputIo.PcbPlacementHandlerRotate)
            .Select(signal => new TeachingOutputRow(signal, machine))
            .ToArray();
    }

    public HardwareArea Area { get; }
    public IReadOnlyList<IoInputStatus> Sensors { get; }
    public IReadOnlyList<TeachingOutputRow> Outputs { get; }
}
