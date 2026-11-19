using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BackupPlate;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.Mlcp;
using IBTM.NgConveyor;
using IBTM.Storage;
using IBTM.UI;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTestSupport;

namespace IBTM.Virtual.Tests;

public sealed class HeightInspectionTests
{
    [Fact]
    public void ReferenceTeachingAndLimitsSaveWithoutChangingBoltCoordinates()
    {
        var store = OpenMachineStore();
        var settings = new MachineSettings();
        var recipes = new RecipeManager(store, new());
        var bolt = new BoltPoint { X = 3, Y = 4, FasteningX = 5, FasteningY = 6, MinimumHeight = 1.4, MaximumHeight = 1.6 };
        recipes.Current.Pcb.BoltPoints = [bolt];
        var target = new TeachingPosition(TeachingTarget.HeightReference1, MotionGroup.InspectionGantry, TeachMode.XYOnly);
        var first = new TeachingPoint(target, settings, recipes, HeatSinkSlot.HeatSink1);
        var second = new TeachingPoint(target, settings, recipes, HeatSinkSlot.HeatSink2);
        Assert.Null(first.Coordinates);
        first.Teach(10, 11, 0);
        second.Teach(20, 21, 0);
        Assert.Equal(10, first.Coordinates!.X);
        Assert.Equal(20, second.Coordinates!.X);
        Assert.Equal(TeachingStorage.Recipe, first.Storage);
        store.SaveRecipe(recipes.Current);
        var edited = store.LoadRecipe(recipes.Current.Name);
        edited.Pcb.BoltPoints[0].MaximumHeight = 1.7;
        // The inspection-only Save updates limits, not taught positions.
        edited.Pcb.BoltPoints[0].X = 99;
        edited.BoltInspection.HeightReference1.Point1 = new() { X = 99 };
        store.SaveInspectionSettings(edited);
        var loaded = new MachineStore(store.DatabaseFile).LoadRecipe(recipes.Current.Name);
        Assert.Equal(10, loaded.BoltInspection.HeightReference1.Point1!.X);
        Assert.Equal(20, loaded.BoltInspection.HeightReference2.Point1!.X);
        var savedBolt = Assert.Single(loaded.Pcb.BoltPoints);
        Assert.Equal((3d, 4d, 5d, 6d), (savedBolt.X, savedBolt.Y, savedBolt.FasteningX, savedBolt.FasteningY));
        Assert.Equal(1.7, savedBolt.MaximumHeight);
    }

