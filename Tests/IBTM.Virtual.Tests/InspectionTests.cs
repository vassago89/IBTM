using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.Storage;
using IBTM.Virtual;
using Microsoft.Data.Sqlite;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTestSupport;

namespace IBTM.Virtual.Tests;

public sealed class InspectionTests
{
    [Theory]
    [InlineData(false, true, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, true, true)]
    public async Task DataMatrixNgExclusionKeepsReadResultsAndOtherNgRouting(
        bool excludeDataMatrix, bool boltPresent, bool fasteningNg, bool routeToNg)
    {
        var io = new VirtualIoService(new NgCarrierTransferHardwareSettings().Outputs, new());
        io.Initialize();
        var settings = new InspectionGantrySettings { ExcludeDataMatrixFromNg = excludeDataMatrix };
        using var motion = new VirtualMotionService(settings.Motion, new(), hasZ: false);
        motion.Initialize();
        var bolt = new BoltPoint { X = 0, Y = 0, BrightnessThreshold = 128, MinimumBrightRatio = 0.5 };
        var recipes = new RecipeManager(OpenMachineStore(), new());
        recipes.Current.Pcb.BoltPoints = [bolt];
        recipes.Current.CarrierImages = [
            new() { IsBarcode = true, Center = new(), Region = new(0, 0, 20, 20) },
            new() { BoltId = bolt.Id, Center = new(), Region = new(0, 0, 20, 20) },
        ];
        var camera = new VirtualCamera(() => motion.Position, () => [])
        {
            SourceImage = new(20, 20, 60, Enumerable.Repeat(boltPresent ? (byte)255 : (byte)0, 1200).ToArray()),
        };
        var units = new UnitSettings { MainConveyor = false, NgConveyor = false };
        var work = ConveyorStation.CreateInspection(io);
        var station = new InspectionStation(work, motion, new(motion), new NgCarrierConveyor(io, new(), units),
            settings, new() { WaitingPosition = new(), CarrierPickupPosition = new() }, io, units,
            camera, new VirtualLightController(), new() { StabilizationDelayMilliseconds = 0 }, recipes);
        Assert.True(await station.HomeHorizontalAsync());
        io.SetInputs(
            (InputIo.InspectionHeatSink1Present, true),
            (InputIo.InspectionBackupPlateUp, false), (InputIo.InspectionBackupPlateDown, true),
            (InputIo.InspectionStopperDown, false), (InputIo.InspectionStopperUp, true));
        var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        assembly.RecordBolt(FasteningHead.Pickup, bolt.Id, new(!fasteningNg, 8));
        assembly.CompleteFastening();
        var captures = new List<InspectionCapture>();
        assembly.InspectionCaptured += captures.Add;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        work.Changed += () =>
        {
            if (work.Completed)
                stop.Cancel();
        };

        await station.RunAsync(stop.Token);

        Assert.True(work.Completed);
        Assert.Null(assembly.PcbBarcode);
        Assert.Equal(AssemblyResult.Ng, assembly.PcbBarcodeResult);
        Assert.Equal(excludeDataMatrix, assembly.IsDataMatrixNgExcluded);
        Assert.False(Assert.Single(captures, capture => capture.BoltId is null).Success);
        Assert.Equal(boltPresent, assembly.BoltPresenceResults[bolt.Id]);
        Assert.Equal(routeToNg ? AssemblyResult.Ng : AssemblyResult.Ok, assembly.Result);
        Assert.Equal(routeToNg, station.RouteToNg);
        Assert.Equal(routeToNg ? new ProductionCounts(0, 1) : new ProductionCounts(1, 0), recipes.Counts);

        assembly.ClearInspectionResults();
        Assert.False(assembly.IsDataMatrixNgExcluded);
        Assert.Equal(AssemblyResult.Pending, assembly.PcbBarcodeResult);
    }

    [Theory]
    [InlineData(null, null, false, false)]
    [InlineData(10.0, null, false, true)]
    [InlineData(null, 10.0, false, true)]
    [InlineData(null, null, true, true)]
    [InlineData(10.0, 15.0, true, true)]
    public void CompletedInspectionRoutesDryRunAndMissingRequiredMeasurementsToNg(
        double? minimumTurns, double? maximumTurns, bool dryRun, bool routeToNg)
    {
        var io = new VirtualIoService(new NgCarrierTransferHardwareSettings().Outputs, new());
        io.Initialize();
        using var motion = new VirtualMotionService(new(), new(), hasZ: false);
        var station = CreateNgTransfer(io, motion);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        var assembly = station.Station.GetAssembly(HeatSinkSlot.HeatSink1);
        assembly.RecordBolt(FasteningHead.Pickup, Guid.NewGuid(), new(true, null,
            dryRun ? BoltResultSource.DryRun : BoltResultSource.Controller)
        {
            MinimumTurns = minimumTurns,
            MaximumTurns = maximumTurns,
        });
        assembly.CompleteFastening();
        assembly.CompleteInspection();
        station.Station.Complete(station.Station.CurrentJob);

        Assert.Equal(minimumTurns.HasValue || maximumTurns.HasValue ? AssemblyResult.Pending : null, assembly.TurnsResult);
        Assert.Equal(dryRun ? AssemblyResult.Ng : AssemblyResult.Ok, assembly.FasteningResult);
        if (dryRun)
            Assert.Equal(AssemblyResult.Ng, assembly.Result);
        Assert.Equal(routeToNg, station.RouteToNg);
    }

