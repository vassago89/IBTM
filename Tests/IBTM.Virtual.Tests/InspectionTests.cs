using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.Storage;
using IBTM.Virtual;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTest;

namespace IBTM.Virtual.Tests;

public sealed class InspectionTests
{
    [Fact]
    public async Task ConveyorRunFeedbackCancelsInFlightInspectionBeforeRecording()
    {
        var io = new VirtualIoService(Outputs(new NgCarrierTransferHardwareSettings(), new ConveyorHardwareSettings()), new());
        io.Initialize();
        var recipes = new RecipeManager(OpenMachineStore(), new());
        recipes.Current.Pcb.BoltPoints = [new() { Number = 1, X = 0, Y = 0 }];
        recipes.Current.CarrierImages = [new() { Number = 1, IsBarcode = true, Center = new(), Region = new(0, 0, 20, 20) }];
        var settings = new InspectionGantrySettings();
        var transfer = new NgCarrierTransferSettings { WaitingPosition = new(), CarrierPickupPosition = new() };
        var operations = new OperationCancellation();
        using var motion = new VirtualMotionService(settings.Motion, operations, hasZ: false);
        motion.Initialize();
        var units = new UnitSettings { MainConveyor = false, NgConveyor = false };
        var carrier = ConveyorStation.CreateInspection(io);
        var station = new InspectionStation(carrier, motion, new(motion), new NgCarrierConveyor(io, new(), units),
            operations, settings, transfer, io, units,
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
        station.StepChanged += () =>
        {
            if (captured && station.Step is InspectionStationState.Waiting)
                stop.Cancel();
        };
        await station.RunAsync(stop.Token);

        Assert.True(captured);
        Assert.False(recorded);
        Assert.False(carrier.Completed);
    }

    [Fact]
    public async Task InspectionProcessesOnePcbAtATimeAndRestartsAtItsBarcode()
    {
        var io = new VirtualIoService(new NgCarrierTransferHardwareSettings().Outputs, new());
        io.Initialize();
        var recipes = new RecipeManager(OpenMachineStore(), new());
        recipes.Current.Pcb.BoltPoints = [
            new() { Number = 1, HeatSink = HeatSinkSlot.HeatSink1, X = 0, Y = 0 },
            new() { Number = 2, HeatSink = HeatSinkSlot.HeatSink2, X = 0, Y = 0 },
        ];
        recipes.Current.CarrierImages = [
            new() { Number = 1, IsBarcode = true, HeatSink = HeatSinkSlot.HeatSink1, Center = new(), Region = new(0, 0, 20, 20) },
            new() { Number = 2, BoltNumber = 1, HeatSink = HeatSinkSlot.HeatSink1, Center = new(), Region = new(0, 0, 20, 20) },
            new() { Number = 3, IsBarcode = true, HeatSink = HeatSinkSlot.HeatSink2, Center = new(), Region = new(0, 0, 20, 20) },
            new() { Number = 4, BoltNumber = 2, HeatSink = HeatSinkSlot.HeatSink2, Center = new(), Region = new(0, 0, 20, 20) },
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
            operations,
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
        var visited = new System.Collections.Generic.List<(HeatSinkSlot?, int?)>();
        using var firstStop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var secondStop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var interrupt = true;
        station.StepChanged += () =>
        {
            if (station.Step is not (InspectionStationState.ReadingBarcode or InspectionStationState.InspectingBolt))
                return;
            var bolt = station.ActiveBolt?.Number;
            visited.Add((station.ActivePcb, bolt));
            if (interrupt && bolt == 2)
                firstStop.Cancel();
        };
        work.Changed += () =>
        {
            if (work.Completed)
                secondStop.Cancel();
        };
        await station.RunAsync(firstStop.Token);
        Assert.Equal(new (HeatSinkSlot?, int?)[] {
            (HeatSinkSlot.HeatSink1, null), (HeatSinkSlot.HeatSink1, 1),
            (HeatSinkSlot.HeatSink2, null), (HeatSinkSlot.HeatSink2, 2),
        }, visited);
        Assert.Single(work.GetAssembly(HeatSinkSlot.HeatSink1).BoltPresenceResults);
        Assert.Empty(work.GetAssembly(HeatSinkSlot.HeatSink2).BoltPresenceResults);
        Assert.False(work.Completed);
        Assert.Null(station.ActivePcb);
        Assert.Null(station.ActiveBolt);
        visited.Clear();
        interrupt = false;
        await station.RunAsync(secondStop.Token);
        Assert.Equal(new (HeatSinkSlot?, int?)[] {
            (HeatSinkSlot.HeatSink1, null), (HeatSinkSlot.HeatSink1, 1),
            (HeatSinkSlot.HeatSink2, null), (HeatSinkSlot.HeatSink2, 2),
        }, visited);
        Assert.True(work.Completed);
        Assert.Single(work.GetAssembly(HeatSinkSlot.HeatSink2).BoltPresenceResults);
        Assert.Null(station.Step);
        Assert.Null(station.ActivePcb);
        Assert.Null(station.ActiveBolt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SavingInspectionRegionWakesTeachingWaitWithoutSensorChange(bool barcode)
    {
        var io = new VirtualIoService(new NgCarrierTransferHardwareSettings().Outputs, new());
        io.Initialize();
        var store = OpenMachineStore();
        var recipes = new RecipeManager(store, new());
        recipes.Current.Name = "Teaching wait";
        recipes.Current.Pcb.BoltPoints = [new() { Number = 1, X = 0, Y = 0 }];
        recipes.Current.CarrierImages = [
            new() { Number = 1, IsBarcode = true, Center = new(), Region = barcode ? null : new(0, 0, 20, 20) },
            new() { Number = 2, BoltNumber = 1, Center = new(), Region = barcode ? new(0, 0, 20, 20) : null },
        ];
        store.SaveRecipe(recipes.Current.Name, recipes.Current, []);
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
            operations,
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
        var waitingState = barcode ? InspectionStationState.BarcodeTeachingRequired : InspectionStationState.FovTeachingRequired;
        var resumedState = barcode ? InspectionStationState.ReadingBarcode : InspectionStationState.InspectingBolt;
        station.Trace += message =>
        {
            if (message.Contains($": {waitingState} "))
                waiting.TrySetResult();
            if (message.Contains($": {resumedState} "))
            {
                resumed.TrySetResult();
                stop.Cancel();
            }
        };
        var run = station.RunAsync(stop.Token);
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var edited = JsonSerializer.Deserialize<Recipe>(JsonSerializer.Serialize(recipes.Current))!;
            edited.CarrierImages[barcode ? 0 : 1].Region = new(0, 0, 20, 20);
            await recipes.SaveInspectionAsync(edited);
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
        var binary = DataMatrixReader.CreateBinaryImage(padded, new(180, 40, 80, 80), threshold: null);
        Assert.NotNull(binary);
        Assert.Equal((80, 80), (binary.Width, binary.Height));
        Assert.All(binary.Pixels, pixel => Assert.True(pixel is 0 or 255));
        Assert.Equal("PCB-000123", DataMatrixReader.Read(binary, new(0, 0, 80, 80)));
        var manual = DataMatrixReader.CreateBinaryImage(padded, new(180, 40, 80, 80), threshold: 128);
        Assert.Equal(BinaryChecker.Check(padded, new(180, 40, 80, 80), 128).Image.Pixels, manual!.Pixels);
        Assert.Equal("PCB-000123", DataMatrixReader.Read(padded, new(180, 40, 80, 80)));
        Assert.Equal("PCB-000123", DataMatrixReader.Read(padded, new(180, 40, 80, 80),
            new() { BinaryThreshold = 128, AutoRotate = true }));
        Assert.Null(DataMatrixReader.Read(padded, new(180, 40, 80, 80), new() { BinaryThreshold = 0 }));
        Assert.Null(DataMatrixReader.Read(padded, new(120, 80, 80, 80)));
        Assert.Throws<ArgumentOutOfRangeException>(() => DataMatrixReader.Read(padded, new(300, 0, 80, 80)));
    }

    [Fact]
    public void BinaryCheckerUsesInclusiveBrightnessThresholdAndBgrLuminance()
    {
        var image = new ImageFrame(5, 1, 15,
            [127, 127, 127, 128, 128, 128, 255, 0, 0, 0, 255, 0, 0, 0, 255]);
        var result = BinaryChecker.Check(image, new(0, 0, 5, 1), 128);

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

        var result = BinaryChecker.Check(image, region, 128);

        Assert.Equal((2, 3, 6), (result.Image.Width, result.Image.Height, result.Image.Stride));
        Assert.Equal(1.0 / 3, result.BrightRatio);
        Assert.Equal(6, result.Image.Pixels.Count(value => value == 255));
        Assert.Throws<ArgumentOutOfRangeException>(() => BinaryChecker.Check(image, region with { X = 3 }, 128));
        Assert.Throws<ArgumentOutOfRangeException>(() => BinaryChecker.Check(image, region, 256));
        Assert.Throws<ArgumentException>(() => BinaryChecker.Check(image with { Stride = 11 }, region, 128));
        Assert.Throws<ArgumentException>(() => BinaryChecker.Check(image with { Pixels = new byte[14] }, region, 128));
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
            BoltNumber = 1,
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
            operations,
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
            Assert.True(inspector.Motion.IsAt(center));
            Assert.Equal(live, inspector.IsLiveView);
            Assert.Equal(0, movements);
            Assert.NotEmpty(image.Frame.Pixels);
        }

        var bolt = new BoltPoint { Number = 1, X = 12, Y = 9 };
        recipes.Current.Pcb.BoltPoints.Add(bolt);
        var taughtPosition = fov.Center!;
        fov.Center = null;
        var capturedFov = await inspector.InspectAsync(bolt);
        Assert.True(inspector.Motion.IsAt(taughtPosition)); // ROI pixels do not alter the taught camera XY.
        Assert.Equal(fov.Region, capturedFov.Region);
        Assert.NotEmpty(capturedFov.Frame.Pixels);
        Assert.True(inspector.HasPosition(bolt));
        Assert.True(inspector.HasRegion(bolt));
        fov.Region = new(310, 30, 60, 80);
        Assert.True(inspector.HasPosition(bolt));
        Assert.False(inspector.HasRegion(bolt));
        movements = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => inspector.InspectAsync(bolt));
        Assert.Equal(0, movements);
        fov.Region = null;
        Assert.True(inspector.HasPosition(bolt));
        Assert.False(inspector.HasRegion(bolt));
        movements = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => inspector.InspectAsync(bolt));
        Assert.Equal(0, movements);

        fov.Center = taughtPosition;
        fov.IsBarcode = true;
        fov.BoltNumber = null;
        fov.Region = new(180, 40, 80, 80);
        var barcodeResult = await inspector.ReadBarcodeAsync(HeatSinkSlot.HeatSink1, CancellationToken.None);
        Assert.True(inspector.Motion.IsAt(fov.Center));
        Assert.True(barcodeResult.Success);
        Assert.Equal("PCB-000123", barcodeResult.Barcode);
        Assert.True(inspector.HasBarcodeRegion(HeatSinkSlot.HeatSink1));
        Assert.True(inspector.HasBarcodePosition(HeatSinkSlot.HeatSink1));
        Assert.False(inspector.HasBarcodeRegion(HeatSinkSlot.HeatSink2));
        Assert.False(inspector.HasBarcodePosition(HeatSinkSlot.HeatSink2));
        Assert.Equal(new PixelRegion(180, 40, 80, 80), inspector.GetBarcodeFov(HeatSinkSlot.HeatSink1).Region);
        fov.Region = new(310, 30, 60, 80);
        Assert.False(inspector.HasBarcodeRegion(HeatSinkSlot.HeatSink1));
        Assert.True(inspector.HasBarcodePosition(HeatSinkSlot.HeatSink1));
        movements = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => inspector.ReadBarcodeAsync(HeatSinkSlot.HeatSink1, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => inspector.ReadBarcodeAsync(HeatSinkSlot.HeatSink2, CancellationToken.None));
        Assert.Equal(0, movements);
    }

    [Trait("Category", "MachineFlow")]
    [Fact]
    public async Task InspectionUsesCurrentCarrierSensorsAndPreservesOwnedResults()
    {
        var io = new VirtualIoService(
            new NgCarrierTransferHardwareSettings().Outputs,
            new MachineOptions());
        var carrierReference = new CarrierReferenceSettings
        {
            UpperLeftLocatingPin = new AxisPosition { X = 2, Y = 2 },
            LowerRightLocatingPin = new AxisPosition { X = 38, Y = 28 },
        };
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
            new() { Number = 1, HeatSink = HeatSinkSlot.HeatSink1, X = 9, Y = 9 },
            new() { Number = 2, HeatSink = HeatSinkSlot.HeatSink1, X = 9, Y = 21 },
            new() { Number = 3, HeatSink = HeatSinkSlot.HeatSink2, X = 31, Y = 9 },
            new() { Number = 4, HeatSink = HeatSinkSlot.HeatSink2, X = 31, Y = 21 },
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
                BoltNumber = bolt.Number,
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
            operations,
            gantrySettings,
            transferSettings,
            io,
            units,
            camera,
            new VirtualLightController(),
            new LightingSettings(),
            recipes);
        var inspector = station;

        io.Initialize();
        motion.Initialize();
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.InspectionHeatSink2Present, true);
        io.SetInput(InputIo.InspectionBackupPlateUp, false);
        io.SetInput(InputIo.InspectionBackupPlateDown, true);
        io.SetInput(InputIo.InspectionStopperDown, false);
        io.SetInput(InputIo.InspectionStopperUp, true);
        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);

        Assert.Empty(work.Assemblies);
        Assert.Null(station.ActivePcb);
        Assert.Null(station.ActiveBolt);
        _ = station.GetNextStep();
        Assert.Null(station.ActivePcb);
        Assert.Null(station.ActiveBolt);
        Assert.Empty(work.Assemblies);

        await inspector.HomeHorizontalAsync();
        var barcodeResult = await inspector.ReadBarcodeAsync(HeatSinkSlot.HeatSink2, CancellationToken.None);
        Assert.True(inspector.Motion.IsAt(recipes.Current.GetInspectionPosition(inspector.GetBarcodeFov(HeatSinkSlot.HeatSink2))));
        Assert.True(barcodeResult.Success);
        Assert.Equal("PCB-2", barcodeResult.Barcode);
        var boltResult = await inspector.InspectAsync(bolts[0]);
        Assert.True(inspector.Motion.IsAt(bolts[0].InspectionPosition!));
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
        camera.AfterCapture = () =>
        {
            if (station.ActiveBolt is null)
                return;
            camera.AfterCapture = null;
            io.SetInput(InputIo.InspectionHeatSink1Present, true);
            io.SetInput(InputIo.InspectionHeatSink2Present, false);
            Assert.Equal(HeatSinkSlot.HeatSink2, station.ActiveBolt!.HeatSink);
        };
        using var cancellation = new CancellationTokenSource();
        var run = station.RunAsync(cancellation.Token);
        Assert.True(await WaitUntilAsync(() => work.Completed, TimeSpan.FromSeconds(2)));

        Assert.Single(work.GetAssembly(HeatSinkSlot.HeatSink1).BoltPresenceResults);
        Assert.Equal(firstBarcode, work.GetAssembly(HeatSinkSlot.HeatSink1).PcbBarcode);
        Assert.Equal(2, work.GetAssembly(HeatSinkSlot.HeatSink2).BoltPresenceResults.Count);
        Assert.Equal(AssemblyResult.Ok, work.GetAssembly(HeatSinkSlot.HeatSink2).InspectionResult);
        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
        io.SetInputs((InputIo.InspectionHeatSink1Present, true), (InputIo.InspectionHeatSink2Present, true));
        Assert.True(await WaitUntilAsync(() => work.Completed, TimeSpan.FromSeconds(2)));

        var heatSink1 = work.Assemblies.Single(assembly => assembly.HeatSink == HeatSinkSlot.HeatSink1);
        var heatSink2 = work.Assemblies.Single(assembly => assembly.HeatSink == HeatSinkSlot.HeatSink2);
        Assert.Equal(AssemblyResult.Ng, heatSink1.InspectionResult);
        Assert.True(heatSink1.BoltPresenceResults[1]);
        Assert.False(heatSink1.BoltPresenceResults[2]);
        Assert.Equal(AssemblyResult.Ok, heatSink2.InspectionResult);
        Assert.All(heatSink2.BoltPresenceResults.Values, Assert.True);

        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
        Assert.False(work.CarrierPresent);

        HeatSinkAssembly[] interruptedAssemblies = [];
        camera.AfterCapture = () =>
        {
            interruptedAssemblies = work.Assemblies.ToArray();
            VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
            VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
            cancellation.Cancel();
        };
        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
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
            operations,
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
        Assert.Equal(InspectionStationState.ReturningToWaitingPosition, transferStation.GetNextStep());
        io.SetInput(InputIo.InspectionBackupPlateDown, false);
        io.SetInput(InputIo.InspectionBackupPlateUp, true);
        io.SetInput(InputIo.InspectionStopperUp, false);
        io.SetInput(InputIo.InspectionStopperDown, true);
        transferWork.Complete(transferWork.CurrentJob);
        Assert.Equal(InspectionStationState.PickingCarrier, transferStation.GetNextStep());
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        io.SetInput(InputIo.NgCarrierGripperClosed, false);
        io.SetInput(InputIo.NgCarrierGripperOpen, true);
        io.SetInput(InputIo.NgCarrierDetected, true);

        Assert.Equal(InspectionStationState.PreparingTransfer, transferStation.GetNextStep());
        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        Assert.Equal(InspectionStationState.PlacingCarrier, transferStation.GetNextStep());
        Assert.Equal(transferStation.GetNextStep(), station.GetNextStep());
        Assert.False(station.IsTransferPending);

        io.SetInput(InputIo.NgCarrierGripperOpen, false);
        io.SetInput(InputIo.NgCarrierGripperClosed, true);
        Assert.Equal(transferStation.GetNextStep(), station.GetNextStep());
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
            var missing = Math.Abs(current.X - _missingPosition.X) <= MotionService.PositionToleranceMillimeters
                && Math.Abs(current.Y - _missingPosition.Y) <= MotionService.PositionToleranceMillimeters;
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
