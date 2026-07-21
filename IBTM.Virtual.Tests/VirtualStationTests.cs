using System.Threading;
using System.Threading.Tasks;
using IBTM.Core.Machine;
using IBTM.Core.Process;
using IBTM.Stations.BoltFastening;
using IBTM.Stations.Inspection;
using IBTM.Stations.PcbPlacement;
using IBTM.Transport;
using IBTM.Virtual;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class VirtualStationTests
{
    [Fact]
    public async Task StationsProcessWhileAnotherCarrierJigMoves()
    {
        var io = new VirtualIoService();
        io.Initialize();

        using var pcbPlacementMotion = new VirtualMotionService();
        using var boltFasteningMotion = new VirtualMotionService();
        using var inspectionMotion = new VirtualMotionService();
        var conveyorServo = new VirtualConveyorServo(io);
        using var conveyor = new Conveyor(
            conveyorServo,
            io,
            new ConveyorSettings { Velocity = 100 });
        var events = new ProcessEvents();
        var stageFailed = false;
        var pcbPlacementPositioned = false;
        var boltFasteningPositioned = false;
        var inspectionPositioned = false;
        events.StageChanged += (stage, status) =>
        {
            stageFailed |= status == StageStatus.Error;
            if (status != StageStatus.Done)
            {
                return;
            }

            if (stage == PcbPlacementStages.PositionCarrierJig)
            {
                pcbPlacementPositioned =
                    io.GetOutput(PcbPlacementStation.BackupPlateUpOutputChannel)
                    && !io.GetOutput(PcbPlacementStation.StopperUpOutputChannel);
            }
            else if (stage == BoltFasteningStages.PositionCarrierJig)
            {
                boltFasteningPositioned =
                    io.GetOutput(BoltFasteningStation.BackupPlateUpOutputChannel)
                    && !io.GetOutput(BoltFasteningStation.StopperUpOutputChannel);
            }
            else if (stage == InspectionStages.PositionCarrierJig)
            {
                inspectionPositioned =
                    io.GetOutput(InspectionStation.BackupPlateUpOutputChannel)
                    && !io.GetOutput(InspectionStation.StopperUpOutputChannel);
            }
        };

        using var pcbPlacement = new PcbPlacementStation(
            pcbPlacementMotion,
            io,
            new StationMotionSettings { SpeedXY = 10_000, SpeedZ = 10_000 },
            events);
        using var boltFastening = new BoltFasteningStation(
            boltFasteningMotion,
            io,
            new BoltFasteningOptions
            {
                Motion = new StationMotionSettings { SpeedXY = 10_000, SpeedZ = 10_000 },
            },
            events,
            new VirtualFiducialService(),
            new VirtualBoltService());
        using var inspection = new InspectionStation(
            inspectionMotion,
            io,
            new InspectionOptions
            {
                Motion = new StationMotionSettings { SpeedXY = 10_000, SpeedZ = 10_000 },
            },
            events,
            new VirtualInspectionService());

        pcbPlacement.Initialize();
        boltFastening.Initialize();
        inspection.Initialize();
        conveyor.Initialize();

        await conveyor.ReceiveAsync(
            pcbPlacement.CarrierJigPositioner,
            CancellationToken.None);
        await pcbPlacement.ProcessAsync(new PcbPlacementRecipe(), CancellationToken.None);
        await conveyor.TransferAsync(
            pcbPlacement.CarrierJigPositioner,
            boltFastening.CarrierJigPositioner,
            CancellationToken.None);
        await conveyor.ReceiveAsync(
            pcbPlacement.CarrierJigPositioner,
            CancellationToken.None);
        Assert.True(io.GetInput(PcbPlacementStation.CarrierJigPresentInputChannel));
        Assert.True(io.GetInput(BoltFasteningStation.CarrierJigPresentInputChannel));
        Assert.False(io.GetInput(InspectionStation.CarrierJigPresentInputChannel));

        await boltFastening.PrepareAsync(CancellationToken.None);
        await boltFastening.ProcessAsync(
            new BoltFasteningRecipe { BoltPoints = [] },
            CancellationToken.None);
        await conveyor.TransferAsync(
            boltFastening.CarrierJigPositioner,
            inspection.CarrierJigPositioner,
            CancellationToken.None);
        Assert.True(io.GetInput(PcbPlacementStation.CarrierJigPresentInputChannel));
        Assert.False(io.GetInput(BoltFasteningStation.CarrierJigPresentInputChannel));
        Assert.True(io.GetInput(InspectionStation.CarrierJigPresentInputChannel));

        var inspectionProcess = inspection.ProcessAsync(
            new InspectionRecipe(),
            CancellationToken.None);
        var nextPcbPlacementProcess = pcbPlacement.ProcessAsync(
            new PcbPlacementRecipe(),
            CancellationToken.None);

        await nextPcbPlacementProcess;
        Assert.False(inspectionProcess.IsCompleted);
        await conveyor.TransferAsync(
            pcbPlacement.CarrierJigPositioner,
            boltFastening.CarrierJigPositioner,
            CancellationToken.None);
        await conveyor.ReceiveAsync(
            pcbPlacement.CarrierJigPositioner,
            CancellationToken.None);

        Assert.True(io.GetInput(PcbPlacementStation.CarrierJigPresentInputChannel));
        Assert.True(io.GetInput(BoltFasteningStation.CarrierJigPresentInputChannel));
        Assert.True(io.GetInput(InspectionStation.CarrierJigPresentInputChannel));

        var inspectionResult = await inspectionProcess;
        if (inspectionResult == InspectionResult.Good)
        {
            await conveyor.SendAsync(
                inspection.CarrierJigPositioner,
                CancellationToken.None);
        }

        Assert.True(io.GetInput(PcbPlacementStation.CarrierJigPresentInputChannel));
        Assert.True(io.GetInput(BoltFasteningStation.CarrierJigPresentInputChannel));
        Assert.False(io.GetInput(InspectionStation.CarrierJigPresentInputChannel));

        Assert.False(stageFailed);
        Assert.True(pcbPlacementPositioned);
        Assert.True(boltFasteningPositioned);
        Assert.True(inspectionPositioned);
        Assert.False(io.GetOutput(PcbPlacementStation.StopperUpOutputChannel));
        Assert.False(io.GetOutput(BoltFasteningStation.StopperUpOutputChannel));
        Assert.False(io.GetOutput(InspectionStation.StopperUpOutputChannel));
        Assert.False(io.GetOutput(PcbPlacementStation.BackupPlateUpOutputChannel));
        Assert.False(io.GetOutput(BoltFasteningStation.BackupPlateUpOutputChannel));
        Assert.False(io.GetOutput(InspectionStation.BackupPlateUpOutputChannel));
        Assert.False(conveyorServo.IsRunning);
        Assert.False(io.GetOutput(Conveyor.DownstreamBoardAvailableOutputChannel));
    }
}
