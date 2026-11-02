using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BackupPlate;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;
using IBTM.UI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTestSupport;

namespace IBTM.Virtual.Tests;

public sealed class BackupPlateTests
{
    [Fact]
    public async Task S2UsesMotionPositionAndNeverCommandsTheFormerCylinder()
    {
        var io = CreateIo();
        io.Initialize();
        var station = CreateFasteningBackup(io, seated: false);
        var writes = new List<OutputIo>();
        io.OutputChanged += (output, value) => writes.Add(output);
        io.SetInputs((InputIo.BoltFasteningHeatSink1Present, true),
            (InputIo.BoltFasteningBackupPlateUp, true), (InputIo.BoltFasteningBackupPlateDown, false));
        Assert.Equal(StationCylinderState.Down, station.BackupPlate);
        Assert.False(station.CarrierSeated);

        await station.PrepareToReceiveAsync(default);
        await station.SeatAsync(default);
        Assert.Equal(5, station.Motion!.Position.Z);
        Assert.True(station.CarrierSeated);
        Assert.Equal(StationCylinderState.Down, station.Stopper);
        io.SetInputs((InputIo.BoltFasteningBackupPlateUp, false), (InputIo.BoltFasteningBackupPlateDown, true));
        Assert.True(station.CarrierSeated);
        await station.ReleaseAsync(default);
        Assert.Equal(0, station.Motion.Position.Z);
        Assert.False(station.CarrierSeated);
        Assert.DoesNotContain(OutputIo.BoltFasteningBackupPlateUp, writes);
        Assert.All(writes, output => Assert.Equal(OutputIo.BoltFasteningStopperUp, output));
    }

    [Fact]
    public async Task S2InterruptedMoveDoesNotReportArrivalAndCanReturnToTransferHeight()
    {
        var io = CreateIo();
        io.Initialize();
        var station = CreateFasteningBackup(io, seated: false);
        var motion = (VirtualMotionService)station.Motion!.Feedback;
        using var cancellation = new CancellationTokenSource();
        motion.PositionChanged += (x, y, z) =>
        {
            if (z > 0.1)
                cancellation.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => station.SetBackupPlateUpAsync(true, cancellation.Token));
        Assert.False(motion.IsMoving);
        Assert.Equal(StationCylinderState.Between, station.BackupPlate);
        await station.SetBackupPlateUpAsync(false, default);
        Assert.Equal(StationCylinderState.Down, station.BackupPlate);
        motion.SetServo(MotionAxis.Z, false);
        Assert.Equal(StationCylinderState.Between, station.BackupPlate);
        await Assert.ThrowsAsync<MotionInterlockException>(() => station.SetBackupPlateUpAsync(true, default));
    }

    [Fact]
    public async Task S2UnknownFeedbackAndUnconfiguredHeightsNeverReportSeated()
    {
        var io = CreateIo();
        io.Initialize();
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        var configured = CreateFasteningBackup(io);
        var status = configured.Motion!;
        Assert.True(configured.CarrierSeated);
        status.InvalidateFeedback(new System.IO.IOException("Disconnected"));
        Assert.Equal(StationCylinderState.Between, configured.BackupPlate);
        Assert.False(configured.CarrierSeated);
        var unconfigured = new BackupPlateStation(io, (IAxisMotion)status.Feedback, status, new());
        await Assert.ThrowsAsync<InvalidOperationException>(() => unconfigured.SetBackupPlateUpAsync(true, default));
        Assert.False(unconfigured.CarrierSeated);
    }

