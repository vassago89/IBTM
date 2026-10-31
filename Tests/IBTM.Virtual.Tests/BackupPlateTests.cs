using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BackupPlate;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTestSupport;

namespace IBTM.Virtual.Tests;

public sealed class BackupPlateTests
{
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
    [InlineData(OutputIo.BoltFasteningBackupPlateUp, OutputIo.BoltFasteningStopperUp,
            InputIo.BoltFasteningBackupPlateUp, InputIo.BoltFasteningBackupPlateDown, 54, 64)]
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
            OutputIo.BoltFasteningBackupPlateUp => BackupPlateStation.CreateBoltFastening(io),
            _ => BackupPlateStation.CreateInspection(io),
        };
        IoHardwareSettings settings = output switch
        {
            OutputIo.PcbPlacementBackupPlateUp => new PcbPlacementStationHardwareSettings(),
            OutputIo.BoltFasteningBackupPlateUp => new BoltFasteningStationHardwareSettings(),
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
        var source = BackupPlateStation.CreateBoltFastening(io);
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
            InputIo.BoltFasteningHeatSink1Present => BackupPlateStation.CreateBoltFastening(io),
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
        var source = BackupPlateStation.CreateBoltFastening(io);
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
