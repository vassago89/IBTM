using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbPlacement;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTest;

namespace IBTM.Virtual.Tests;

public sealed partial class ConveyorTests
{
    [Theory]
    [InlineData(InputIo.PcbPlacementHeatSink1Present, InputIo.PcbPlacementHeatSink2Present)]
    [InlineData(InputIo.BoltFasteningHeatSink1Present, InputIo.BoltFasteningHeatSink2Present)]
    [InlineData(InputIo.InspectionHeatSink1Present, InputIo.InspectionHeatSink2Present)]
    public async Task HeatSinkPresenceReportsOnlyCombinedCarrierEdges(InputIo heatSink1, InputIo heatSink2)
    {
        var io = CreateIo();
        var station = heatSink1 switch
        {
            InputIo.PcbPlacementHeatSink1Present => ConveyorStation.CreatePcbPlacement(io),
            InputIo.BoltFasteningHeatSink1Present => ConveyorStation.CreateBoltFastening(io),
            _ => ConveyorStation.CreateInspection(io),
        };
        var edges = new List<bool>();
        station.CarrierChanged += edges.Add;
        var work = new PcbPlacementWork(station);
        Assert.False(station.CarrierPresent);
        var arrival = station.WaitForCarrierAsync(default);
        io.SetInputs((heatSink1, true), (heatSink2, true));
        await arrival.WaitAsync(TimeSpan.FromSeconds(1));
        var job = work.CurrentJob;
        work.Complete(job);

        io.SetInput(heatSink1, false);
        Assert.True(station.CarrierPresent);
        Assert.Same(job, work.CurrentJob);
        Assert.True(work.Completed);
        io.SetInputs((heatSink1, true), (heatSink2, false));
        Assert.True(station.CarrierPresent);
        Assert.Same(job, work.CurrentJob);
        Assert.True(work.Completed);
        Assert.Equal(new[] { true }, edges);

        io.SetInput(heatSink1, false);
        Assert.False(station.CarrierPresent);
        arrival = station.WaitForCarrierAsync(default);
        io.SetInput(heatSink2, true);
        await arrival.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.NotSame(job, work.CurrentJob);
        Assert.False(work.Completed);
        Assert.Equal(new[] { true, false, true }, edges);
    }