    [Fact]
    public async Task StartupNotificationFailureClearsInspectionRun()
    {
        var io = new VirtualIoService(new NgCarrierTransferHardwareSettings().Outputs, new());
        using var motion = new VirtualMotionService(new(), new(), hasZ: false);
        var station = CreateNgTransfer(io, motion);
        var failure = new InvalidOperationException("Run-start notification failed.");
        station.Trace += message =>
        {
            if (message.EndsWith("run started."))
                throw failure;
        };

        var error = await Record.ExceptionAsync(() => station.RunAsync());

        Assert.Same(failure, error);
        Assert.False(station.IsRunning);
        Assert.Null(station.Step);
    }

    [Fact]
    public async Task ConveyorRunFeedbackCancelsInFlightInspectionBeforeRecording()
    {
        var io = new VirtualIoService(Outputs(new NgCarrierTransferHardwareSettings(), new ConveyorHardwareSettings()), new());
        io.Initialize();
        var recipes = new RecipeManager(OpenMachineStore(), new());
        recipes.Current.Pcb.BoltPoints = [new() { Id = VirtualTestSupport.BoltId(1), X = 0, Y = 0 }];
        recipes.Current.CarrierImages = [new() { Number = 1, IsBarcode = true, Center = new(), Region = new(0, 0, 20, 20) }];
        var settings = new InspectionGantrySettings();
        var transfer = new NgCarrierTransferSettings { WaitingPosition = new(), CarrierPickupPosition = new() };
        var operations = new OperationCancellation();
        using var motion = new VirtualMotionService(settings.Motion, operations, hasZ: false);
        motion.Initialize();
        var units = new UnitSettings { MainConveyor = false, NgConveyor = false };
        var carrier = ConveyorStation.CreateInspection(io);
        var station = new InspectionStation(carrier, motion, new(motion), new NgCarrierConveyor(io, new(), units),
            settings, transfer, io, units,
            new VirtualCamera(() => motion.Position, () => []), new VirtualLightController(), new(), recipes);
        Assert.True(await station.HomeHorizontalAsync());
        io.SetInputs(
            (InputIo.InspectionHeatSink1Present, true),
            (InputIo.InspectionBackupPlateUp, false), (InputIo.InspectionBackupPlateDown, true),
            (InputIo.InspectionStopperDown, false), (InputIo.InspectionStopperUp, true),
            (InputIo.NgCarrierPickupUp, true), (InputIo.NgCarrierPickupDown, false),
            (InputIo.NgCarrierGripperOpen, true), (InputIo.NgCarrierGripperClosed, false));
        var captured = false;
        var recorded = false;
        carrier.GetAssembly(HeatSinkSlot.HeatSink1).InspectionCaptured += capture => recorded = true;
        station.InspectionCaptured += (image, pcb, bolt) =>
        {
            captured = true;
            io.SetOutput(OutputIo.MainConveyorRun, true);
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<MotionInterlockException>(() => station.RunAsync(stop.Token));

        Assert.True(captured);
        Assert.False(recorded);
        Assert.False(carrier.Completed);
    }

    [Fact]
    public async Task InspectionProcessesOnePcbAtATimeAndRestartsAtItsBarcode()
    {
        var io = new VirtualIoService(new NgCarrierTransferHardwareSettings().Outputs, new());
        io.Initialize();
        var store = OpenMachineStore();
        var recipes = new RecipeManager(store, new());
        recipes.Current.Pcb.BoltPoints = [
            new() { Id = VirtualTestSupport.BoltId(1), HeatSink = HeatSinkSlot.HeatSink1, X = 0, Y = 0 },
            new() { Id = VirtualTestSupport.BoltId(2), HeatSink = HeatSinkSlot.HeatSink2, X = 0, Y = 0 },
        ];
        recipes.Current.CarrierImages = [
            new() { Number = 1, IsBarcode = true, HeatSink = HeatSinkSlot.HeatSink1, Center = new(), Region = new(0, 0, 20, 20) },
            new() { Number = 2, BoltId = VirtualTestSupport.BoltId(1), HeatSink = HeatSinkSlot.HeatSink1, Center = new(), Region = new(0, 0, 20, 20) },
            new() { Number = 3, IsBarcode = true, HeatSink = HeatSinkSlot.HeatSink2, Center = new(), Region = new(0, 0, 20, 20) },
            new() { Number = 4, BoltId = VirtualTestSupport.BoltId(2), HeatSink = HeatSinkSlot.HeatSink2, Center = new(), Region = new(0, 0, 20, 20) },
        ];
        var settings = new InspectionGantrySettings();
        var transfer = new NgCarrierTransferSettings
        {
            WaitingPosition = new(), CarrierPickupPosition = new(), ShuttlePlacePosition = new() { X = 100, Y = 100 },
        };
        var operations = new OperationCancellation();
        using var motion = new VirtualMotionService(settings.Motion, operations, hasZ: false);
        motion.Initialize();
        var units = new UnitSettings { MainConveyor = false, NgConveyor = false };
        var work = ConveyorStation.CreateInspection(io);
        var station = new InspectionStation(
            work,
            motion,
            new MotionStatus(motion),
            new NgCarrierConveyor(io, new(), units),
            settings,
            transfer,
            io,
            units,
            new VirtualCamera(() => motion.Position, () => []),
            new VirtualLightController(),
            new(),
            recipes);
        Assert.True(await station.HomeHorizontalAsync());
        io.SetInputs(
            (InputIo.InspectionHeatSink1Present, true), (InputIo.InspectionHeatSink2Present, true),
            (InputIo.InspectionBackupPlateUp, false), (InputIo.InspectionBackupPlateDown, true),
            (InputIo.InspectionStopperDown, false), (InputIo.InspectionStopperUp, true),
            (InputIo.NgCarrierPickupUp, true), (InputIo.NgCarrierPickupDown, false),
            (InputIo.NgCarrierGripperOpen, true), (InputIo.NgCarrierGripperClosed, false));
        var visited = new System.Collections.Generic.List<(HeatSinkSlot?, Guid?)>();
        using var firstStop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var secondStop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var interrupt = true;
        station.Trace += message =>
        {
            if (!message.StartsWith($"InspectionStation: {InspectionStationState.InspectingPoint} ", StringComparison.Ordinal))
                return;
            var bolt = station.ActiveBolt?.Id;
            visited.Add((station.ActivePcb, bolt));
            if (interrupt && bolt == VirtualTestSupport.BoltId(2))
                firstStop.Cancel();
        };
        work.Changed += () =>
        {
            if (work.Completed)
                secondStop.Cancel();
        };
        await station.RunAsync(firstStop.Token);
        Assert.Equal(new (HeatSinkSlot?, Guid?)[] {
            (HeatSinkSlot.HeatSink1, null), (HeatSinkSlot.HeatSink1, VirtualTestSupport.BoltId(1)),
            (HeatSinkSlot.HeatSink2, null), (HeatSinkSlot.HeatSink2, VirtualTestSupport.BoltId(2)),
        }, visited);
        Assert.Single(work.GetAssembly(HeatSinkSlot.HeatSink1).BoltPresenceResults);
        Assert.Empty(work.GetAssembly(HeatSinkSlot.HeatSink2).BoltPresenceResults);
        Assert.False(work.Completed);
        Assert.Null(station.ActivePcb);
        Assert.Null(station.ActiveBolt);
        Assert.Null(work.LastCycleSeconds);
        Assert.Equal(new ProductionCounts(0, 0), recipes.Counts);
        visited.Clear();
        interrupt = false;
        using (var connection = new SqliteConnection($"Data Source={store.DatabaseFile}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER FailCountSave BEFORE INSERT ON ProductionCounts BEGIN SELECT RAISE(ABORT, 'count save failed'); END";
            command.ExecuteNonQuery();
            try
            {
                var error = await Assert.ThrowsAsync<SqliteException>(() => station.RunAsync(secondStop.Token));
                Assert.Contains("count save failed", error.Message);
                Assert.False(work.Completed);
                Assert.False(station.IsTransferAllowed);
                Assert.Null(work.LastCycleSeconds);
                Assert.Equal(new ProductionCounts(0, 0), recipes.Counts);
                Assert.Equal(recipes.Counts, store.LoadProductionCounts(recipes.Current.Name));
            }
            finally
            {
                command.CommandText = "DROP TRIGGER FailCountSave";
                command.ExecuteNonQuery();
            }
        }
        visited.Clear();
        await station.RunAsync(secondStop.Token);
        Assert.Equal(new (HeatSinkSlot?, Guid?)[] {
            (HeatSinkSlot.HeatSink1, null), (HeatSinkSlot.HeatSink1, VirtualTestSupport.BoltId(1)),
            (HeatSinkSlot.HeatSink2, null), (HeatSinkSlot.HeatSink2, VirtualTestSupport.BoltId(2)),
        }, visited);
        Assert.True(work.Completed);
        Assert.Single(work.GetAssembly(HeatSinkSlot.HeatSink2).BoltPresenceResults);
        Assert.True(work.LastCycleSeconds > 0);
        Assert.Equal(new ProductionCounts(0, 2), recipes.Counts);
        var cycleTime = work.LastCycleSeconds;
        Assert.Null(station.Step);
        Assert.Null(station.ActivePcb);
        Assert.Null(station.ActiveBolt);
        visited.Clear();
        using var completedStop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var restartedInspection = false;
        station.StepChanged += () =>
        {
            if (station.Step is null)
                return;
            restartedInspection |= station.Step is InspectionStationState.PreparingInspection;
            completedStop.Cancel();
        };
        await station.RunAsync(completedStop.Token);
        Assert.True(work.Completed);
        Assert.False(restartedInspection);
        Assert.Empty(visited);
        Assert.Equal(cycleTime, work.LastCycleSeconds);
        Assert.Equal(new ProductionCounts(0, 2), recipes.Counts);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public async Task PcbPresenceChangeStopsInspectionWithoutRestarting(bool secondPcbArrives, bool betweenPoints, bool removeCarrier)
    {
        var io = new VirtualIoService(new NgCarrierTransferHardwareSettings().Outputs, new());
        io.Initialize();
        var recipes = new RecipeManager(OpenMachineStore(), new());
        recipes.Current.Pcb.BoltPoints = [
            new() { HeatSink = HeatSinkSlot.HeatSink1, X = 0, Y = 0 },
            new() { HeatSink = HeatSinkSlot.HeatSink2, X = 0, Y = 0 },
        ];
        foreach (var bolt in recipes.Current.Pcb.BoltPoints)
        {
            recipes.Current.CarrierImages.Add(new()
            {
                IsBarcode = true, HeatSink = bolt.HeatSink, Center = new(), Region = new(0, 0, 20, 20),
            });
            recipes.Current.CarrierImages.Add(new()
            {
                BoltId = bolt.Id, HeatSink = bolt.HeatSink, Region = new(0, 0, 20, 20),
            });
        }
        var operations = new OperationCancellation();
        var settings = new InspectionGantrySettings();
        using var motion = new VirtualMotionService(settings.Motion, operations, hasZ: false);
        motion.Initialize();
        var units = new UnitSettings { MainConveyor = false, NgConveyor = false };
        var carrier = ConveyorStation.CreateInspection(io);
        var station = new InspectionStation(carrier, motion, new(motion), new NgCarrierConveyor(io, new(), units),
            settings, new() { WaitingPosition = new(), CarrierPickupPosition = new() }, io, units,
            new VirtualCamera(() => motion.Position, () => []), new VirtualLightController(), new(), recipes);
        Assert.True(await station.HomeHorizontalAsync());
        io.SetInputs(
            (InputIo.InspectionHeatSink1Present, !removeCarrier), (InputIo.InspectionHeatSink2Present, !secondPcbArrives),
            (InputIo.InspectionBackupPlateUp, false), (InputIo.InspectionBackupPlateDown, true),
            (InputIo.InspectionStopperDown, false), (InputIo.InspectionStopperUp, true),
            (InputIo.NgCarrierPickupUp, true), (InputIo.NgCarrierPickupDown, false),
            (InputIo.NgCarrierGripperOpen, true), (InputIo.NgCarrierGripperClosed, false));
        var captures = 0;
        var recorded = 0;
        carrier.AssemblyCreated += assembly => assembly.InspectionCaptured += _ =>
        {
            if (++recorded == 1 && betweenPoints)
                io.SetInput(InputIo.InspectionHeatSink2Present, secondPcbArrives);
        };
        station.InspectionCaptured += (image, pcb, bolt) =>
        {
            if (++captures == 1 && !betweenPoints)
                io.SetInput(InputIo.InspectionHeatSink2Present, secondPcbArrives);
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<MotionInterlockException>(() => station.RunAsync(stop.Token));

        Assert.False(carrier.Completed);
        Assert.Equal(betweenPoints ? 1 : 0, recorded);
        Assert.Equal(1, captures);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EditingInspectionRegionWakesTeachingWaitWithoutSaving(bool barcode)
    {
        var io = new VirtualIoService(new NgCarrierTransferHardwareSettings().Outputs, new());
        io.Initialize();
        var store = OpenMachineStore();
        var recipes = new RecipeManager(store, new());
        recipes.Current.Name = "Teaching wait";
        recipes.Current.Pcb.BoltPoints = [new() { Id = VirtualTestSupport.BoltId(1), X = 0, Y = 0 }];
        recipes.Current.CarrierImages = [
            new() { Number = 1, IsBarcode = true, Center = new(), Region = barcode ? null : new(0, 0, 20, 20) },
            new() { Number = 2, BoltId = VirtualTestSupport.BoltId(1), Center = new(), Region = barcode ? new(0, 0, 20, 20) : null },
        ];
        store.SaveRecipe(recipes.Current);
        var settings = new InspectionGantrySettings();
        var transfer = new NgCarrierTransferSettings
        {
            WaitingPosition = new(), CarrierPickupPosition = new(), ShuttlePlacePosition = new() { X = 100, Y = 100 },
        };
        var operations = new OperationCancellation();
        using var motion = new VirtualMotionService(settings.Motion, operations, hasZ: false);
        motion.Initialize();
        var units = new UnitSettings { MainConveyor = false, NgConveyor = false };
        var work = ConveyorStation.CreateInspection(io);
        var conveyor = new NgCarrierConveyor(io, new(), units);
        var station = new InspectionStation(
            work,
            motion,
            new MotionStatus(motion),
            conveyor,
            settings,
            transfer,
            io,
            units,
            new VirtualCamera(() => motion.Position, () => []),
            new VirtualLightController(),
            new(),
            recipes);
        Assert.True(await station.HomeHorizontalAsync());
        io.SetInputs(
            (InputIo.InspectionHeatSink1Present, true), (InputIo.InspectionHeatSink2Present, false),
            (InputIo.InspectionBackupPlateUp, false), (InputIo.InspectionBackupPlateDown, true),
            (InputIo.InspectionStopperDown, false), (InputIo.InspectionStopperUp, true),
            (InputIo.NgCarrierPickupUp, true), (InputIo.NgCarrierPickupDown, false),
            (InputIo.NgCarrierGripperOpen, true), (InputIo.NgCarrierGripperClosed, false));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        station.Trace += message =>
        {
            if (message.Contains($": {InspectionStationState.TeachingRequired} "))
                waiting.TrySetResult();
            if (message.StartsWith($"InspectionStation: {InspectionStationState.InspectingPoint} ", StringComparison.Ordinal)
                && (station.ActiveBolt is null) == barcode)
            {
                resumed.TrySetResult();
                stop.Cancel();
            }
        };
        var run = station.RunAsync(stop.Token);
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
            recipes.Current.CarrierImages[barcode ? 0 : 1].Region = new(0, 0, 20, 20);
            recipes.NotifyInspectionChanged();
            await resumed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            stop.Cancel();
            await run;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DataMatrixReadsOnlyTheDrawnOffCenterRegion(bool inverted)
    {
        var camera = new VirtualCamera(
            () => (10, 17, 0),
            () => [],
            () => [new(new() { X = 13, Y = 15 }, 4, 4, "PCB-000123")]);
        var image = await camera.CaptureAsync();
        var stride = image.Stride + 5;
        var pixels = new byte[stride * image.Height];
        for (var row = 0; row < image.Height; row++)
            for (var column = 0; column < image.Stride; column++)
                pixels[row * stride + column] = inverted
                    ? (byte)(255 - image.Pixels[row * image.Stride + column])
                    : image.Pixels[row * image.Stride + column];
        var padded = new ImageFrame(image.Width, image.Height, stride, pixels);
        var binary = BinaryRegionAnalyzer.Check(padded, new(180, 40, 80, 80), 128).Image;
        Assert.Equal((80, 80), (binary.Width, binary.Height));
        Assert.All(binary.Pixels, pixel => Assert.True(pixel is 0 or 255));
        Assert.Equal("PCB-000123", DataMatrixReader.Read(binary, new(0, 0, 80, 80), new())?.Text);
        Assert.Equal("PCB-000123", DataMatrixReader.Read(padded, new(180, 40, 80, 80), new())?.Text);
        Assert.Equal("PCB-000123", DataMatrixReader.Read(padded, new(180, 40, 80, 80),
            new() { ThresholdMinimum = 128, ThresholdMaximum = 128, AutoRotate = true }).Text);
        Assert.Null(DataMatrixReader.Read(padded, new(180, 40, 80, 80),
            new() { ThresholdMinimum = 0, ThresholdMaximum = 0, DilationRadius = 0 }).Text);
        Assert.Null(DataMatrixReader.Read(padded, new(120, 80, 80, 80), new()).Text);
        Assert.Throws<ArgumentOutOfRangeException>(() => DataMatrixReader.Read(padded, new(300, 0, 80, 80), new()));
    }

    [Fact]
    public void BinaryCheckerUsesInclusiveBrightnessThresholdAndBgrLuminance()
    {
        var image = new ImageFrame(5, 1, 15,
            [127, 127, 127, 128, 128, 128, 255, 0, 0, 0, 255, 0, 0, 0, 255]);
        var result = BinaryRegionAnalyzer.Check(image, new(0, 0, 5, 1), 128);

        Assert.Equal(0.4, result.BrightRatio);
        Assert.Equal(new byte[] { 0, 0, 0, 255, 255, 255, 0, 0, 0, 255, 255, 255, 0, 0, 0 }, result.Image.Pixels);
        Assert.Equal(127, image.Pixels[0]);
    }

    [Fact]
    public void BinaryCheckerUsesOnlyDrawnRectangleAndIgnoresRowPadding()
    {
        // White surroundings and padding must not count toward this 2 x 3 ROI.
        var pixels = Enumerable.Repeat((byte)255, 14 * 5).ToArray();
        var region = new PixelRegion(1, 1, 2, 3);
        for (var y = region.Y; y < region.Y + region.Height; y++)
            for (var x = region.X; x < region.X + region.Width; x++)
                for (var channel = 0; channel < 3; channel++)
                    pixels[y * 14 + x * 3 + channel] = y == 2 ? (byte)200 : (byte)20;
        var image = new ImageFrame(4, 5, 14, pixels);

        var result = BinaryRegionAnalyzer.Check(image, region, 128);

        Assert.Equal((2, 3, 6), (result.Image.Width, result.Image.Height, result.Image.Stride));
        Assert.Equal(1.0 / 3, result.BrightRatio);
        Assert.Equal(6, result.Image.Pixels.Count(value => value == 255));
        Assert.Throws<ArgumentOutOfRangeException>(() => BinaryRegionAnalyzer.Check(image, region with { X = 3 }, 128));
        Assert.Throws<ArgumentOutOfRangeException>(() => BinaryRegionAnalyzer.Check(image, region, 256));
        Assert.Throws<ArgumentException>(() => BinaryRegionAnalyzer.Check(image with { Stride = 11 }, region, 128));
        Assert.Throws<ArgumentException>(() => BinaryRegionAnalyzer.Check(image with { Pixels = new byte[14] }, region, 128));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FovCaptureUsesCurrentPositionWithoutMoving(bool live)
    {
        var settings = new InspectionGantrySettings
        {
            Motion = new() { HorizontalSpeed = 1_000 },
        };
        var operations = new OperationCancellation();
        using var motion = new VirtualMotionService(
            settings.Motion,
            operations,
            hasZ: false);
        var io = new VirtualIoService(new NgCarrierTransferHardwareSettings().Outputs, new());
        io.Initialize();
        motion.Initialize();
        var recipe = new BoltInspectionRecipe();
        var fov = new CarrierImageTile
        {
            Number = 1,
            Center = new() { X = 12, Y = 9 },
            Region = new(200, 30, 60, 80),
            BoltId = VirtualTestSupport.BoltId(1),
            HeatSink = HeatSinkSlot.HeatSink1,
        };
        var units = new UnitSettings();
        var recipes = new RecipeManager(OpenMachineStore(), new())
        {
            Current = { BoltInspection = recipe, CarrierImages = [fov] },
        };
        var work = ConveyorStation.CreateInspection(io);
        var inspector = new InspectionStation(
            work,
            motion,
            new MotionStatus(motion),
            new NgCarrierConveyor(io, new(), units),
            settings,
            new(),
            io,
            units,
            new VirtualCamera(
                () => motion.Position,
                () => [],
                () => [new(new() { X = 15, Y = 7 }, 4, 4, "PCB-000123")]),
            new VirtualLightController(),
            new LightingSettings(),
            recipes);
        Assert.True(await inspector.HomeHorizontalAsync());
        if (live)
            await inspector.StartLiveViewAsync();

        var movements = 0;
        motion.PositionChanged += (_, _, _) => movements++;
        foreach (var center in new[] { new AxisPosition { X = 12, Y = 9 }, new AxisPosition { X = 27, Y = 16 } })
        {
            await inspector.MoveToAsync(center, 1_000);
            movements = 0;
            var image = await inspector.CaptureCarrierImageAsync();
            Assert.Equal((center.X, center.Y), (image.Center.X, image.Center.Y));
            Assert.True(VirtualTestSupport.IsAt(inspector.Motion.Feedback, center));
            Assert.Equal(live, inspector.IsLiveView);
            Assert.Equal(0, movements);
            Assert.NotEmpty(image.Frame.Pixels);
        }

        var bolt = new BoltPoint { Id = VirtualTestSupport.BoltId(1), X = 12, Y = 9 };
        recipes.Current.Pcb.BoltPoints.Add(bolt);
        var taughtPosition = fov.Center!;
        fov.Center = null;
        var capturedFov = await inspector.InspectAsync(bolt);
        Assert.True(VirtualTestSupport.IsAt(inspector.Motion.Feedback, taughtPosition)); // ROI pixels do not alter the taught camera XY.
        Assert.Equal(fov.Region, capturedFov.Region);
        Assert.NotEmpty(capturedFov.Frame.Pixels);
        Assert.NotNull(bolt.InspectionPosition);
        Assert.True(inspector.HasRegion(bolt));
        fov.Region = new(310, 30, 60, 80);
        Assert.NotNull(bolt.InspectionPosition);
        Assert.False(inspector.HasRegion(bolt));
        movements = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => inspector.InspectAsync(bolt));
        Assert.Equal(0, movements);
        fov.Region = null;
        Assert.NotNull(bolt.InspectionPosition);
        Assert.False(inspector.HasRegion(bolt));
        movements = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => inspector.InspectAsync(bolt));
        Assert.Equal(0, movements);

        fov.Center = taughtPosition;
        fov.IsBarcode = true;
        fov.BoltId = null;
        fov.Region = new(180, 40, 80, 80);
        var barcodeResult = await inspector.ReadBarcodeAsync(HeatSinkSlot.HeatSink1, CancellationToken.None);
        Assert.True(VirtualTestSupport.IsAt(inspector.Motion.Feedback, fov.Center));
        Assert.True(barcodeResult.Success);
        Assert.Equal("PCB-000123", barcodeResult.Barcode);
        Assert.True(inspector.HasBarcodeRegion(HeatSinkSlot.HeatSink1));
        Assert.NotNull(recipes.Current.FindInspectionImage(HeatSinkSlot.HeatSink1, boltId: null)?.Center);
        Assert.False(inspector.HasBarcodeRegion(HeatSinkSlot.HeatSink2));
        Assert.Null(recipes.Current.FindInspectionImage(HeatSinkSlot.HeatSink2, boltId: null)?.Center);
        Assert.Equal(new PixelRegion(180, 40, 80, 80), inspector.GetBarcodeFov(HeatSinkSlot.HeatSink1).Region);
        fov.Region = new(310, 30, 60, 80);
        Assert.False(inspector.HasBarcodeRegion(HeatSinkSlot.HeatSink1));
        Assert.NotNull(recipes.Current.FindInspectionImage(HeatSinkSlot.HeatSink1, boltId: null)?.Center);
        movements = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => inspector.ReadBarcodeAsync(HeatSinkSlot.HeatSink1, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => inspector.ReadBarcodeAsync(HeatSinkSlot.HeatSink2, CancellationToken.None));
        Assert.Equal(0, movements);

        fov.Region = new(180, 40, 80, 80);
        recipes.Current.CarrierImages.Add(new() { IsBarcode = true, HeatSink = HeatSinkSlot.HeatSink1 });
        Assert.Null(recipes.Current.FindInspectionImage(HeatSinkSlot.HeatSink1, boltId: null)?.Center);
        Assert.False(inspector.HasBarcodeRegion(HeatSinkSlot.HeatSink1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => inspector.ReadBarcodeAsync(HeatSinkSlot.HeatSink1, CancellationToken.None));
        Assert.Equal(0, movements);
    }

    [Trait("Category", "MachineFlow")]
    [Fact]
    public async Task InspectionUsesCurrentCarrierSensorsAndPreservesOwnedResults()
    {
        var io = new VirtualIoService(
            new NgCarrierTransferHardwareSettings().Outputs,
            new MachineOptions());
        var gantrySettings = new InspectionGantrySettings
        {
            Motion = new MotionSettings { HorizontalSpeed = 200 },
        };
        var operations = new OperationCancellation();
        using var motion = new VirtualMotionService(
            gantrySettings.Motion,
            operations,
            hasZ: false);
        var transferSettings = new NgCarrierTransferSettings { CarrierPickupPosition = new(), WaitingPosition = new() };
        var units = new UnitSettings { MainConveyor = false };
        var recipes = new RecipeManager(OpenMachineStore(), new());
        var work = ConveyorStation.CreateInspection(io);
        BoltPoint[] bolts = [
            new() { Id = VirtualTestSupport.BoltId(1), HeatSink = HeatSinkSlot.HeatSink1, X = 9, Y = 9 },
            new() { Id = VirtualTestSupport.BoltId(2), HeatSink = HeatSinkSlot.HeatSink1, X = 9, Y = 21 },
            new() { Id = VirtualTestSupport.BoltId(3), HeatSink = HeatSinkSlot.HeatSink2, X = 31, Y = 9 },
            new() { Id = VirtualTestSupport.BoltId(4), HeatSink = HeatSinkSlot.HeatSink2, X = 31, Y = 21 },
        ];
        var camera = new MissingBoltCamera(
            new VirtualCamera(
                () => motion.Position,
                () => bolts.Select(bolt => bolt.InspectionPosition!),
                () => [
                    new(new() { X = 13, Y = 15 }, 4, 4, "PCB-1"),
                    new(new() { X = 31, Y = 15 }, 4, 4, "PCB-2")
        ]),
            () => motion.Position,
            bolts[1].InspectionPosition!);
        recipes.Current.Pcb.BoltPoints = [.. bolts];
        recipes.Current.CarrierImages = [.. bolts.Select((bolt, index) => new CarrierImageTile
            {
                Number = index + 1,
                Region = new(128, 88, 64, 64),
                BoltId = bolt.Id,
                HeatSink = bolt.HeatSink,
            }),
                new()
                {
                    Number = 5, Center = new() { X = 10, Y = 17 },
                    IsBarcode = true, HeatSink = HeatSinkSlot.HeatSink1, Region = new(180, 40, 80, 80),
                },
                new()
                {
                    Number = 6, Center = new() { X = 28, Y = 17 },
                    IsBarcode = true, HeatSink = HeatSinkSlot.HeatSink2, Region = new(180, 40, 80, 80),
                },
            ];
        var conveyor = new NgCarrierConveyor(io, new NgConveyorSettings(), units);
        var station = new InspectionStation(
            work,
            motion,
            new MotionStatus(motion),
            conveyor,
            gantrySettings,
            transferSettings,
            io,
            units,
            camera,
            new VirtualLightController(),
            new LightingSettings(),
            recipes);

        io.Initialize();
        motion.Initialize();
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.InspectionHeatSink2Present, true);
        io.SetInput(InputIo.InspectionBackupPlateUp, false);
        io.SetInput(InputIo.InspectionBackupPlateDown, true);
        io.SetInput(InputIo.InspectionStopperDown, false);
        io.SetInput(InputIo.InspectionStopperUp, true);
        VirtualTestSupport.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);

        Assert.Empty(work.Assemblies);
        Assert.Null(station.ActivePcb);
        Assert.Null(station.ActiveBolt);
        _ = station.NextStep;
        Assert.Null(station.ActivePcb);
        Assert.Null(station.ActiveBolt);
        Assert.Empty(work.Assemblies);

        await station.HomeHorizontalAsync();
        var barcodeResult = await station.ReadBarcodeAsync(HeatSinkSlot.HeatSink2, CancellationToken.None);
        Assert.True(VirtualTestSupport.IsAt(station.Motion.Feedback, recipes.Current.GetInspectionPosition(station.GetBarcodeFov(HeatSinkSlot.HeatSink2))));
        Assert.True(barcodeResult.Success);
        Assert.Equal("PCB-2", barcodeResult.Barcode);
        var boltResult = await station.InspectAsync(bolts[0]);
        Assert.True(VirtualTestSupport.IsAt(station.Motion.Feedback, bolts[0].InspectionPosition!));
        Assert.NotEmpty(boltResult.Frame.Pixels);
        Assert.Empty(work.Assemblies);

        using var firstStop = new CancellationTokenSource();
        var firstRun = station.RunAsync(firstStop.Token);
        Assert.True(
            await WaitUntilAsync(
                () => work.GetAssembly(HeatSinkSlot.HeatSink1).BoltPresenceResults.Count == 1,
                TimeSpan.FromSeconds(2)));
        firstStop.Cancel();
        await firstRun;

        Assert.False(work.Completed);
        Assert.Single(work.GetAssembly(HeatSinkSlot.HeatSink1).BoltPresenceResults);

        var firstBarcode = work.GetAssembly(HeatSinkSlot.HeatSink1).PcbBarcode;
        await station.RunAsync(firstStop.Token);
        Assert.Single(work.GetAssembly(HeatSinkSlot.HeatSink1).BoltPresenceResults);
        Assert.Equal(firstBarcode, work.GetAssembly(HeatSinkSlot.HeatSink1).PcbBarcode);

        io.SetInput(InputIo.InspectionHeatSink1Present, false);
        using var cancellation = new CancellationTokenSource();
        var run = station.RunAsync(cancellation.Token);
        Assert.True(await WaitUntilAsync(() => work.Completed, TimeSpan.FromSeconds(2)));

        Assert.Single(work.GetAssembly(HeatSinkSlot.HeatSink1).BoltPresenceResults);
        Assert.Equal(firstBarcode, work.GetAssembly(HeatSinkSlot.HeatSink1).PcbBarcode);
        Assert.Equal(2, work.GetAssembly(HeatSinkSlot.HeatSink2).BoltPresenceResults.Count);
        Assert.Equal(AssemblyResult.Ok, work.GetAssembly(HeatSinkSlot.HeatSink2).InspectionResult);
        VirtualTestSupport.SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
        work.ClearJob();
        io.SetInputs((InputIo.InspectionHeatSink1Present, true), (InputIo.InspectionHeatSink2Present, true));
        Assert.True(await WaitUntilAsync(() => work.Completed, TimeSpan.FromSeconds(2)));

        var heatSink1 = work.Assemblies.Single(assembly => assembly.HeatSink == HeatSinkSlot.HeatSink1);
        var heatSink2 = work.Assemblies.Single(assembly => assembly.HeatSink == HeatSinkSlot.HeatSink2);
        Assert.Equal(AssemblyResult.Ng, heatSink1.InspectionResult);
        Assert.True(heatSink1.BoltPresenceResults[VirtualTestSupport.BoltId(1)]);
        Assert.False(heatSink1.BoltPresenceResults[VirtualTestSupport.BoltId(2)]);
        Assert.Equal(AssemblyResult.Ok, heatSink2.InspectionResult);
        Assert.All(heatSink2.BoltPresenceResults.Values, Assert.True);

        VirtualTestSupport.SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
        work.ClearJob();
        Assert.False(work.CarrierPresent);

        HeatSinkAssembly[] interruptedAssemblies = [];
        camera.AfterCapture = () =>
        {
            interruptedAssemblies = work.Assemblies.ToArray();
            VirtualTestSupport.SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
            work.ClearJob();
            VirtualTestSupport.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
            cancellation.Cancel();
        };
        VirtualTestSupport.SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        VirtualTestSupport.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        var interruptedAssembly = Assert.Single(interruptedAssemblies);
        Assert.Null(interruptedAssembly.PcbBarcode);
        Assert.Empty(interruptedAssembly.BoltPresenceResults);
        Assert.Empty(work.Assemblies);
        Assert.False(work.Completed);

        var position = motion.Position;
        transferSettings.ShuttlePlacePosition = new AxisPosition
        {
            X = position.X,
            Y = position.Y,
        };
        var transferUnits = new UnitSettings();
        var transferWork = ConveyorStation.CreateInspection(io);
        var transferStation = new InspectionStation(
            transferWork,
            motion,
            new MotionStatus(motion),
            new NgCarrierConveyor(io, new(), transferUnits),
            gantrySettings,
            transferSettings,
            io,
            transferUnits,
            camera,
            new VirtualLightController(),
            new LightingSettings(),
            recipes);
        io.SetInput(InputIo.NgShuttleUp, true);
        io.SetInput(InputIo.InspectionBackupPlateUp, false);
        io.SetInput(InputIo.InspectionBackupPlateDown, true);
        Assert.Equal(InspectionStationState.WaitingForConveyor, transferStation.NextStep);
        io.SetInput(InputIo.InspectionBackupPlateDown, false);
        io.SetInput(InputIo.InspectionBackupPlateUp, true);
        io.SetInput(InputIo.InspectionStopperUp, false);
        io.SetInput(InputIo.InspectionStopperDown, true);
        transferWork.Complete(transferWork.CurrentJob);
        Assert.Equal(InspectionStationState.PickingCarrier, transferStation.NextStep);
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        io.SetInput(InputIo.NgCarrierGripperClosed, false);
        io.SetInput(InputIo.NgCarrierGripperOpen, true);
        io.SetInput(InputIo.NgCarrierDetected, true);

        Assert.Equal(InspectionStationState.PreparingTransfer, transferStation.NextStep);
        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        Assert.Equal(InspectionStationState.PreparingTransfer, transferStation.NextStep);
        Assert.Equal(transferStation.NextStep, station.NextStep);
        Assert.False(station.IsTransferPending);

        io.SetInput(InputIo.NgCarrierGripperOpen, false);
        io.SetInput(InputIo.NgCarrierGripperClosed, true);
        Assert.Equal(transferStation.NextStep, station.NextStep);
        Assert.False(station.IsTransferPending);
    }

    private sealed class MissingBoltCamera : ICamera
    {
        private readonly ICamera _camera;
        private readonly Func<(double X, double Y, double Z)> _position;
        private readonly AxisPosition _missingPosition;

        public MissingBoltCamera(
            ICamera camera,
            Func<(double X, double Y, double Z)> position,
            AxisPosition missingPosition)
        {
            _camera = camera;
            _position = position;
            _missingPosition = missingPosition;
        }

        public event Action<ImageFrame>? FrameReady
        {
            add
            {
            }

            remove
            {
            }
        }

        public event Action<Exception>? LiveViewFailed
        {
            add
            {
            }

            remove
            {
            }
        }

        public bool IsLiveView { get; }
        public Action? AfterCapture { get; set; }

        public (int Width, int Height) FrameSize => _camera.FrameSize;

        public void Initialize()
        {
            _camera.Initialize();
        }

        public async Task<ImageFrame> CaptureAsync(CancellationToken cancellationToken = default)
        {
            var image = await _camera.CaptureAsync(cancellationToken);
            var current = _position();
            var missing = Math.Abs(current.X - _missingPosition.X) <= VirtualTestSupport.PositionToleranceMillimeters
                && Math.Abs(current.Y - _missingPosition.Y) <= VirtualTestSupport.PositionToleranceMillimeters;
            var captured = missing
                ? image with
                {
                    Pixels = Enumerable.Repeat((byte)30, image.Stride * image.Height).ToArray(),
                }

                : image;
            AfterCapture?.Invoke();
            return captured;
        }

        public void StartLiveView()
        {
        }

        public void StopLiveView()
        {
        }
    }
}
