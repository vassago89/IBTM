using System.Threading;
using System.Threading.Tasks;
using IBTM.Core.Machine;
using IBTM.Core.Process;
using IBTM.Stations.BoltFastening;
using IBTM.Stations.Inspection;
using IBTM.Stations.PcbPlacement;
using IBTM.Virtual;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class VirtualStationTests
{
    [Fact]
    public async Task StationsRunFromVirtualInputsAndReleaseActuators()
    {
        var io = new VirtualIoService();
        io.Initialize();
        io.SetInput(PcbPlacementStation.ShuttlePresentInputChannel, false);

        using var zone1Motion = new VirtualMotionService();
        using var zone2Motion = new VirtualMotionService();
        using var zone3Motion = new VirtualMotionService();
        var events = new ProcessEvents();
        var stageFailed = false;
        events.StageChanged += (_, status) => stageFailed |= status == StageStatus.Error;

        using var zone1 = new PcbPlacementStation(
            zone1Motion,
            io,
            new ZoneMotionParams { SpeedXY = 10_000, SpeedZ = 10_000 },
            events);
        using var zone2 = new BoltFasteningStation(
            zone2Motion,
            io,
            new BoltFasteningOptions
            {
                Motion = new ZoneMotionParams { SpeedXY = 10_000, SpeedZ = 10_000 },
            },
            events,
            new VirtualFiducialService(),
            new VirtualBoltService());
        using var zone3 = new InspectionStation(
            zone3Motion,
            io,
            new InspectionOptions
            {
                Motion = new ZoneMotionParams { SpeedXY = 10_000, SpeedZ = 10_000 },
            },
            events,
            new VirtualInspectionService());

        zone1.Initialize();
        zone2.Initialize();
        zone3.Initialize();

        var zone1Run = zone1.RunAsync(new PcbPlacementRecipe(), CancellationToken.None);
        await Task.Delay(50);
        Assert.False(zone1Run.IsCompleted);

        io.SetInput(PcbPlacementStation.ShuttlePresentInputChannel, true);
        await zone1Run;

        await zone2.PrepareAsync(CancellationToken.None);
        await zone2.RunAsync(
            new BoltFasteningRecipe { BoltPoints = [] },
            CancellationToken.None);
        await zone3.RunAsync(new InspectionRecipe(), CancellationToken.None);

        Assert.False(stageFailed);
        Assert.False(io.GetOutput(PcbPlacementStation.StopperChannel));
        Assert.False(io.GetOutput(BoltFasteningStation.StopperChannel));
        Assert.False(io.GetOutput(InspectionStation.StopperChannel));
        Assert.False(io.GetOutput(InspectionStation.BoardAvailableChannel));
    }
}