    [Fact]
    public void DepartedWorkCannotCompleteNewCarrierAndTransferKeepsOriginalLoad()
    {
        var io = CreateIo();
        var source = new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io));
        var destination = CreateInspectionWork(io);
        io.Initialize();
        VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        var departing = source.CurrentJob;
        var original = source.GetAssembly(departing, HeatSinkSlot.HeatSink1);
        original.RecordPcbBolt(1, new BoltResult(false, 1.25));
        source.Complete(departing);

        VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
        VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        var replacement = source.GetAssembly(HeatSinkSlot.HeatSink2);
        Assert.Throws<InvalidOperationException>(() => source.Complete(departing));
        Assert.Throws<InvalidOperationException>(() => source.GetAssembly(departing, HeatSinkSlot.HeatSink1));
        Assert.False(source.Completed);

        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        source.TransferAssembliesTo(destination, departing);
        Assert.Equal(departing.Id, destination.CurrentJob.Id);
        Assert.NotSame(departing, destination.CurrentJob);
        Assert.Same(original, Assert.Single(destination.Assemblies));
        Assert.True(destination.HasNg);
        Assert.False(destination.Completed);
        Assert.Same(replacement, Assert.Single(source.Assemblies));
    }

    [Fact]
    public void HeatSinkInputChangesDoNotEraseCompletionOrTransferredNg()
    {
        var io = CreateIo();
        var source = new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io));
        var destination = CreateInspectionWork(io);
        io.Initialize();
        VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        var assembly = source.GetAssembly(HeatSinkSlot.HeatSink1);
        var result = new BoltResult(false, 1.25);
        assembly.RecordPcbBolt(1, result);
        source.Complete(source.CurrentJob);
        var changes = 0;
        source.Changed += () => changes++;

        io.SetInputs((InputIo.BoltFasteningHeatSink1Present, false), (InputIo.BoltFasteningHeatSink2Present, true));

        Assert.Equal(2, changes);
        Assert.True(source.Completed);
        Assert.True(source.HasNg);
        Assert.Same(result, assembly.PcbBoltResults[1]);

        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.InspectionHeatSink1Present, false);
        io.SetInput(InputIo.InspectionHeatSink2Present, true);
        source.TransferAssembliesTo(destination, source.CurrentJob);

        Assert.Empty(source.Assemblies);
        Assert.Same(assembly, Assert.Single(destination.Assemblies));
        Assert.False(destination.Completed);
        Assert.True(destination.HasNg);
        assembly.CompleteInspection();
        destination.Complete(destination.CurrentJob);
        io.SetInputs((InputIo.InspectionHeatSink1Present, true), (InputIo.InspectionHeatSink2Present, false));
        Assert.True(destination.Completed);
        Assert.True(destination.HasNg);

        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        Assert.Empty(destination.Assemblies);
        Assert.False(destination.Completed);
        Assert.False(destination.HasNg);
    }

    [Fact]
    public void DestinationSensorDoesNotMoveWorkWithoutTransfer()
    {
        var virtualIo = CreateIo();
        IIoService io = virtualIo;
        var placementWork = new PcbPlacementWork(ConveyorStation.CreatePcbPlacement(io));
        var boltWork = new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io));
        _ = new MainConveyor(
            io,
            new ConveyorSettings { CarrierStopDelaySeconds = 0 },
            new OperationCancellation(),
            placementWork,
            boltWork,
            CreateInspectionWork(io),
            routeInspectionToNg: () => false);

        io.Initialize();
        var assembly = placementWork.GetAssembly(HeatSinkSlot.HeatSink1);
        VirtualTest.SetCarrier(virtualIo, InputIo.BoltFasteningHeatSink1Present, true);

        Assert.Same(assembly, Assert.Single(placementWork.Assemblies));
        Assert.Empty(boltWork.Assemblies);
    }

    [Fact]
    public void StationWorkReadsCurrentUnitSetting()
    {
        var virtualIo = CreateIo();
        IIoService io = virtualIo;
        var enabled = false;
        var placementWork = new PcbPlacementWork(ConveyorStation.CreatePcbPlacement(io), () => enabled);

        io.Initialize();
        Assert.False(placementWork.Completed);
        VirtualTest.SetCarrier(virtualIo, InputIo.PcbPlacementHeatSink1Present, true);
        virtualIo.SetInput(InputIo.PcbPlacementHeatSink1Present, true);

        Assert.False(placementWork.Completed);
        placementWork.Complete(placementWork.CurrentJob); // Skipping a disabled station must not create a production completion.
        enabled = true;
        Assert.False(placementWork.Completed);
        enabled = false;
        virtualIo.SetInput(InputIo.PcbPlacementBackupPlateDown, false);
        virtualIo.SetInput(InputIo.PcbPlacementBackupPlateUp, true);
        Assert.True(placementWork.Completed);
        enabled = true;
        Assert.False(placementWork.Completed);
        enabled = false;
        Assert.True(placementWork.Completed);

        virtualIo.SetInput(InputIo.PcbPlacementBackupPlateDown, true);
        Assert.False(placementWork.Completed); // Contradictory feedback is not a completed rise.
        virtualIo.SetInput(InputIo.PcbPlacementBackupPlateDown, false);
        VirtualTest.SetCarrier(virtualIo, InputIo.PcbPlacementHeatSink1Present, false);
        Assert.False(placementWork.Completed);
    }

    [Fact]
    public void DisabledInspectionCanTransferCarrierAlreadyPresentAtStartup()
    {
        var io = CreateIo();
        io.Initialize();
        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.InspectionBackupPlateDown, false);
        io.SetInput(InputIo.InspectionBackupPlateUp, true);
        io.SetInput(InputIo.InspectionStopperUp, false);
        io.SetInput(InputIo.InspectionStopperDown, true);

        var work = CreateInspectionWork(io,
            isEnabled: () => false);

        Assert.True(work.Station.CarrierSeated);
        Assert.True(work.CanTransfer);
        Assert.True(work.RouteToNg);
        Assert.False(work.HasNg);
        work.GetAssembly(HeatSinkSlot.HeatSink1).RecordPcbBolt(1, new BoltResult(false, 1.25));
        Assert.True(work.HasNg);

        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
        Assert.False(work.CanTransfer);
        Assert.False(work.HasNg);
    }
}