    [Fact]
    public void ReferencePlaneRemovesTiltAndRejectsCollinearPoints()
    {
        // z = 10 + 0.1x + 0.2y, at three points of the same surface.
        var plane = new HeightReferencePlane(DateTimeOffset.Now,
            new(0, 0, 10), new(10, 0, 11), new(0, 10, 12));
        Assert.Equal(11.1, plane.HeightAt(3, 4), 8);
        var result = new BoltHeightResult(DateTimeOffset.Now, new(3, 4, 12.6), plane.HeightAt(3, 4), 1.4, 1.6);
        Assert.Equal(1.5, result.Height, 8);
        Assert.True(result.Success);
        Assert.False((result with { Maximum = 1.4 }).Success);
        Assert.Throws<InvalidOperationException>(() =>
            (plane with { Point3 = new(20, 0, 12) }).HeightAt(3, 4));
        Assert.Throws<InvalidOperationException>(() =>
            (plane with { Point1 = new(0, 0, double.NaN) }).HeightAt(3, 4));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EachPcbMeasuresItsOwnPlaneThenBoltsWithProbeRetractedBetweenMoves(bool failSecondMark, bool excludeFromNg)
    {
        var io = new VirtualIoService(Outputs(new NgCarrierTransferHardwareSettings(),
            new InspectionGantryHardwareSettings()), new());
        io.Initialize();
        var settings = new InspectionGantrySettings
        {
            ProbeOffsetX = 2.5, ProbeOffsetY = -1.5, ExcludeDataMatrixFromNg = true,
            CameraResolution = 0.1,
            ExcludeFiducialFromNg = excludeFromNg,
        };
        using var motion = new VirtualMotionService(settings.Motion, new(), hasZ: false);
        motion.Initialize();
        var recipes = new RecipeManager(OpenMachineStore(), new());
        Assert.Equal(10, recipes.Current.BoltInspection.HeightMeasurementCount);
        recipes.Current.BoltInspection.HeightMeasurementCount = 2;
        var bolt1 = new BoltPoint { X = 3, Y = 4, MinimumHeight = 1.4, MaximumHeight = 1.6 };
        var bolt2 = new BoltPoint { X = 23, Y = 4, HeatSink = HeatSinkSlot.HeatSink2, MinimumHeight = 1.4, MaximumHeight = 1.6 };
        recipes.Current.Pcb.BoltPoints = [bolt1, bolt2];
        recipes.Current.CarrierImages = [
            new() { IsBarcode = true, HeatSink = HeatSinkSlot.HeatSink1, Center = new(), Region = new(0, 0, 20, 20) },
            new() { IsBarcode = true, HeatSink = HeatSinkSlot.HeatSink2, Center = new() { X = 20 }, Region = new(0, 0, 20, 20) },
        ];
        recipes.Current.BoltInspection.HeightReference1 = new()
        {
            Point1 = new() { X = 0, Y = 0 }, Point2 = new() { X = 10, Y = 0 }, Point3 = new() { X = 0, Y = 10 },
        };
        recipes.Current.BoltInspection.HeightReference2 = new()
        {
            Point1 = new() { X = 20, Y = 0 }, Point2 = new() { X = 30, Y = 0 }, Point3 = new() { X = 20, Y = 10 },
        };
        var connection = new MlcpTests.TestConnection();
        using var controller = new MlcpController(connection);
        var values = new Queue<string>(["9", "11", "10", "12", "11", "13", "12.4", "12.8",
            "19", "21", "20", "22", "21", "23", "23", "23.2"]);
        var measuredAt = new List<(double X, double Y)>();
        controller.FrameTransferred += (direction, bytes) =>
        {
            if (direction != MlcpFrameDirection.Transmit)
                return;
            Assert.True(io.GetInput(InputIo.InspectionProbeDown));
            Assert.False(io.GetInput(InputIo.InspectionProbeUp));
            Assert.False(io.GetInput(InputIo.InspectionBackupPlateUp));
            Assert.True(io.GetInput(InputIo.InspectionBackupPlateDown));
            Assert.True(MotionServiceBase.IsReadyAndStopped(motion));
            measuredAt.Add((motion.Position.X, motion.Position.Y));
            // Controller NG does not replace recipe height judgment.
            connection.Reply(Encoding.ASCII.GetBytes("\x05NG,01,+" + values.Dequeue() + "\x03@@\r\n"));
        };
        motion.PositionChanged += (x, y, z) => Assert.True(io.GetInput(InputIo.InspectionProbeUp));
        foreach (var pcb in Enum.GetValues<HeatSinkSlot>())
        {
            var fiducials = recipes.Current.BoltInspection.GetFiducials(pcb);
            var x = pcb == HeatSinkSlot.HeatSink1 ? 0 : 20;
            // Legacy recipe: 5 px right and 5 px up from each saved camera position.
            fiducials.First.LegacyCameraPosition = new() { X = x };
            fiducials.Second.LegacyCameraPosition = new() { X = x + (pcb == HeatSinkSlot.HeatSink1 ? 10 : 2) };
            foreach (var mark in new[] { fiducials.First, fiducials.Second })
            {
                mark.WhiteMark = true;
                mark.ReferenceDiameterMm = 2.8;
                // The registered hole is deliberately off-centre in the camera image.
                mark.Contour = FiducialDetector.Detect(FiducialTests.CircleImage((105, 95, 14)), mark, settings.CameraResolution, registering: true).Contour;
                mark.ImageWidth = mark.ImageHeight = 200;
            }
        }
        var camera = new VirtualCamera(() => motion.Position, () => [])
        {
            SourceImage = FiducialTests.CircleImage((115, 100, 14)),
        };
        var units = new UnitSettings { MainConveyor = false, NgConveyor = false };
        var work = BackupPlateStation.CreateInspection(io);
        var station = new InspectionStation(work, motion, new(motion), new NgCarrierConveyor(io, new(), units),
            settings, new() { WaitingPosition = new(), CarrierPickupPosition = new() }, io, units,
            camera, new VirtualLightController(), new() { StabilizationDelayMilliseconds = 0 }, recipes,
            controller, new() { PortName = "Virtual" });
        motion.PositionChanged += (x, y, z) =>
        {
            if (station.Step is not InspectionStationState.FindingFiducials)
                return;
            camera.SourceImage = failSecondMark && x == 10.5 ? FiducialTests.CircleImage()
                : x == 22.5 ? FiducialTests.CircleImage((80, 85, 14))
                : x == 20.5 ? FiducialTests.CircleImage((100, 105, 14))
                : FiducialTests.CircleImage((110, 105, 14));
        };
        Assert.True(await station.HomeHorizontalAsync());
        io.SetInputs((InputIo.InspectionHeatSink1Present, true), (InputIo.InspectionHeatSink2Present, true),
            (InputIo.InspectionBackupPlateUp, false), (InputIo.InspectionBackupPlateDown, true),
            (InputIo.InspectionStopperDown, false), (InputIo.InspectionStopperUp, true));
        var photos = new List<Guid?>();
        foreach (var pcb in Enum.GetValues<HeatSinkSlot>())
        {
            var old = work.GetAssembly(pcb);
            old.RecordAlignment(new(DateTimeOffset.Now, new(new(0, 0), new(99, 99), 0), new(new(10, 0), new(109, 99), 0)));
            old.RecordHeightReference(new(DateTimeOffset.Now, new(0, 0, 99), new(10, 0, 99), new(0, 10, 99)));
            old.RecordHeight(pcb == HeatSinkSlot.HeatSink1 ? bolt1.Id : bolt2.Id,
                new(DateTimeOffset.Now, new(3, 4, 99), 99, 1, 2));
            work.GetAssembly(pcb).InspectionCaptured += capture =>
            {
                var assembly = work.GetAssembly(pcb);
                if (pcb == HeatSinkSlot.HeatSink1 && failSecondMark)
                {
                    Assert.Null(assembly.Alignment);
                    Assert.NotNull(assembly.FiducialError);
                }
                else
                    Assert.NotNull(assembly.Alignment);
                var expected = pcb == HeatSinkSlot.HeatSink1
                    ? failSecondMark
                        ? capture.BoltId is null ? (0d, 0d) : (3d, 4d)
                        : capture.BoltId is null ? (1d, -0.5) : (4d, 3.5)
                    : capture.BoltId is null ? (21d, -0.5) : (17d, 2.5);
                Assert.Equal(expected.Item1, motion.Position.X, 6);
                Assert.Equal(expected.Item2, motion.Position.Y, 6);
                photos.Add(capture.BoltId);
            };
        }
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        work.Changed += () => { if (work.Completed) stop.Cancel(); };
        await station.RunAsync(stop.Token);

        Assert.True(work.Completed, $"{station.Step}, measured={measuredAt.Count}, photos={photos.Count}");
        Assert.Empty(values);
        Assert.Equal(new (double, double)[] {
            failSecondMark ? (2.5, -1.5) : (3.5, -2),
            failSecondMark ? (12.5, -1.5) : (13.5, -2),
            failSecondMark ? (2.5, 8.5) : (3.5, 8),
            failSecondMark ? (5.5, 2.5) : (6.5, 2),
            (23.5, -2), (23.5, 8), (13.5, -2), (19.5, 1),
        }.SelectMany(position => new[] { position, position }), measuredAt);
        Assert.Contains(bolt1.Id, photos);
        Assert.Contains(bolt2.Id, photos);
        var first = work.GetAssembly(HeatSinkSlot.HeatSink1);
        var second = work.GetAssembly(HeatSinkSlot.HeatSink2);
        Assert.Equal(10, first.HeightReference!.Point1.Value);
        Assert.Equal(20, second.HeightReference!.Point1.Value);
        Assert.NotSame(first.Alignment, second.Alignment);
        if (failSecondMark)
        {
            Assert.Null(first.Alignment);
            Assert.Contains(UiText.Get("Fiducial 2"), first.FiducialError);
            Assert.Equal(excludeFromNg, first.IsFiducialNgExcluded);
        }
        else
        {
            Assert.Equal(new(0.5, 0.5), first.Alignment!.First.Reference);
            Assert.Equal(1.5, first.Alignment.First.Measured.X);
            Assert.Equal(0, first.Alignment.First.Measured.Y, 6);
            Assert.Equal(0, first.Alignment.AngleDegrees, 6);
            Assert.Null(first.FiducialError);
        }
        Assert.Equal(new(20.5, 0.5), second.Alignment!.First.Reference);
        Assert.InRange(second.Alignment.First.MatchScore!.Value, 99.9, 100);
        Assert.InRange(second.Alignment.Second.MatchScore!.Value, 99.9, 100);
        Assert.Equal(20.5, second.Alignment.First.Measured.X);
        Assert.Equal(0, second.Alignment.First.Measured.Y, 6);
        Assert.Equal(90, second.Alignment.AngleDegrees, 6);
        Assert.Null(second.FiducialError);
        Assert.Equal((3d, 4d), (bolt1.X, bolt1.Y));
        Assert.True(Assert.Single(first.HeightResults).Value.Success);
        Assert.Equal(1.5, first.HeightResults[bolt1.Id].Height, 8);
        var readings = first.HeightResults[bolt1.Id].Sample.Readings;
        Assert.Equal(new[] { 12.4, 12.8 }, readings.Select(point => point.Displacement));
        Assert.True(readings[1].ElapsedSeconds >= readings[0].ElapsedSeconds);
        Assert.False(Assert.Single(second.HeightResults).Value.Success);
        Assert.Equal(2, second.HeightResults[bolt2.Id].Height, 8);
        Assert.Equal(failSecondMark && !excludeFromNg ? AssemblyResult.Ng : AssemblyResult.Ok, first.InspectionResult);
        Assert.Equal(AssemblyResult.Ng, second.InspectionResult);
        Assert.True(io.GetInput(InputIo.InspectionProbeUp));
        first.ClearInspectionResults();
        Assert.Empty(first.HeightResults);
        Assert.Null(first.HeightReference);
        Assert.Null(first.Alignment);
        Assert.Null(first.FiducialError);
        Assert.False(first.IsFiducialNgExcluded);
    }

    [Fact]
    public async Task MeasurementTimeoutLeavesNoResultAndRequiresProbeUpBeforeMoving()
    {
        var io = new VirtualIoService(Outputs(new NgCarrierTransferHardwareSettings(),
            new InspectionGantryHardwareSettings()), new());
        io.Initialize();
        using var motion = new VirtualMotionService(new(), new(), hasZ: false);
        motion.Initialize();
        using var controller = new MlcpController(new MlcpTests.TestConnection())
        {
            ResponseTimeout = TimeSpan.FromMilliseconds(30),
        };
        var units = new UnitSettings();
        var station = new InspectionStation(BackupPlateStation.CreateInspection(io), motion, new(motion),
            new NgCarrierConveyor(io, new(), units), new() { ProbeOffsetX = 2 }, new(), io, units,
            new VirtualCamera(() => motion.Position, () => []), new VirtualLightController(), new(),
            new(OpenMachineStore(), new()), controller, new() { PortName = "Virtual" });
        Assert.True(await station.HomeHorizontalAsync());
        await Assert.ThrowsAsync<TimeoutException>(() => station.MeasureHeightAtAsync(new() { X = 3, Y = 4 }, CancellationToken.None));
        Assert.False(controller.IsOpen);
        Assert.True(io.GetInput(InputIo.InspectionProbeDown));
        Assert.Empty(station.Station.GetAssembly(HeatSinkSlot.HeatSink1).HeightResults);
        await Assert.ThrowsAsync<MotionInterlockException>(() => station.MoveToAsync(new() { X = 10 }));
        Assert.Equal((5d, 4d), (motion.Position.X, motion.Position.Y));
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.InspectionProbeDown, false, CancellationToken.None);
        await station.MoveToAsync(new() { X = 10 });
    }
}
