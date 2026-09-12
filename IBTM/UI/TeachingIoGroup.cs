using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using IBTM.Device;

namespace IBTM.UI;

public sealed class TeachingIoGroup
{
    public TeachingIoGroup(
        IoStatus io,
        IReadOnlyDictionary<OutputIo, TeachingOutput> outputs,
        IAsyncRelayCommand<TeachingOutput> toggleCommand)
    {
        Area = io.Area;
        Sensors = io.Sensors;
        Outputs = io.Outputs.Select(
            signal =>
                new TeachingOutputRow(
                    signal,
                    outputs.GetValueOrDefault(signal.Signal),
                    toggleCommand))
            .ToArray();
    }

    public HardwareArea Area { get; }
    public IReadOnlyList<IoInputStatus> Sensors { get; }
    public IReadOnlyList<TeachingOutputRow> Outputs { get; }
}

public sealed record TeachingOutputRow(
    IoOutputStatus Io,
    TeachingOutput? Output,
    IAsyncRelayCommand<TeachingOutput> ToggleOutputCommand);
