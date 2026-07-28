using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbSupply;
using IBTM.Sequence;
using IBTM.Stations.BoltFastening;
using IBTM.Stations.Inspection;
using IBTM.Stations.PcbPlacement;
using IBTM.Transport;
using IBTM.Virtual;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class VirtualSequenceTests
{
    [Fact]
    public async Task AutoSequenceRunsStationWorkersConcurrently()
    {
        var io = new VirtualIoService();
        var motionSettings = new StationMotionSettings
        {
            HorizontalSpeed = 10_000,
            SpeedZ = 10_000,
        };
        using var pcbSupplyMotion =
            new VirtualMotionService(motionSettings, hasY: false);
        using var pcbPlacementMotion =
            new VirtualMotionService(motionSettings);
        using var boltFasteningMotion =
            new VirtualMotionService(motionSettings);
        using var inspectionMotion =
            new VirtualMotionService(motionSettings);
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
        var boltFastening = new BoltFasteningStation(
            boltFasteningMotion,
            io,
            new BoltFasteningSettings
            {
                Motion = motionSettings,
            },
            events,
            new VirtualBoltService(),
            new VirtualBoltService());
        var inspection = new InspectionStation(
            inspectionMotion,
            io,
            new InspectionSettings
            {
                Motion = motionSettings,
            },
            events,
            inspectionCamera,
            light,
            lighting);
        var options = new MachineOptions
        {
            UseEmergencyStop = true,
            UseDoorInterlock = true,
            UseAirPressureInterlock = true,
            UseTowerLamp = true,
            UseBuzzer = true,
        };
        using var sequence = new AutoSequence(
            io,
            conveyor,
            pcbFeeder,
            pcbPlacement,
            boltFastening,
            inspection,
            light,
            new Dictionary<EquipmentUnit, MotionService>
            {
                [EquipmentUnit.PcbSupply] = pcbSupplyMotion,
                [EquipmentUnit.PcbPlacement] = pcbPlacementMotion,
                [EquipmentUnit.BoltFastening] = boltFasteningMotion,
                [EquipmentUnit.Inspection] = inspectionMotion,
            },
            options,
            events)
        {
            CurrentRecipe = new Recipe
            {
                BoltFastening = new BoltFasteningRecipe { BoltPoints = [] },
            },
        };
        var cycleCompleted = new TaskCompletionSource<ProductionStats>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        events.StatsUpdated += stats => cycleCompleted.TrySetResult(stats);

        sequence.Initialize();
        Assert.True(io.GetOutput(OutputIo.TowerLampYellow));

        var running = sequence.StartAsync();
        var stats = await cycleCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        sequence.Stop();
        await running.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, stats.TotalCount);
        Assert.False(conveyorServo.IsRunning);
        Assert.False(io.GetOutput(OutputIo.MainLaneUpstreamMachineReady));
        Assert.False(io.GetOutput(OutputIo.MainLaneDownstreamBoardAvailable));
        Assert.True(io.GetOutput(OutputIo.TowerLampYellow));
        Assert.False(io.GetOutput(OutputIo.TowerLampRed));
        Assert.False(io.GetOutput(OutputIo.Buzzer));

        io.SetInput(InputIo.EmergencyStopReleased, false);
        await Assert.ThrowsAsync<InvalidOperationException>(sequence.StartAsync);
        Assert.True(io.GetOutput(OutputIo.TowerLampRed));
        Assert.True(io.GetOutput(OutputIo.Buzzer));

        io.SetInput(InputIo.EmergencyStopReleased, true);
        sequence.Reset();
        Assert.True(io.GetOutput(OutputIo.TowerLampYellow));
        Assert.False(io.GetOutput(OutputIo.TowerLampRed));
        Assert.False(io.GetOutput(OutputIo.Buzzer));

        io.SetOutput(OutputIo.PcbPlacementBackupPlateUp, true);
        sequence.Stop();

        Assert.True(io.GetOutput(OutputIo.PcbPlacementBackupPlateUp));
    }
}
