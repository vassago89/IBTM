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
    [Fact]
    public void FirstPresenceNotificationAfterIoStartupKeepsTheExistingCarrierResults()
    {
        var io = CreateIo();
        io.IsReady = false;
        io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
        var station = ConveyorStation.CreatePcbPlacement(io);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadingPresenceFromAnEarlierInputSubscriberDoesNotConsumeTheArrival(bool presentAtStartup)
    {
        var io = CreateIo();
        io.SetInput(InputIo.PcbPlacementHeatSink1Present, presentAtStartup);
        ConveyorStation? station = null;
        io.InputChanged += (input, value) =>
        {
            if (station is not null)
                _ = station.CarrierPresent;
        };
        station = ConveyorStation.CreatePcbPlacement(io);
        var edges = new List<bool>();
        station.CarrierChanged += edges.Add;
        var initialJob = station.CurrentJob;

        io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);

        if (presentAtStartup)
        {
            Assert.Empty(edges);
            Assert.Same(initialJob, station.CurrentJob);
        }
        else
        {
            Assert.Equal(new[] { true }, edges);
            Assert.NotSame(initialJob, station.CurrentJob);
        }
    }

    [Fact]
    public void TransferRejectsReplacedDestinationWithoutChangingEitherJob()
    {
        var io = CreateIo();
        var source = ConveyorStation.CreateBoltFastening(io);
        var destination = ConveyorStation.CreateInspection(io);
        io.Initialize();
        SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        var departing = source.CurrentJob;
        var original = source.GetAssembly(departing, HeatSinkSlot.HeatSink1);
        original.RecordBolt(FasteningHead.Shooting, 1, new(false, 1.25));
        SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        var arrived = destination.CurrentJob;

        SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
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
        Assert.False(station.CarrierPresent);
        var arrival = station.WaitForCarrierAsync(default);
        io.SetInputs((heatSink1, true), (heatSink2, true));
        await arrival.WaitAsync(TimeSpan.FromSeconds(1));
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
        Assert.Equal(new[] { true }, edges);

        io.SetInput(heatSink1, false);
        Assert.False(station.CarrierPresent);
        arrival = station.WaitForCarrierAsync(default);
        io.SetInput(heatSink2, true);
        await arrival.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.NotSame(job, station.CurrentJob);
        Assert.False(station.Completed);
        Assert.Equal(new[] { true, false, true }, edges);
    }

    [Fact]
    public void DepartedWorkCannotCompleteNewCarrierAndTransferKeepsOriginalLoad()
    {
        var io = CreateIo();
        var source = ConveyorStation.CreateBoltFastening(io);
        var destination = CreateInspectionStation(io);
        io.Initialize();
        VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        var departing = source.CurrentJob;
        var original = source.GetAssembly(departing, HeatSinkSlot.HeatSink1);
        original.RecordBolt(FasteningHead.Shooting, 1, new BoltResult(false, 1.25));
        source.Complete(departing);

        VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
        VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        var replacement = source.GetAssembly(HeatSinkSlot.HeatSink2);
        Assert.Throws<InvalidOperationException>(() => source.Complete(departing));
        Assert.Throws<InvalidOperationException>(() => source.GetAssembly(departing, HeatSinkSlot.HeatSink1));
        Assert.False(source.Completed);

        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        source.TransferAssembliesTo(destination.Station, departing, destination.Station.CurrentJob);
        Assert.Equal(departing.Id, destination.Station.CurrentJob.Id);
        Assert.NotSame(departing, destination.Station.CurrentJob);
        Assert.Same(original, Assert.Single(destination.Station.Assemblies));
        Assert.True(destination.HasNg);
        Assert.False(destination.Station.Completed);
        Assert.Same(replacement, Assert.Single(source.Assemblies));
    }

    [Fact]
    public void HeatSinkInputChangesDoNotEraseCompletionOrTransferredNg()
    {
        var io = CreateIo();
        var source = ConveyorStation.CreateBoltFastening(io);
        var destination = CreateInspectionStation(io);
        io.Initialize();
        VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        var assembly = source.GetAssembly(HeatSinkSlot.HeatSink1);
        var result = new BoltResult(false, 1.25);
        assembly.RecordBolt(FasteningHead.Shooting, 1, result);
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
        source.TransferAssembliesTo(destination.Station, source.CurrentJob, destination.Station.CurrentJob);

        Assert.Empty(source.Assemblies);
        Assert.Same(assembly, Assert.Single(destination.Station.Assemblies));
        Assert.False(destination.Station.Completed);
        Assert.True(destination.HasNg);
        assembly.CompleteInspection();
        destination.Station.Complete(destination.Station.CurrentJob);
        io.SetInputs((InputIo.InspectionHeatSink1Present, true), (InputIo.InspectionHeatSink2Present, false));
        Assert.True(destination.Station.Completed);
        Assert.True(destination.HasNg);

        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        Assert.Empty(destination.Station.Assemblies);
        Assert.False(destination.Station.Completed);
        Assert.False(destination.HasNg);
    }

    [Fact]
    public void DestinationSensorDoesNotMoveWorkWithoutTransfer()
    {
        var virtualIo = CreateIo();
        IIoService io = virtualIo;
        var placementWork = ConveyorStation.CreatePcbPlacement(io);
        var boltWork = ConveyorStation.CreateBoltFastening(io);
        _ = new MainConveyor(
            io,
            new ConveyorSettings { CarrierStopDelaySeconds = 0 },
            new OperationCancellation(),
            placementWork,
            boltWork,
            CreateInspectionStation(io),
            new UnitSettings { Inspection = false });

        io.Initialize();
        var assembly = placementWork.GetAssembly(HeatSinkSlot.HeatSink1);
        VirtualTest.SetCarrier(virtualIo, InputIo.BoltFasteningHeatSink1Present, true);

        Assert.Same(assembly, Assert.Single(placementWork.Assemblies));
        Assert.Empty(boltWork.Assemblies);
    }

    [Fact]
    public void StationCompletionBelongsToCurrentCarrierUntilReplacement()
    {
        var virtualIo = CreateIo();
        IIoService io = virtualIo;
        var placementWork = ConveyorStation.CreatePcbPlacement(io);

        io.Initialize();
        Assert.False(placementWork.Completed);
        VirtualTest.SetCarrier(virtualIo, InputIo.PcbPlacementHeatSink1Present, true);
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
        VirtualTest.SetCarrier(virtualIo, InputIo.PcbPlacementHeatSink1Present, false);
        Assert.False(placementWork.Completed);
        VirtualTest.SetCarrier(virtualIo, InputIo.PcbPlacementHeatSink1Present, true);
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

        var work = CreateInspectionStation(io,
            new UnitSettings { Inspection = false });

        Assert.True(work.Station.CarrierSeated);
        Assert.False(work.IsTransferAllowed);
        work.Station.Complete(work.Station.CurrentJob);
        Assert.True(work.IsTransferAllowed);
        Assert.True(work.RouteToNg);
        Assert.False(work.HasNg);
        work.Station.GetAssembly(HeatSinkSlot.HeatSink1).RecordBolt(FasteningHead.Shooting, 1, new BoltResult(false, 1.25));
        Assert.True(work.HasNg);

        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
        Assert.False(work.IsTransferAllowed);
        Assert.False(work.HasNg);
    }
}
