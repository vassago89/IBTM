using System.Collections.Generic;
using System.Linq;
using IBTM.Device;

namespace IBTM.UI;

public sealed class TeachingIoGroup
{
    public TeachingIoGroup(
        IoStatus io,
        IReadOnlyDictionary<OutputIo, TeachingOutput> outputs,
        MachineController machine)
    {
        Area = io.Area;
        Sensors = io.Sensors;
        Outputs = io.Outputs.Select(
            signal =>
                new TeachingOutputRow(
                    signal,
                    outputs.GetValueOrDefault(signal.Signal),
                    machine))
            .ToArray();
    }

    public HardwareArea Area { get; }
    public IReadOnlyList<IoInputStatus> Sensors { get; }
    public IReadOnlyList<TeachingOutputRow> Outputs { get; }
}
