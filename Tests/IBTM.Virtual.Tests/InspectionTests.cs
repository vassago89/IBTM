using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.Virtual;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTest;

namespace IBTM.Virtual.Tests;

public sealed class InspectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DataMatrixReadsOnlyTheDrawnOffCenterRegion(bool inverted)
    {
        var camera = new VirtualCamera(
            () => (10, 17, 0),
            () => [],
            () => [new(new() { X = 13, Y = 15 }, 4, 4, "PCB-000123")]);
        var image = camera.Capture(500, 0);
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
    public void BoltPredictionUsesInclusiveProbabilityThreshold()
    {
        float[] probabilities = [0, 0.5f, 0.5f, 0.9f, 1];
        var prediction = new BoltPrediction(new(1, 1, 3, [0, 0, 0]), probabilities);
        foreach (var threshold in new[] { 0f, 0.49f, 0.5f, 0.50001f, 1f })
            Assert.Equal(
                (double)probabilities.Count(value => value >= threshold) / probabilities.Length,
                prediction.MaskRatio(threshold));
    }

    [Theory]
    [InlineData(64)]
    [InlineData(256)]
    public void ModelInputUsesOnlyTheConfiguredCentralRegion(int regionSize)
    {
        const int width = 400;
        const int height = 300;
        const int stride = width * ImageFrame.ColorChannelCount + 4;
        var pixels = Enumerable.Repeat((byte)255, stride * height).ToArray();
        var left = (width - regionSize) / 2;
        var top = (height - regionSize) / 2;
        for (var y = 0; y < regionSize; y++)
            for (var x = 0; x < regionSize; x++)
                for (var channel = 0; channel < ImageFrame.ColorChannelCount; channel++)
                    pixels[(top + y) * stride + (left + x) * ImageFrame.ColorChannelCount + channel] = (byte)(10 + 40 * x / (regionSize - 1) + 80 * y / (regionSize - 1) + channel);
        var source = new ImageFrame(width, height, stride, pixels);

        var input = BoltImageInput.Create(source, regionSize);

        Assert.Equal((128, 128, 384), (input.Width, input.Height, input.Stride));
        Assert.Equal(10, input.Pixels[0]);
        Assert.InRange(input.Pixels[127 * 3], (byte)49, (byte)50);
        Assert.InRange(input.Pixels[127 * input.Stride], (byte)89, (byte)90);
        Assert.InRange(input.Pixels[^1], (byte)131, (byte)132);
        Assert.DoesNotContain((byte)255, input.Pixels);
        Assert.Equal(255, source.Pixels[0]);
    }

    [Fact]
    public void ModelInputUsesTheDrawnOffCenterRectangle()
    {
        const int width = 400;
        const int height = 300;
        const int stride = width * 3 + 4;
        var pixels = Enumerable.Repeat((byte)255, stride * height).ToArray();
        var region = new PixelRegion(20, 170, 64, 96);
        for (var y = region.Y; y < region.Y + region.Height; y++)
            for (var x = region.X; x < region.X + region.Width; x++)
                for (var channel = 0; channel < 3; channel++)
                    pixels[y * stride + x * 3 + channel] = (byte)(10 + channel);
        var image = new ImageFrame(width, height, stride, pixels);

        var input = BoltImageInput.Create(image, region);

        Assert.Equal((128, 128, 384), (input.Width, input.Height, input.Stride));
        for (var index = 0; index < input.Pixels.Length; index++)
            Assert.Equal((byte)(10 + index % 3), input.Pixels[index]);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BoltImageInput.Create(image, region with { X = 390 }));
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
        var gantry = new InspectionGantry(motion, new NgCarrierTransfer(io), operations, settings);
        Assert.True(await gantry.HomeHorizontalAsync());
        var recipe = new BoltInspectionRecipe();
        var fov = new CarrierImageTile
        {
            Number = 1,
            Center = new() { X = 12, Y = 9 },
            Region = new(200, 30, 60, 80),
            BoltNumber = 1,
            HeatSink = HeatSinkSlot.HeatSink1,
        };
        var inspector = new BoltInspector(
            gantry,
            new VirtualCamera(
                motion.GetPosition,
                () => [],
                () => [new(new() { X = 15, Y = 7 }, 4, 4, "PCB-000123")]),
            new VirtualLightController(),
            new BoltPresenceDetector(() => new(), new VirtualBoltRecessSegmenter(), () => 0.5f),
            settings,
            new LightingSettings(),
            () => recipe,
            () => [fov]);
        var inspections = new List<BoltInspectionImage>();
        inspector.Inspected += inspections.Add;

        if (live)
            await inspector.StartLiveViewAsync();

        var movements = 0;
        motion.PositionChanged += (_, _, _) => movements++;
        foreach (var center in new[] { new AxisPosition { X = 12, Y = 9 }, new AxisPosition { X = 27, Y = 16 } })
        {
            await gantry.MoveToAsync(center, 1_000);
            movements = 0;
            var image = await inspector.CaptureCarrierImageAsync();
            Assert.Equal((center.X, center.Y), (image.Center.X, image.Center.Y));
            Assert.True(gantry.IsAt(center));
            Assert.Equal(live, inspector.IsLiveView);
            Assert.Equal(0, movements);
            Assert.NotEmpty(image.Frame.Pixels);
        }
        Assert.Empty(inspections); // Carrier teaching images are not automatic bolt inspections.

        var bolt = new BoltTarget(new() { Number = 1, X = 999, Y = 999 });
        var capturedFov = await inspector.CaptureAsync(bolt);
        Assert.True(gantry.IsAt(fov.Center)); // Never move the camera center to the bolt / ROI center.
        Assert.Equal(128, inspector.Predict(capturedFov, fov.Region!).Input.Width);
        fov.Region = null;
        Assert.False(inspector.HasPosition(bolt));
        movements = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => inspector.CaptureAsync(bolt));
        Assert.Equal(0, movements);

        fov.IsBarcode = true;
        fov.BoltNumber = null;
        fov.Region = new(180, 40, 80, 80);
        var barcodeImage = await inspector.CaptureBarcodeAsync(HeatSinkSlot.HeatSink1);
        Assert.True(gantry.IsAt(fov.Center));
        Assert.Equal("PCB-000123", DataMatrixReader.Read(barcodeImage, fov.Region));
        Assert.True(inspector.HasBarcodeRegion(HeatSinkSlot.HeatSink1));
        Assert.False(inspector.HasBarcodeRegion(HeatSinkSlot.HeatSink2));
        Assert.Equal(new PixelRegion(180, 40, 80, 80), inspector.GetBarcodeFov(HeatSinkSlot.HeatSink1).Region);
        fov.Region = new(310, 30, 60, 80);
        Assert.False(inspector.HasBarcodeRegion(HeatSinkSlot.HeatSink1));
        movements = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => inspector.CaptureBarcodeAsync(HeatSinkSlot.HeatSink1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => inspector.CaptureBarcodeAsync(HeatSinkSlot.HeatSink2));
        Assert.Equal(0, movements);
    }

    [Trait("Category", "MachineFlow")]
    [Fact]
    public async Task InspectionUsesCurrentCarrierSensorsAndRestartsIncompleteWork()
    {
        var io = new VirtualIoService(
            new NgCarrierTransferHardwareSettings().Outputs,
            new MachineOptions());
        var transferFeedback = new NgCarrierTransfer(io);
        var work = new InspectionWork(ConveyorStation.Inspection(io), transferFeedback);
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
        var transfer = new NgCarrierTransfer(io);
        var gantry = new InspectionGantry(motion, transfer, operations, gantrySettings);
        BoltTarget[] bolts = [
            Bolt(1, HeatSinkSlot.HeatSink1, 9, 9, carrierReference),
            Bolt(2, HeatSinkSlot.HeatSink1, 9, 21, carrierReference),
            Bolt(3, HeatSinkSlot.HeatSink2, 31, 9, carrierReference),
            Bolt(4, HeatSinkSlot.HeatSink2, 31, 21, carrierReference),
        ];
        var camera = new MissingBoltCamera(
            new VirtualCamera(
                motion.GetPosition,
                () => bolts.Select(bolt => gantrySettings.GetBoltPosition(bolt, carrierReference)),
                () => [
            new(new() { X = 13, Y = 15 }, 4, 4, "PCB-1"),
            new(new() { X = 31, Y = 15 }, 4, 4, "PCB-2")
        ]),
            motion.GetPosition,
            gantrySettings.GetBoltPosition(bolts[1], carrierReference));
        var segmenter = new CountingSegmenter();
        var inspector = new BoltInspector(
            gantry,
            camera,
            new VirtualLightController(),
            new BoltPresenceDetector(() => new BoltInspectionRecipe(), segmenter, () => 0.5f),
            gantrySettings,
            new LightingSettings(),
            () => new(),
            () => [.. bolts.Select((bolt, index) => new CarrierImageTile
            {
                Number = index + 1,
                Center = gantrySettings.GetBoltPosition(bolt, carrierReference),
                Region = new(96, 56, 128, 128),
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
            ]);
        var inspections = new List<BoltInspectionImage>();
        inspector.Inspected += inspections.Add;
        var shuttleFeedback = new NgShuttleFeedback(io);
        var conveyor = new NgCarrierConveyor(io, new NgConveyorSettings(), shuttleFeedback);
        var shuttle = new NgShuttle(io, conveyor, shuttleFeedback, transferFeedback);
        var transferSettings = new NgCarrierTransferSettings();
        var station = new InspectionStation(
            work,
            inspector,
            transfer,
            new NgCarrierMove(work, shuttle, transfer, gantry, transferSettings),
            shuttle,
            isTransferEnabled: () => false,
            isConveyorEnabled: () => true);

        io.Initialize();
        motion.Initialize();
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.InspectionHeatSink2Present, true);
        io.SetInput(InputIo.InspectionBackupPlateUp, true);
        io.SetInput(InputIo.InspectionStopperDown, true);
        io.SetInput(InputIo.InspectionStopperUp, false);
        io.SetInput(InputIo.InspectionCarrierPresent, true);

        var barcodeImage = await inspector.CaptureBarcodeAsync(HeatSinkSlot.HeatSink2);
        Assert.True(inspector.IsAtBarcode(HeatSinkSlot.HeatSink2));
        Assert.Equal("PCB-2", DataMatrixReader.Read(barcodeImage, inspector.GetBarcodeFov(HeatSinkSlot.HeatSink2).Region!));
        var boltImage = await inspector.CaptureAsync(bolts[0]);
        Assert.True(gantry.IsAt(gantrySettings.GetBoltPosition(bolts[0], carrierReference)));
        Assert.NotEmpty(boltImage.Pixels);
        Assert.Empty(inspections);

        using var firstStop = new CancellationTokenSource();
        var firstRun = station.RunAsync(bolts, firstStop.Token);
        Assert.True(
            await WaitUntilAsync(
                () => work.Assembly(HeatSinkSlot.HeatSink1).BoltPresenceResults.Count == 1,
                TimeSpan.FromSeconds(2)));
        firstStop.Cancel();
        await firstRun;

        Assert.False(work.Completed);
        Assert.Single(work.Assembly(HeatSinkSlot.HeatSink1).BoltPresenceResults);

        io.SetInput(InputIo.InspectionHeatSink1Present, false);
        camera.AfterCapture = () =>
        {
            if (station.ActiveBolt(bolts) is null)
                return;
            camera.AfterCapture = null;
            io.SetInput(InputIo.InspectionHeatSink1Present, true);
            io.SetInput(InputIo.InspectionHeatSink2Present, false);
            Assert.Equal(HeatSinkSlot.HeatSink2, station.ActiveBolt(bolts)!.HeatSink);
        };
        using var cancellation = new CancellationTokenSource();
        var run = station.RunAsync(bolts, cancellation.Token);
        Assert.True(await WaitUntilAsync(() => work.Completed, TimeSpan.FromSeconds(2)));

        Assert.Empty(work.Assembly(HeatSinkSlot.HeatSink1).BoltPresenceResults);
        Assert.Equal(2, work.Assembly(HeatSinkSlot.HeatSink2).BoltPresenceResults.Count);
        Assert.Equal(AssemblyResult.Ok, work.Assembly(HeatSinkSlot.HeatSink2).InspectionResult);
        io.SetInput(InputIo.InspectionHeatSink1Present, false);
        io.SetInput(InputIo.InspectionHeatSink2Present, false);
        Assert.True(work.Completed);
        Assert.False(work.HasNg);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.InspectionHeatSink2Present, true);
        Assert.True(work.Completed);

        io.SetInput(InputIo.InspectionCarrierPresent, false);
        io.SetInput(InputIo.InspectionCarrierPresent, true);
        Assert.True(await WaitUntilAsync(() => work.Completed, TimeSpan.FromSeconds(2)));

        var heatSink1 = work.Assemblies.Single(assembly => assembly.HeatSink == HeatSinkSlot.HeatSink1);
        var heatSink2 = work.Assemblies.Single(assembly => assembly.HeatSink == HeatSinkSlot.HeatSink2);
        Assert.Equal(AssemblyResult.Ng, heatSink1.InspectionResult);
        Assert.True(heatSink1.BoltPresenceResults[1]);
        Assert.False(heatSink1.BoltPresenceResults[2]);
        Assert.Equal(AssemblyResult.Ok, heatSink2.InspectionResult);
        Assert.All(heatSink2.BoltPresenceResults.Values, Assert.True);

        io.SetInput(InputIo.InspectionCarrierPresent, false);
        io.SetInput(InputIo.InspectionHeatSink1Present, false);
        io.SetInput(InputIo.InspectionHeatSink2Present, false);
        io.SetInput(InputIo.InspectionCarrierPresent, true);

        Assert.True(await WaitUntilAsync(() => work.Completed, TimeSpan.FromSeconds(2)));
        Assert.True(work.HasNg);
        Assert.Empty(work.Assemblies);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        Assert.True(work.Completed);
        Assert.True(work.HasNg);
        Assert.Empty(work.Assemblies);

        var segmentCallsAtStop = 0;
        camera.AfterCapture = () =>
        {
            segmentCallsAtStop = segmenter.Calls;
            io.SetInput(InputIo.InspectionCarrierPresent, false);
            io.SetInput(InputIo.InspectionCarrierPresent, true);
            cancellation.Cancel();
        };
        io.SetInput(InputIo.InspectionCarrierPresent, false);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.InspectionCarrierPresent, true);
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(segmentCallsAtStop, segmenter.Calls);
        Assert.Equal(segmenter.Calls, inspections.Count);
        Assert.Contains(
            inspections,
            image =>
                image.BoltNumber == 2
                    && image.HeatSink == HeatSinkSlot.HeatSink1
                    && !image.Present);
        Assert.All(
            inspections,
            image =>
            {
                Assert.Equal(128, image.RegionSize);
                Assert.True(image.Image.Width > 128 && image.Image.Height > 128);
            });
        Assert.Empty(work.Assemblies);
        Assert.False(work.Completed);

        var position = motion.GetPosition();
        transferSettings.ShuttlePlacePosition = new AxisPosition
        {
            X = position.X,
            Y = position.Y,
        };
        var transferWork = new InspectionWork(
            ConveyorStation.Inspection(io),
            transferFeedback,
            isEnabled: () => false);
        var transferStation = new InspectionStation(
            transferWork,
            inspector,
            transfer,
            new NgCarrierMove(transferWork, shuttle, transfer, gantry, transferSettings),
            shuttle,
            isTransferEnabled: () => true,
            isConveyorEnabled: () => true);
        io.SetInput(InputIo.NgShuttleUp, true);
        io.SetInput(InputIo.InspectionBackupPlateUp, false);
        io.SetInput(InputIo.InspectionBackupPlateDown, true);
        Assert.Equal(InspectionStationState.Waiting, transferStation.State([]));
        io.SetInput(InputIo.InspectionBackupPlateDown, false);
        io.SetInput(InputIo.InspectionBackupPlateUp, true);
        Assert.Equal(InspectionStationState.MovingTransferToCarrier, transferStation.State([]));
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        io.SetInput(InputIo.NgCarrierGripperClosed, false);
        io.SetInput(InputIo.NgCarrierGripperOpen, true);
        io.SetInput(InputIo.NgCarrierDetected, true);

        Assert.Equal(InspectionStationState.WaitingForShuttleCarrier, transferStation.State([]));
        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        Assert.Equal(InspectionStationState.RaisingCarrierTransfer, transferStation.State([]));
        Assert.Equal(InspectionStationState.Waiting, station.State(bolts));

        io.SetInput(InputIo.NgCarrierGripperOpen, false);
        io.SetInput(InputIo.NgCarrierGripperClosed, true);
        Assert.Equal(InspectionStationState.Waiting, station.State(bolts));
    }

    private static BoltTarget Bolt(
        int number,
        HeatSinkSlot heatSink,
        double x,
        double y,
        CarrierReferenceSettings reference)
    {
        var position = CarrierCoordinates.FromMachine(
            new AxisPosition { X = x, Y = y },
            reference.UpperLeftLocatingPin!);
        return new BoltTarget(new BoltPoint { Number = number, HeatSink = heatSink, X = position.X, Y = position.Y });
    }

    private sealed class CountingSegmenter : IBoltRecessSegmenter
    {
        private readonly VirtualBoltRecessSegmenter _inner = new();
        public int Calls { get; private set; }

        public void CheckReady()
        {
            _inner.CheckReady();
        }

        public void Reload()
        {
            _inner.Reload();
        }

        public float[] Segment(ImageFrame image)
        {
            Calls++;
            return _inner.Segment(image);
        }
    }

    private sealed class MissingBoltCamera(
        ICamera camera,
        Func<(double X, double Y, double Z)> position,
        AxisPosition missingPosition) : ICamera
    {
        public bool IsLiveView { get; }
        public Action? AfterCapture { get; set; }

        public (int Width, int Height) FrameSize
        {
            get
            {
                return camera.FrameSize;
            }
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

        public void Initialize()
        {
            camera.Initialize();
        }

        public ImageFrame Capture(double exposureMicroseconds, double gain)
        {
            var image = camera.Capture(exposureMicroseconds, gain);
            var current = position();
            var missing = Math.Abs(current.X - missingPosition.X) <= MotionService.PositionToleranceMillimeters
                && Math.Abs(current.Y - missingPosition.Y) <= MotionService.PositionToleranceMillimeters;
            var captured = missing
                ? image with
                {
                    Pixels = Enumerable.Repeat((byte)30, image.Stride * image.Height).ToArray(),
                }

                : image;
            AfterCapture?.Invoke();
            return captured;
        }

        public void StartLiveView(double exposureMicroseconds, double gain)
        {
        }

        public void StopLiveView()
        {
        }
    }
}
