using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbSupply;
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
    public async Task PcbSupplyFeedbackTimeoutRaisesStageAlarm()
    {
        var hardware = new HardwareMap();
        hardware.OutputFeedbacks[OutputIo.PcbSupplyGripper]
            .TimeoutMilliseconds = 20;
        var io = new VirtualIoService(hardware);
        io.DisabledFeedbacks.Add(OutputIo.PcbSupplyGripper);
        io.Initialize();

        var motionSettings = new StationMotionSettings
        {
            HorizontalSpeed = 10_000,
            SpeedZ = 10_000,
        };
        using var motion = new VirtualMotionService(
            motionSettings,
            hasY: false);
        var events = new ProcessEvents();
        var alarm = new TaskCompletionSource<ProcessStage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        events.StageChanged += (stage, status) =>
        {
            if (status == StageStatus.Error)
            {
                alarm.TrySetResult(stage);
            }
        };
        var feeder = new PcbFeeder(
            motion,
            io,
            new PcbHandoff(),
            new PcbSupplySettings { Motion = motionSettings },
            events);
        feeder.Initialize();

        await Assert.ThrowsAsync<IoFeedbackTimeoutException>(
            async () => await feeder.RunAsync(
                new PcbSupplyRecipe(),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal(
            ProcessStage.SupplyPcb,
            await alarm.Task.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task HandlerSensorsSkipMissingPcbs(
        bool supplyPcbPresent,
        bool placementPcbPresent)
    {
        var io = new VirtualIoService
        {
            SupplyPcbPresentOnPick = supplyPcbPresent,
            PlacementPcbPresentOnPick = placementPcbPresent,
        };
        io.Initialize();

        var motionSettings = new StationMotionSettings
        {
            HorizontalSpeed = 10_000,
            SpeedZ = 10_000,
        };
        using var pcbSupplyMotion =
            new VirtualMotionService(motionSettings, hasY: false);
        using var pcbPlacementMotion =
            new VirtualMotionService(motionSettings);
        using var alignmentCamera =
            new VirtualCameraStreamService(inspection: false);
        var light = new VirtualLightController();
        var lighting = new LightingSettings();
        var events = new ProcessEvents();
        var handoff = new PcbHandoff();
        var supplySettings = new PcbSupplySettings
        {
            Motion = motionSettings,
        };
        var placementSettings = new PcbPlacementSettings
        {
            Motion = motionSettings,
        };
        var pcbFeeder = new PcbFeeder(
            pcbSupplyMotion,
            io,
            handoff,
            supplySettings,
            events);
        var pcbPlacement = new PcbPlacementStation(
            pcbPlacementMotion,
            io,
            handoff,
            placementSettings,
            events,
            new PcbAligner(
                pcbPlacementMotion,
                placementSettings,
                alignmentCamera,
                light,
                lighting));
        var placementRecipe = new PcbPlacementRecipe();
        var handoffPositionReached = 0;
        pcbPlacementMotion.PositionChanged += (x, y, z) =>
        {
            var fiducial = placementRecipe.Fiducial1Position;
            if (x == fiducial.X
                && y == fiducial.Y
                && z == fiducial.Z
                && io.GetInput(InputIo.PcbSupplyRotationHandoff))
            {
                handoffPositionReached++;
            }
        };

        pcbFeeder.Initialize();
        pcbPlacement.Initialize();
        io.SetInput(InputIo.PcbPlacementCarrierJigPresent, true);

        using var feederCancellation = new CancellationTokenSource();
        var feederTask = pcbFeeder.RunAsync(
            new PcbSupplyRecipe(),
            feederCancellation.Token);

        var carrierJig = await pcbPlacement.ProcessAsync(
            placementRecipe,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(carrierJig.Pcb1Present);
        Assert.False(carrierJig.Pcb2Present);
        Assert.Equal(0, alignmentCamera.CaptureCount);
        if (supplyPcbPresent)
        {
            Assert.True(handoffPositionReached >= 2);
        }
        else
        {
            Assert.Equal(0, handoffPositionReached);
        }
        Assert.Empty(light.ActiveChannels);
        Assert.Equal(motionSettings.SafeZ, pcbPlacementMotion.GetPosition().Z);

        feederCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await feederTask);

        Assert.True(io.GetInput(InputIo.PcbSupplyRotationHome));
        Assert.False(io.GetInput(InputIo.PcbSupplyRotationHandoff));
        Assert.False(io.GetInput(InputIo.PcbSupplyCarrierAvailable));
        Assert.False(io.GetOutput(OutputIo.PcbSupplyReady));

        pcbFeeder.Stop();
    }

    [Fact]
    public async Task DetectedPcbWithMissingFiducialRaisesAlarm()
    {
        var io = new VirtualIoService();
        io.Initialize();

        var motionSettings = new StationMotionSettings
        {
            HorizontalSpeed = 10_000,
            SpeedZ = 10_000,
        };
        using var supplyMotion =
            new VirtualMotionService(motionSettings, hasY: false);
        using var placementMotion =
            new VirtualMotionService(motionSettings);
        using var camera = new VirtualCameraStreamService(inspection: false)
        {
            FiducialPresent = false,
        };
        var light = new VirtualLightController();
        var lighting = new LightingSettings();
        var events = new ProcessEvents();
        var handoff = new PcbHandoff();
        var placementSettings =
            new PcbPlacementSettings { Motion = motionSettings };
        var feeder = new PcbFeeder(
            supplyMotion,
            io,
            handoff,
            new PcbSupplySettings { Motion = motionSettings },
            events);
        var placement = new PcbPlacementStation(
            placementMotion,
            io,
            handoff,
            placementSettings,
            events,
            new PcbAligner(
                placementMotion,
                placementSettings,
                camera,
                light,
                lighting));

        feeder.Initialize();
        placement.Initialize();
        io.SetInput(InputIo.PcbPlacementCarrierJigPresent, true);

        using var cancellation = new CancellationTokenSource();
        var feederTask = feeder.RunAsync(
            new PcbSupplyRecipe(),
            cancellation.Token);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await placement.ProcessAsync(
                new PcbPlacementRecipe(),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("fiducials were not found", exception.Message);
        Assert.Equal(1, camera.CaptureCount);
        Assert.True(io.GetInput(InputIo.PcbPlacementPcbPresent));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await feederTask);
        feeder.Stop();
    }

    [Fact]
    public async Task MissingPcbsSkipBoltFasteningAndInspection()
    {
        var io = new VirtualIoService();
        io.Initialize();

        var motionSettings = new StationMotionSettings
        {
            HorizontalSpeed = 10_000,
            SpeedZ = 10_000,
        };
        using var boltMotion =
            new VirtualMotionService(motionSettings);
        using var inspectionMotion =
            new VirtualMotionService(motionSettings);
        using var inspectionCamera = new VirtualCameraStreamService(inspection: true);
        var light = new VirtualLightController();
        var lighting = new LightingSettings();
        var events = new ProcessEvents();
        var standardHead = new VirtualBoltService();
        var loctiteHead = new VirtualBoltService();
        var boltFastening = new BoltFasteningStation(
            boltMotion,
            io,
            new BoltFasteningSettings { Motion = motionSettings },
            events,
            standardHead,
            loctiteHead);
        var inspection = new InspectionStation(
            inspectionMotion,
            io,
            new InspectionSettings { Motion = motionSettings },
            events,
            inspectionCamera,
            light,
            lighting);
        var carrierJig = new CarrierJigState(false, false);

        boltFastening.Initialize();
        inspection.Initialize();
        io.SetInput(InputIo.BoltFasteningCarrierJigPresent, true);
        await boltFastening.ProcessAsync(
            new BoltFasteningRecipe(),
            carrierJig,
            CancellationToken.None);

        io.SetInput(InputIo.InspectionCarrierJigPresent, true);
        var result = await inspection.ProcessAsync(
            new InspectionRecipe(),
            carrierJig,
            CancellationToken.None);

        Assert.Equal(0, standardHead.SupplyCount);
        Assert.Equal(0, standardHead.TightenCount);
        Assert.Equal(0, loctiteHead.SupplyCount);
        Assert.Equal(0, loctiteHead.TightenCount);
        Assert.Equal(0, inspectionCamera.CaptureCount);
        Assert.Equal(InspectionResult.Missing, result.Pcb1.Result);
        Assert.Equal(InspectionResult.Missing, result.Pcb2.Result);
        Assert.Equal(InspectionResult.Ng, result.OverallResult);
        Assert.Equal(1, inspection.NgStackCount);
    }

    [Fact]
    public async Task BoltHeadsShareMotionAndUseTheirOwnOffsets()
    {
        var io = new VirtualIoService();
        io.Initialize();
        io.SetInput(InputIo.BoltFasteningCarrierJigPresent, true);

        var standardHead = new VirtualBoltService();
        var loctiteHead = new VirtualBoltService();
        var settings = new BoltFasteningSettings
        {
            Motion = new StationMotionSettings
            {
                HorizontalSpeed = 10_000,
                SpeedZ = 10_000,
                SafeZ = -5,
            },
            StandardHead = new BoltHeadSettings { OffsetX = 1, OffsetY = 2 },
            LoctiteHead = new BoltHeadSettings { OffsetX = 4, OffsetY = 5 },
        };
        using var motion = new VirtualMotionService(settings.Motion);
        var station = new BoltFasteningStation(
            motion,
            io,
            settings,
            new ProcessEvents(),
            standardHead,
            loctiteHead);
        var recipe = new BoltFasteningRecipe
        {
            Pcb1Reference = new AxisPos { X = 100, Y = 200 },
            BoltPoints =
            [
                new BoltPoint
                {
                    Number = 1,
                    BoltType = BoltType.Standard,
                    X = 1,
                    Y = 2,
                    Z = 3,
                    TargetTorqueNm = 1,
                },
                new BoltPoint
                {
                    Number = 2,
                    BoltType = BoltType.Loctite,
                    X = 10,
                    Y = 20,
                    Z = 3,
                    TargetTorqueNm = 2,
                },
            ],
        };

        station.Initialize();
        await station.PrepareAsync(CancellationToken.None);
        await motion.MoveToZAsync(8, settings.Motion.SpeedZ);
        var previous = motion.GetPosition();
        var unsafeXyMove = false;
        motion.PositionChanged += (x, y, z) =>
        {
            if ((x != previous.X || y != previous.Y)
                && z != settings.Motion.SafeZ)
            {
                unsafeXyMove = true;
            }

            previous = (x, y, z);
        };
        await station.ProcessAsync(
            recipe,
            new CarrierJigState(true, false),
            CancellationToken.None);

        Assert.Equal(1, standardHead.SupplyCount);
        Assert.Equal(1, standardHead.TightenCount);
        Assert.Equal(1, loctiteHead.SupplyCount);
        Assert.Equal(1, loctiteHead.TightenCount);
        Assert.False(unsafeXyMove);
        Assert.Equal((106, 215, -5), motion.GetPosition());
    }

    [Fact]
    public async Task StationsProcessCarrierJigsAcrossOccupiedStations()
    {
        var io = new VirtualIoService();
        io.Initialize();

        const double safeZ = -5;
        var pcbSupplyMotionSettings = new StationMotionSettings
        {
            HorizontalSpeed = 10_000,
            SpeedZ = 10_000,
            SafeZ = safeZ,
        };
        var placementSettings = new PcbPlacementSettings
        {
            Motion = new StationMotionSettings
            {
                HorizontalSpeed = 10_000,
                SpeedZ = 10_000,
                SafeZ = safeZ,
            },
        };
        var boltSettings = new BoltFasteningSettings
        {
            Motion = new StationMotionSettings
            {
                HorizontalSpeed = 10_000,
                SpeedZ = 10_000,
                SafeZ = safeZ,
            },
        };
        var inspectionSettings = new InspectionSettings
        {
            Motion = new StationMotionSettings
            {
                HorizontalSpeed = 10_000,
                SpeedZ = 10_000,
                SafeZ = safeZ,
            },
        };
        using var pcbSupplyMotion = new VirtualMotionService(
            pcbSupplyMotionSettings,
            hasY: false);
        using var pcbPlacementMotion = new VirtualMotionService(placementSettings.Motion);
        using var boltFasteningMotion = new VirtualMotionService(boltSettings.Motion);
        using var inspectionMotion = new VirtualMotionService(inspectionSettings.Motion);
        using var alignmentCamera = new VirtualCameraStreamService(inspection: false);
        using var inspectionCamera = new VirtualCameraStreamService(inspection: true);
        var light = new VirtualLightController();
        var lighting = new LightingSettings();
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

            if (stage == ProcessStage.PositionPcbPlacementCarrierJig)
            {
                pcbPlacementPositioned =
                    io.GetOutput(OutputIo.PcbPlacementBackupPlateUp)
                    && io.GetInput(InputIo.PcbPlacementBackupPlateUp)
                    && !io.GetOutput(OutputIo.PcbPlacementStopperUp);
            }
            else if (stage == ProcessStage.PositionBoltFasteningCarrierJig)
            {
                boltFasteningPositioned =
                    io.GetOutput(OutputIo.BoltFasteningBackupPlateUp)
                    && io.GetInput(InputIo.BoltFasteningBackupPlateUp)
                    && !io.GetOutput(OutputIo.BoltFasteningStopperUp);
            }
            else if (stage == ProcessStage.PositionInspectionCarrierJig)
            {
                inspectionPositioned =
                    io.GetOutput(OutputIo.InspectionBackupPlateUp)
                    && io.GetInput(InputIo.InspectionBackupPlateUp)
                    && !io.GetOutput(OutputIo.InspectionStopperUp);
            }
        };

        var handoff = new PcbHandoff();
        var supplySettings = new PcbSupplySettings
        {
            Motion = pcbSupplyMotionSettings,
        };
        var pcbSupplyUnsafeXy = TrackUnsafeXyMotion(pcbSupplyMotion, safeZ);
        var pcbPlacementUnsafeXy = TrackUnsafeXyMotion(pcbPlacementMotion, safeZ);
        var boltFasteningUnsafeXy = TrackUnsafeXyMotion(boltFasteningMotion, safeZ);
        var inspectionUnsafeXy = TrackUnsafeXyMotion(inspectionMotion, safeZ);
        var pcbFeeder = new PcbFeeder(
            pcbSupplyMotion,
            io,
            handoff,
            supplySettings,
            events);
        var pcbPlacement = new PcbPlacementStation(
            pcbPlacementMotion,
            io,
            handoff,
            placementSettings,
            events,
            new PcbAligner(
                pcbPlacementMotion,
                placementSettings,
                alignmentCamera,
                light,
                lighting));
        var boltFastening = new BoltFasteningStation(
            boltFasteningMotion,
            io,
            boltSettings,
            events,
            new VirtualBoltService(),
            new VirtualBoltService());
        var inspection = new InspectionStation(
            inspectionMotion,
            io,
            inspectionSettings,
            events,
            inspectionCamera,
            light,
            lighting);

        pcbFeeder.Initialize();
        pcbPlacement.Initialize();
        boltFastening.Initialize();
        inspection.Initialize();
        light.Initialize();
        conveyor.Initialize();
        using var feederCancellation = new CancellationTokenSource();
        var feederTask = pcbFeeder.RunAsync(
            new PcbSupplyRecipe(),
            feederCancellation.Token);

        await conveyor.ReceiveAsync(
            pcbPlacement.CarrierJigPositioner,
            CancellationToken.None);
        var carrierJig = await pcbPlacement.ProcessAsync(
            new PcbPlacementRecipe(),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(carrierJig.Pcb1Present);
        Assert.True(carrierJig.Pcb2Present);
        Assert.Equal(4, alignmentCamera.CaptureCount);
        Assert.Empty(light.ActiveChannels);
        await conveyor.TransferAsync(
            pcbPlacement.CarrierJigPositioner,
            boltFastening.CarrierJigPositioner,
            CancellationToken.None);
        await conveyor.ReceiveAsync(
            pcbPlacement.CarrierJigPositioner,
            CancellationToken.None);
        Assert.True(io.GetInput(InputIo.PcbPlacementCarrierJigPresent));
        Assert.True(io.GetInput(InputIo.BoltFasteningCarrierJigPresent));
        Assert.False(io.GetInput(InputIo.InspectionCarrierJigPresent));

        await boltFastening.PrepareAsync(CancellationToken.None);
        await boltFastening.ProcessAsync(
            new BoltFasteningRecipe { BoltPoints = [] },
            carrierJig,
            CancellationToken.None);
        await conveyor.TransferAsync(
            boltFastening.CarrierJigPositioner,
            inspection.CarrierJigPositioner,
            CancellationToken.None);
        Assert.True(io.GetInput(InputIo.PcbPlacementCarrierJigPresent));
        Assert.False(io.GetInput(InputIo.BoltFasteningCarrierJigPresent));
        Assert.True(io.GetInput(InputIo.InspectionCarrierJigPresent));

        var inspectionProcess = inspection.ProcessAsync(
            new InspectionRecipe(),
            carrierJig,
            CancellationToken.None);
        var nextPcbPlacementProcess = pcbPlacement.ProcessAsync(
            new PcbPlacementRecipe(),
            CancellationToken.None);

        await nextPcbPlacementProcess.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(8, alignmentCamera.CaptureCount);
        Assert.Empty(light.ActiveChannels);
        await conveyor.TransferAsync(
            pcbPlacement.CarrierJigPositioner,
            boltFastening.CarrierJigPositioner,
            CancellationToken.None);
        await conveyor.ReceiveAsync(
            pcbPlacement.CarrierJigPositioner,
            CancellationToken.None);

        Assert.True(io.GetInput(InputIo.PcbPlacementCarrierJigPresent));
        Assert.True(io.GetInput(InputIo.BoltFasteningCarrierJigPresent));
        Assert.True(io.GetInput(InputIo.InspectionCarrierJigPresent));

        var inspectionResult = await inspectionProcess;
        if (inspectionResult.OverallResult == InspectionResult.Good)
        {
            await conveyor.SendAsync(
                inspection.CarrierJigPositioner,
                CancellationToken.None);
        }

        Assert.True(io.GetInput(InputIo.PcbPlacementCarrierJigPresent));
        Assert.True(io.GetInput(InputIo.BoltFasteningCarrierJigPresent));
        Assert.False(io.GetInput(InputIo.InspectionCarrierJigPresent));

        Assert.False(stageFailed);
        Assert.True(pcbPlacementPositioned);
        Assert.True(boltFasteningPositioned);
        Assert.True(inspectionPositioned);
        Assert.Equal(2, inspectionCamera.CaptureCount);
        Assert.False(io.GetOutput(OutputIo.PcbPlacementStopperUp));
        Assert.False(io.GetOutput(OutputIo.BoltFasteningStopperUp));
        Assert.False(io.GetOutput(OutputIo.InspectionStopperUp));
        Assert.False(io.GetOutput(OutputIo.PcbPlacementBackupPlateUp));
        Assert.False(io.GetOutput(OutputIo.BoltFasteningBackupPlateUp));
        Assert.False(io.GetOutput(OutputIo.InspectionBackupPlateUp));
        Assert.False(io.GetInput(InputIo.PcbPlacementBackupPlateUp));
        Assert.False(io.GetInput(InputIo.BoltFasteningBackupPlateUp));
        Assert.False(io.GetInput(InputIo.InspectionBackupPlateUp));
        Assert.False(conveyorServo.IsRunning);
        Assert.False(io.GetOutput(OutputIo.MainLaneDownstreamBoardAvailable));
        Assert.False(pcbSupplyUnsafeXy());
        Assert.False(pcbPlacementUnsafeXy());
        Assert.False(boltFasteningUnsafeXy());
        Assert.False(inspectionUnsafeXy());

        feederCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await feederTask);
        pcbFeeder.Stop();
    }

    private static Func<bool> TrackUnsafeXyMotion(
        VirtualMotionService motion,
        double safeZ)
    {
        var previous = motion.GetPosition();
        var unsafeMove = false;
        motion.PositionChanged += (x, y, z) =>
        {
            if ((x != previous.X || y != previous.Y) && z != safeZ)
            {
                unsafeMove = true;
            }

            previous = (x, y, z);
        };
        return () => unsafeMove;
    }
}