    [Fact]
    public async Task S2ConveyorOnlyIncludesBackupResetHomeAndStartPreparation()
    {
        var settings = MachineTestSupport.FlowSettings();
        settings.Units = MachineTestSupport.EnableOnly(MachineTestSupport.MachineUnit.MainConveyor);
        await using var services = MachineTestSupport.CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var station = services.GetRequiredKeyedService<BackupPlateStation>(MotionGroup.BoltFastening);
        await machine.InitializeAsync();
        try
        {
            await machine.ResetAsync();
            await machine.HomeAsync(CancellationToken.None);
            Assert.True(station.Motion!.Feedback.GetAxisState(MotionAxis.Z) is { ServoOn: true, Homed: true }, state.AlarmDetail);
            await WaitUntilAsync(() => station.Motion.Axes[MotionAxis.Z].State is { ServoOn: true, Homed: true });
            io.SetInput(InputIo.AutoMode, false);
            Assert.True(state.AutoMode);
            Assert.True(await machine.PrepareStartAreaAsync(
                StartArea.Station2, StartPreparationAction.MoveBackupToFastening, default));
            Assert.Equal(StationCylinderState.Up, station.BackupPlate);
            Assert.True(await machine.PrepareStartAreaAsync(
                StartArea.Station2, StartPreparationAction.MoveBackupToTransfer, default));
            Assert.Equal(StationCylinderState.Down, station.BackupPlate);
            Assert.False(state.IsError, state.AlarmDetail);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task S2SettingsPersistAndOldCylinderMappingsAreExcluded()
    {
        var settings = new MachineSettings();
        settings.BoltFasteningBackup.TransferHeight = -1.25;
        settings.BoltFasteningBackup.FasteningHeight = 12.375;
        settings.BoltFasteningBackup.Motion.ZSpeed = 3.5;
        settings.Fastech.IpAddress = "192.168.0.7";
        settings.Fastech.BackupAxis.PulsesPerMillimeter = 4000.5;
        settings.BoltFasteningStationHardware.Inputs[InputIo.BoltFasteningBackupPlateUp] = 66;
        settings.BoltFasteningStationHardware.Inputs[InputIo.BoltFasteningBackupPlateDown] = 67;
        settings.BoltFasteningStationHardware.Outputs[OutputIo.BoltFasteningBackupPlateUp] = new() { Number = 54, OffNumber = 55 };
        settings.ConveyorHardware.Inputs[InputIo.BoltFasteningBackupPlateUp] = 166;
        settings.ConveyorHardware.Inputs[InputIo.BoltFasteningBackupPlateDown] = 167;
        settings.ConveyorHardware.Outputs[OutputIo.BoltFasteningBackupPlateUp] = new() { Number = 154, OffNumber = 155 };
        var store = OpenMachineStore();
        store.SaveSettings(settings.Sections);
        var loaded = await MachineSettings.LoadAsync(store);
        Assert.Equal(-1.25, loaded.BoltFasteningBackup.TransferHeight);
        Assert.Equal(12.375, loaded.BoltFasteningBackup.FasteningHeight);
        Assert.Equal(3.5, loaded.BoltFasteningBackup.Motion.ZSpeed);
        Assert.Equal("192.168.0.7", loaded.Fastech.IpAddress);
        Assert.Equal(4000.5, loaded.Fastech.BackupAxis.PulsesPerMillimeter);
        Assert.DoesNotContain(InputIo.BoltFasteningBackupPlateUp, loaded.BoltFasteningStationHardware.Inputs.Keys);
        Assert.DoesNotContain(InputIo.BoltFasteningBackupPlateDown, loaded.BoltFasteningStationHardware.Inputs.Keys);
        Assert.DoesNotContain(OutputIo.BoltFasteningBackupPlateUp, loaded.BoltFasteningStationHardware.Outputs.Keys);
        Assert.DoesNotContain(InputIo.BoltFasteningBackupPlateUp, loaded.ConveyorHardware.Inputs.Keys);
        Assert.DoesNotContain(InputIo.BoltFasteningBackupPlateDown, loaded.ConveyorHardware.Inputs.Keys);
        Assert.DoesNotContain(OutputIo.BoltFasteningBackupPlateUp, loaded.ConveyorHardware.Outputs.Keys);
        Assert.Contains(OutputIo.BoltFasteningStopperUp, loaded.BoltFasteningStationHardware.Outputs.Keys);
    }

    [Fact]
    public async Task S2SettingsCanSaveOneTaughtHeightWithoutAllowingAutomaticMoves()
    {
        var settings = MachineTestSupport.FlowSettings();
        settings.Units = MachineTestSupport.EnableOnly(MachineTestSupport.MachineUnit.MainConveyor);
        await using var services = MachineTestSupport.CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var view = services.GetRequiredService<SettingsViewModel>();
        var store = services.GetRequiredService<MachineStore>();
        var station = services.GetRequiredKeyedService<BackupPlateStation>(MotionGroup.BoltFastening);
        var backup = settings.BoltFasteningBackup;
        backup.TransferHeight = 1.25;
        backup.FasteningHeight = null;
        backup.Motion.ZSpeed = 3.5;
        await machine.InitializeAsync();
        try
        {
            Assert.True(view.IsSettingsEditAllowed);
            await view.SaveSettingsCommand.ExecuteAsync(null);
            Assert.Equal(UiText.Get("Settings saved."), view.DatabaseMessage);
            var saved = store.LoadSettings().Get<BackupPlateSettings>();
            Assert.Equal(1.25, saved.TransferHeight);
            Assert.Null(saved.FasteningHeight);
            Assert.Equal(3.5, saved.Motion.ZSpeed);
            Assert.NotNull(backup.ValidationError);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => station.SetBackupPlateUpAsync(true, default));

            backup.TransferHeight = null;
            backup.Motion.ZSpeed = 0;
            await view.SaveSettingsCommand.ExecuteAsync(null);
            Assert.Contains(UiText.Get(nameof(MotionSettings.ZSpeed)), view.DatabaseMessage);
            saved = store.LoadSettings().Get<BackupPlateSettings>();
            Assert.Equal(1.25, saved.TransferHeight);
            Assert.Equal(3.5, saved.Motion.ZSpeed);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task CarrierInputSettingsKeepSavedKeysAndAddresses()
    {
        var saved = new SavedSettings(new Dictionary<string, string>
        {
            ["PcbPlacementStationHardwareSettings"] = """{"Inputs":{"PcbPlacementHeatSink1Present":154,"PcbPlacementHeatSink2Present":155}}""",
            ["BoltFasteningStationHardwareSettings"] = """{"Inputs":{"BoltFasteningHeatSink1Present":162,"BoltFasteningHeatSink2Present":163}}""",
            ["InspectionStationHardwareSettings"] = """{"Inputs":{"InspectionHeatSink1Present":169,"InspectionHeatSink2Present":170}}""",
        });
        var store = OpenMachineStore();
        store.SaveSettings([
            saved.Get<PcbPlacementStationHardwareSettings>(),
            saved.Get<BoltFasteningStationHardwareSettings>(),
            saved.Get<InspectionStationHardwareSettings>(),
        ]);

        var settings = await MachineSettings.LoadAsync(store);
        Assert.Equal(154, settings.PcbPlacementStationHardware.Inputs[InputIo.PcbPlacementHeatSink1Present]);
        Assert.Equal(155, settings.PcbPlacementStationHardware.Inputs[InputIo.PcbPlacementHeatSink2Present]);
        Assert.Equal(162, settings.BoltFasteningStationHardware.Inputs[InputIo.BoltFasteningHeatSink1Present]);
        Assert.Equal(163, settings.BoltFasteningStationHardware.Inputs[InputIo.BoltFasteningHeatSink2Present]);
        Assert.Equal(169, settings.InspectionStationHardware.Inputs[InputIo.InspectionHeatSink1Present]);
        Assert.Equal(170, settings.InspectionStationHardware.Inputs[InputIo.InspectionHeatSink2Present]);
    }

    [Theory]
    [InlineData(OutputIo.PcbPlacementBackupPlateUp, OutputIo.PcbPlacementStopperUp,
            InputIo.PcbPlacementBackupPlateUp, InputIo.PcbPlacementBackupPlateDown, 50, 57)]
    [InlineData(OutputIo.InspectionBackupPlateUp, OutputIo.InspectionStopperUp,
            InputIo.InspectionBackupPlateUp, InputIo.InspectionBackupPlateDown, 58, 71)]
    public async Task StationCylindersOnRaisesAndOffLowers(
        OutputIo output,
        OutputIo stopper,
        InputIo up,
        InputIo down,
        int channel,
        int stopperDownInput)
    {
        var io = CreateIo();
        io.Initialize();
        var station = output switch
        {
            OutputIo.PcbPlacementBackupPlateUp => BackupPlateStation.CreatePcbPlacement(io),
            _ => BackupPlateStation.CreateInspection(io),
        };
        IoHardwareSettings settings = output switch
        {
            OutputIo.PcbPlacementBackupPlateUp => new PcbPlacementStationHardwareSettings(),
            _ => new InspectionStationHardwareSettings(),
        };
        var hardware = settings.Outputs[output];
        Assert.Equal(channel, hardware.Number);
        Assert.Equal(channel + 1, hardware.OffNumber);
        Assert.Equal(up, hardware.Feedback!.OnInput);
        Assert.Equal(down, hardware.Feedback.OffInput);
        var stopperHardware = settings.Outputs[stopper];
        Assert.Equal(channel - 2, stopperHardware.Number);
        Assert.Equal(channel - 1, stopperHardware.OffNumber);
        Assert.Equal(stopperDownInput + 1, settings.Inputs[stopperHardware.Feedback!.OnInput]);
        Assert.Equal(stopperDownInput, settings.Inputs[stopperHardware.Feedback.OffInput!.Value]);

        await station.PrepareToReceiveAsync(CancellationToken.None);
        Assert.False(io.GetOutput(output));
        Assert.Equal(StationCylinderState.Down, station.BackupPlate);
        Assert.True(io.GetOutput(stopper));
        Assert.Equal(StationCylinderState.Up, station.Stopper);

        var carrier = output switch
        {
            OutputIo.PcbPlacementBackupPlateUp => InputIo.PcbPlacementHeatSink1Present,
            OutputIo.BoltFasteningBackupPlateUp => InputIo.BoltFasteningHeatSink1Present,
            _ => InputIo.InspectionHeatSink1Present,
        };
        io.SetInput(carrier, true);
        Assert.False(station.CarrierSeated);
        await ((IIoService)io).SetOutputAndWaitAsync(output, true);
        Assert.Equal(StationCylinderState.Up, station.Stopper);
        Assert.True(station.CarrierSeated);

        await station.SeatAsync(CancellationToken.None);
        Assert.True(io.GetOutput(output));
        Assert.Equal(StationCylinderState.Up, station.BackupPlate);
        Assert.False(io.GetOutput(stopper));
        Assert.Equal(StationCylinderState.Down, station.Stopper);

        await station.ReleaseAsync(CancellationToken.None);
        Assert.False(io.GetOutput(output));
        Assert.Equal(StationCylinderState.Down, station.BackupPlate);
        Assert.False(io.GetOutput(stopper));
        Assert.Equal(StationCylinderState.Down, station.Stopper);

        await station.PrepareToReceiveAsync(CancellationToken.None);
        io.OutputChanged += (changedOutput, value) =>
        {
            Assert.True(changedOutput == output || changedOutput == stopper);
            if (changedOutput == stopper && !value)
            {
                Assert.Equal(StationCylinderState.Up, station.BackupPlate);
            }
        };

        await station.SeatAsync(CancellationToken.None);
        Assert.True(io.GetOutput(output));
        Assert.Equal(StationCylinderState.Up, station.BackupPlate);
        Assert.False(io.GetOutput(stopper));
        Assert.Equal(StationCylinderState.Down, station.Stopper);
    }

    [Fact]
    public void FirstPresenceNotificationAfterIoStartupKeepsTheExistingCarrierResults()
    {
        var io = CreateIo();
        io.IsReady = false;
        io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
        var station = BackupPlateStation.CreatePcbPlacement(io);
        io.IsReady = true;
        var job = station.CurrentJob;
        var assembly = station.GetAssembly(HeatSinkSlot.HeatSink1);
        station.Complete(job);

        // Physical I/O's initial scan has no arrival notification. A later
        // second sensor change must not replace the carrier already being worked.
        io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);

        Assert.Same(job, station.CurrentJob);
        Assert.Same(assembly, Assert.Single(station.Assemblies));
        Assert.True(station.Completed);
    }

    [Fact]
    public void TransferRejectsReplacedDestinationWithoutChangingEitherJob()
    {
        var io = CreateIo();
        var source = VirtualTestSupport.CreateFasteningBackup(io);
        var destination = BackupPlateStation.CreateInspection(io);
        io.Initialize();
        SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        var departing = source.CurrentJob;
        var original = source.GetAssembly(departing, HeatSinkSlot.HeatSink1);
        original.RecordBolt(FasteningHead.Shooting, VirtualTestSupport.BoltId(1), new(false, 1.25));
        SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        var arrived = destination.CurrentJob;

        SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
        destination.ClearJob();
        SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        var replacementJob = destination.CurrentJob;
        var replacement = destination.GetAssembly(HeatSinkSlot.HeatSink2);
        destination.Complete(replacementJob);
        Assert.Throws<InvalidOperationException>(() =>
            source.TransferAssembliesTo(destination, departing, arrived));

        Assert.Same(departing, source.CurrentJob);
        Assert.Same(original, Assert.Single(source.Assemblies));
        Assert.Same(replacementJob, destination.CurrentJob);
        Assert.Same(replacement, Assert.Single(destination.Assemblies));
        Assert.True(destination.Completed);
    }

    [Theory]
    [InlineData(InputIo.PcbPlacementHeatSink1Present, InputIo.PcbPlacementHeatSink2Present)]
    [InlineData(InputIo.BoltFasteningHeatSink1Present, InputIo.BoltFasteningHeatSink2Present)]
    [InlineData(InputIo.InspectionHeatSink1Present, InputIo.InspectionHeatSink2Present)]
    public void CarrierPresenceChangesPreserveJobUntilExplicitClear(InputIo heatSink1, InputIo heatSink2)
    {
        var io = CreateIo();
        var station = heatSink1 switch
        {
            InputIo.PcbPlacementHeatSink1Present => BackupPlateStation.CreatePcbPlacement(io),
            InputIo.BoltFasteningHeatSink1Present => VirtualTestSupport.CreateFasteningBackup(io),
            _ => BackupPlateStation.CreateInspection(io),
        };
        Assert.False(station.CarrierPresent);
        io.SetInputs((heatSink1, true), (heatSink2, true));
        Assert.True(station.CarrierPresent);
        var job = station.CurrentJob;
        station.Complete(job);

        io.SetInput(heatSink1, false);
        Assert.True(station.CarrierPresent);
        Assert.Same(job, station.CurrentJob);
        Assert.True(station.Completed);
        io.SetInputs((heatSink1, true), (heatSink2, false));
        Assert.True(station.CarrierPresent);
        Assert.Same(job, station.CurrentJob);
        Assert.True(station.Completed);

        io.SetInput(heatSink1, false);
        Assert.False(station.CarrierPresent);
        io.SetInput(heatSink2, true);
        Assert.True(station.CarrierPresent);
        Assert.Same(job, station.CurrentJob);
        Assert.True(station.Completed);
        Assert.Throws<InvalidOperationException>(station.ClearJob);
        io.SetInput(heatSink2, false);
        station.ClearJob();
        Assert.NotSame(job, station.CurrentJob);
        Assert.False(station.Completed);
    }

    [Fact]
    public void DepartedWorkCannotCompleteNewCarrierAndTransferKeepsOriginalLoad()
    {
        var io = CreateIo();
        var source = VirtualTestSupport.CreateFasteningBackup(io);
        var destination = BackupPlateStation.CreateInspection(io);
        io.Initialize();
        VirtualTestSupport.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        var departing = source.CurrentJob;
        var original = source.GetAssembly(departing, HeatSinkSlot.HeatSink1);
        original.RecordBolt(FasteningHead.Shooting, VirtualTestSupport.BoltId(1), new BoltResult(false, 1.25));
        source.Complete(departing);

        VirtualTestSupport.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
        source.ClearJob();
        VirtualTestSupport.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        var replacement = source.GetAssembly(HeatSinkSlot.HeatSink2);
        Assert.Throws<InvalidOperationException>(() => source.Complete(departing));
        Assert.Throws<InvalidOperationException>(() => source.GetAssembly(departing, HeatSinkSlot.HeatSink1));
        Assert.False(source.Completed);

        VirtualTestSupport.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        source.TransferAssembliesTo(destination, departing, destination.CurrentJob);
        Assert.Equal(departing.Id, destination.CurrentJob.Id);
        Assert.NotSame(departing, destination.CurrentJob);
        Assert.Same(original, Assert.Single(destination.Assemblies));
        Assert.Equal(AssemblyResult.Ng, Assert.Single(destination.Assemblies).Result);
        Assert.False(destination.Completed);
        Assert.Same(replacement, Assert.Single(source.Assemblies));
    }

    [Fact]
    public void StationCompletionSurvivesSensorEdgesUntilEmptyWorkIsCleared()
    {
        var virtualIo = CreateIo();
        IIoService io = virtualIo;
        var placementWork = BackupPlateStation.CreatePcbPlacement(io);

        io.Initialize();
        Assert.False(placementWork.Completed);
        VirtualTestSupport.SetCarrier(virtualIo, InputIo.PcbPlacementHeatSink1Present, true);
        virtualIo.SetInput(InputIo.PcbPlacementHeatSink1Present, true);

        Assert.False(placementWork.Completed);
        virtualIo.SetInput(InputIo.PcbPlacementBackupPlateDown, false);
        virtualIo.SetInput(InputIo.PcbPlacementBackupPlateUp, true);
        Assert.False(placementWork.Completed); // Physical seating alone does not complete station work.
        placementWork.Complete(placementWork.CurrentJob);
        Assert.True(placementWork.Completed);

        virtualIo.SetInput(InputIo.PcbPlacementBackupPlateDown, true);
        Assert.False(placementWork.CarrierSeated);
        Assert.True(placementWork.Completed); // Position changes do not erase the owned result.
        virtualIo.SetInput(InputIo.PcbPlacementBackupPlateDown, false);
        VirtualTestSupport.SetCarrier(virtualIo, InputIo.PcbPlacementHeatSink1Present, false);
        Assert.False(placementWork.Completed);
        VirtualTestSupport.SetCarrier(virtualIo, InputIo.PcbPlacementHeatSink1Present, true);
        Assert.True(placementWork.Completed);
        VirtualTestSupport.SetCarrier(virtualIo, InputIo.PcbPlacementHeatSink1Present, false);
        placementWork.ClearJob();
        VirtualTestSupport.SetCarrier(virtualIo, InputIo.PcbPlacementHeatSink1Present, true);
        Assert.False(placementWork.Completed);
    }

    private static VirtualIoService CreateIo()
    {
        return new(Outputs(new PcbPlacementStationHardwareSettings(),
            new BoltFasteningStationHardwareSettings(), new InspectionStationHardwareSettings()), new());
    }
}
