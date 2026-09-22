using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        Assert.Equal("PCB-000123", DataMatrixReader.Read(padded, new(180, 40, 80, 80)));
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
        var reference = new CarrierReferenceSettings
        {
            UpperLeftLocatingPin = new() { X = 2, Y = 3 },
            LowerRightLocatingPin = new() { X = 32, Y = 23 },
        };
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
        var work = new InspectionWork(io, motion, new(), units);
        var inspector = new InspectionStation(
            work, new NgCarrierConveyor(io, new(), work, units), operations, settings, new(), io,
            units,
            new VirtualCamera(
                motion.GetPosition,
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
            Assert.True(inspector.IsAt(center));
            Assert.Equal(live, inspector.IsLiveView);
            Assert.Equal(0, movements);
            Assert.NotEmpty(image.Frame.Pixels);
        }

        var bolt = new BoltPoint { Number = 1, X = 999, Y = 999 };
        var capturedFov = await inspector.CaptureAsync(bolt);
        Assert.True(inspector.IsAt(fov.Center)); // Never move the camera center to the bolt / ROI center.
        Assert.Equal(fov.Region!.Width, inspector.Check(capturedFov, fov.Region, bolt).Image.Width);
        Assert.True(inspector.HasPosition(bolt));
        Assert.True(inspector.HasRegion(bolt));
        fov.Region = new(310, 30, 60, 80);
        Assert.True(inspector.HasPosition(bolt));
        Assert.False(inspector.HasRegion(bolt));
        movements = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => inspector.CaptureAsync(bolt));
        Assert.Equal(0, movements);
        fov.Region = null;
        Assert.True(inspector.HasPosition(bolt));
        Assert.False(inspector.HasRegion(bolt));
        movements = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => inspector.CaptureAsync(bolt));
        Assert.Equal(0, movements);

        fov.IsBarcode = true;
        fov.BoltNumber = null;
        fov.Region = new(180, 40, 80, 80);
        var barcodeImage = await inspector.CaptureBarcodeAsync(HeatSinkSlot.HeatSink1);
        Assert.True(inspector.IsAt(fov.Center));
        Assert.Equal("PCB-000123", DataMatrixReader.Read(barcodeImage, fov.Region));
        Assert.True(inspector.HasBarcodeRegion(HeatSinkSlot.HeatSink1));
        Assert.True(inspector.HasBarcodePosition(HeatSinkSlot.HeatSink1));
        Assert.False(inspector.HasBarcodeRegion(HeatSinkSlot.HeatSink2));
        Assert.False(inspector.HasBarcodePosition(HeatSinkSlot.HeatSink2));
        Assert.Equal(new PixelRegion(180, 40, 80, 80), inspector.GetBarcodeFov(HeatSinkSlot.HeatSink1).Region);
        fov.Region = new(310, 30, 60, 80);
        Assert.False(inspector.HasBarcodeRegion(HeatSinkSlot.HeatSink1));
        Assert.True(inspector.HasBarcodePosition(HeatSinkSlot.HeatSink1));
        movements = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => inspector.CaptureBarcodeAsync(HeatSinkSlot.HeatSink1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => inspector.CaptureBarcodeAsync(HeatSinkSlot.HeatSink2));
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
        var transferSettings = new NgCarrierTransferSettings { PickupSafeX = 0 };
        var units = new UnitSettings { MainConveyor = false };
        var recipes = new RecipeManager(OpenMachineStore(), new());
        var work = new InspectionWork(io, motion, transferSettings, units);
        BoltPoint[] bolts = [
            new() { Number = 1, HeatSink = HeatSinkSlot.HeatSink1, X = 9, Y = 9 },
            new() { Number = 2, HeatSink = HeatSinkSlot.HeatSink1, X = 9, Y = 21 },
            new() { Number = 3, HeatSink = HeatSinkSlot.HeatSink2, X = 31, Y = 9 },
            new() { Number = 4, HeatSink = HeatSinkSlot.HeatSink2, X = 31, Y = 21 },
        ];
        var camera = new MissingBoltCamera(
            new VirtualCamera(
                motion.GetPosition,
                () => bolts.Select(gantrySettings.GetBoltPosition),
                () => [
                    new(new() { X = 13, Y = 15 }, 4, 4, "PCB-1"),
                    new(new() { X = 31, Y = 15 }, 4, 4, "PCB-2")
        ]),
            motion.GetPosition,
            gantrySettings.GetBoltPosition(bolts[1]));
        recipes.Current.CarrierImages = [.. bolts.Select((bolt, index) => new CarrierImageTile
            {
                Number = index + 1,
                Center = gantrySettings.GetBoltPosition(bolt),
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
        var conveyor = new NgCarrierConveyor(io, new NgConveyorSettings(), work, units);
        var station = new InspectionStation(
            work, conveyor, operations, gantrySettings, transferSettings, io,
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
        Assert.Equal(HeatSinkSlot.HeatSink1, station.GetActivePcb());
        _ = station.GetState();
        Assert.Empty(work.Assemblies);

        var barcodeImage = await inspector.CaptureBarcodeAsync(HeatSinkSlot.HeatSink2);
        Assert.True(inspector.IsAtBarcode(HeatSinkSlot.HeatSink2));
        Assert.Equal("PCB-2", DataMatrixReader.Read(barcodeImage, inspector.GetBarcodeFov(HeatSinkSlot.HeatSink2).Region!));
        var boltImage = await inspector.CaptureAsync(bolts[0]);
        Assert.True(inspector.IsAt(gantrySettings.GetBoltPosition(bolts[0])));
        Assert.NotEmpty(boltImage.Pixels);
        Assert.Empty(work.Assemblies);

        using var firstStop = new CancellationTokenSource();
        var firstRun = station.RunAsync(bolts, firstStop.Token);
        Assert.True(
            await WaitUntilAsync(
                () => work.GetAssembly(HeatSinkSlot.HeatSink1).BoltPresenceResults.Count == 1,
                TimeSpan.FromSeconds(2)));
        firstStop.Cancel();
        await firstRun;

        Assert.False(work.Completed);
        Assert.Single(work.GetAssembly(HeatSinkSlot.HeatSink1).BoltPresenceResults);

        var firstBarcode = work.GetAssembly(HeatSinkSlot.HeatSink1).PcbBarcode;
        await station.RunAsync(bolts, firstStop.Token);
        Assert.Single(work.GetAssembly(HeatSinkSlot.HeatSink1).BoltPresenceResults);
        Assert.Equal(firstBarcode, work.GetAssembly(HeatSinkSlot.HeatSink1).PcbBarcode);

        io.SetInput(InputIo.InspectionHeatSink1Present, false);
        camera.AfterCapture = () =>
        {
            if (station.GetActiveBolt() is null)
                return;
            camera.AfterCapture = null;
            io.SetInput(InputIo.InspectionHeatSink1Present, true);
            io.SetInput(InputIo.InspectionHeatSink2Present, false);
            Assert.Equal(HeatSinkSlot.HeatSink2, station.GetActiveBolt()!.HeatSink);
        };
        using var cancellation = new CancellationTokenSource();
        var run = station.RunAsync(bolts, cancellation.Token);
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
        Assert.False(work.Station.CarrierPresent);

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

        var position = motion.GetPosition();
        transferSettings.ShuttlePlacePosition = new AxisPosition
        {
            X = position.X,
            Y = position.Y,
        };
        var transferUnits = new UnitSettings();
        var transferWork = new InspectionWork(
            io,
            motion,
            transferSettings,
            transferUnits);
        var transferStation = new InspectionStation(
            transferWork, new NgCarrierConveyor(io, new(), transferWork, transferUnits),
            operations, gantrySettings, transferSettings, io,
            transferUnits,
            camera,
            new VirtualLightController(),
            new LightingSettings(),
            recipes);
        io.SetInput(InputIo.NgShuttleUp, true);
        io.SetInput(InputIo.InspectionBackupPlateUp, false);
        io.SetInput(InputIo.InspectionBackupPlateDown, true);
        Assert.Equal(InspectionStationState.ReturningToWaitingPosition, transferStation.GetState());
        io.SetInput(InputIo.InspectionBackupPlateDown, false);
        io.SetInput(InputIo.InspectionBackupPlateUp, true);
        io.SetInput(InputIo.InspectionStopperUp, false);
        io.SetInput(InputIo.InspectionStopperDown, true);
        transferWork.Complete(transferWork.CurrentJob);
        Assert.Equal(InspectionStationState.PickingCarrier, transferStation.GetState());
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        io.SetInput(InputIo.NgCarrierGripperClosed, false);
        io.SetInput(InputIo.NgCarrierGripperOpen, true);
        io.SetInput(InputIo.NgCarrierDetected, true);

        Assert.Equal(InspectionStationState.PreparingTransfer, transferStation.GetState());
        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        Assert.Equal(InspectionStationState.PlacingCarrier, transferStation.GetState());
        Assert.Equal(transferStation.GetState(), station.GetState());
        Assert.False(station.IsTransferPending);

        io.SetInput(InputIo.NgCarrierGripperOpen, false);
        io.SetInput(InputIo.NgCarrierGripperClosed, true);
        Assert.Equal(transferStation.GetState(), station.GetState());
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
