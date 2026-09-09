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
    public void DataMatrixReadsOriginalCentralRegion(bool inverted)
    {
        var camera = new VirtualCamera(() => (13, 15, 0), () => [],
            () => [new(new() { X = 13, Y = 15 }, 4, 4, "PCB-000123")]);
        var image = camera.Capture(500, 0);
        var stride = image.Stride + 5;
        var pixels = new byte[stride * image.Height];
        for (var row = 0; row < image.Height; row++)
        for (var column = 0; column < image.Stride; column++)
            pixels[row * stride + column] = inverted
                ? (byte)(255 - image.Pixels[row * image.Stride + column]) : image.Pixels[row * image.Stride + column];
        var padded = new ImageFrame(image.Width, image.Height, stride, pixels);
        Assert.Equal("PCB-000123", DataMatrixReader.Read(padded, 80, 80));
        Assert.Null(DataMatrixReader.Read(padded, 20, 20));
    }

    [Fact]
    public void BoltPredictionUsesInclusiveProbabilityThreshold()
    {
        float[] probabilities = [0, 0.5f, 0.5f, 0.9f, 1];
        var prediction = new BoltPrediction(new(1, 1, 3, [0, 0, 0]), probabilities);
        foreach (var threshold in new[] { 0f, 0.49f, 0.5f, 0.50001f, 1f })
            Assert.Equal((double)probabilities.Count(value => value >= threshold) / probabilities.Length,
                prediction.MaskRatio(threshold));
    }

    [Theory]
    [InlineData(64)]
    [InlineData(128)]
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
                    pixels[(top + y) * stride + (left + x) * ImageFrame.ColorChannelCount + channel] =
                        (byte)(10 + 40 * x / (regionSize - 1) + 80 * y / (regionSize - 1) + channel);
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

    [Theory]
    [InlineData(2, 3, 32, 23)]
    [InlineData(32, 23, 2, 3)]
    public async Task CarrierScanUsesTheTwoReferencePins(
        double left, double top, double right, double bottom)
    {
        var reference = new CarrierReferenceSettings
        {
            UpperLeftLocatingPin = new() { X = left, Y = top },
            LowerRightLocatingPin = new() { X = right, Y = bottom },
        };
        var settings = new InspectionGantrySettings
        {
            Motion = new() { HorizontalSpeed = 1_000 },
        };
        var operations = new OperationCancellation();
        using var motion = new VirtualMotionService(
            settings.Motion, operations, hasZ: false,
            xRange: (0, 40), yRange: (0, 30));
        var io = new VirtualIoService(
            new NgCarrierTransferHardwareSettings().Outputs, new());
        io.Initialize();
        motion.Initialize();
        var gantry = new InspectionGantry(motion, new NgCarrierTransfer(io), operations);
        Assert.True(await gantry.HomeHorizontalAsync(1_000));
        var scale = 0.05;
        var recipe = new BoltInspectionRecipe();
        var pcb = TaughtPcbLayout();
        var inspector = new BoltInspector(
            gantry,
            new VirtualCamera(motion.GetPosition, () => []),
            new VirtualLightController(),
            new BoltPresenceDetector(() => new(), new VirtualBoltRecessSegmenter(), () => 0.5f),
            settings, reference, new LightingSettings(), () => recipe, () => pcb, () => scale);
        var inspections = new List<BoltInspectionImage>();
        inspector.Inspected += inspections.Add;

        var images = await inspector.CaptureCarrierImagesAsync();
        var middleX = (left + right) / 2;
        var middleY = (top + bottom) / 2;
        Assert.Equal(
            new[]
            {
                (left, top), (middleX, top), (right, top),
                (right, middleY), (middleX, middleY), (left, middleY),
                (left, bottom), (middleX, bottom), (right, bottom),
            },
            images.Select(image => (image.Center.X, image.Center.Y)));
        Assert.True(gantry.IsAt(reference.LowerRightLocatingPin));
        Assert.All(images, image => Assert.NotEmpty(image.Frame.Pixels));
        Assert.Empty(inspections); // Carrier teaching images are not automatic bolt inspections.

        pcb.DataMatrix = new(0, 0, 10, 4);
        Assert.Equal((16, 12), inspector.FieldOfView);
        Assert.True(inspector.HasBarcodeRegion(HeatSinkSlot.HeatSink1));
        scale = 0.025;
        Assert.Equal((8, 6), inspector.FieldOfView);
        Assert.Equal((400, 160), inspector.BarcodePixelSize());
        Assert.False(inspector.HasBarcodeRegion(HeatSinkSlot.HeatSink1));
        images = await inspector.CaptureCarrierImagesAsync();
        Assert.Equal(30, images.Count); // 6 columns at <= 7 mm, 5 rows at <= 5 mm.
        var xs = images.Select(image => image.Center.X).Distinct().Order().ToArray();
        var ys = images.Select(image => image.Center.Y).Distinct().Order().ToArray();
        Assert.All(xs.Zip(xs.Skip(1)), pair => Assert.InRange(pair.Second - pair.First, 0, 7));
        Assert.All(ys.Zip(ys.Skip(1)), pair => Assert.InRange(pair.Second - pair.First, 0, 5));
        recipe.CarrierScanOverlapMillimeters = inspector.FieldOfView.Height;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => inspector.CaptureCarrierImagesAsync());
    }

    [Fact]
    public async Task InspectionUsesCurrentCarrierSensorsAndRestartsIncompleteWork()
    {
        var io = new VirtualIoService(
            new NgCarrierTransferHardwareSettings().Outputs,
            new MachineOptions());
        var transferFeedback = new TestNgCarrierTransferFeedback(io);
        var work = new InspectionWork(
            ConveyorStation.Inspection(io),
            transferFeedback);
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
            hasZ: false,
            xRange: (0, 40),
            yRange: (0, 30));
        var transfer = new NgCarrierTransfer(io);
        var gantry = new InspectionGantry(motion, transfer, operations);
        BoltTarget[] bolts =
        [
            Bolt(1, HeatSinkSlot.HeatSink1, 9, 9, carrierReference),
            Bolt(2, HeatSinkSlot.HeatSink1, 9, 21, carrierReference),
            Bolt(3, HeatSinkSlot.HeatSink2, 31, 9, carrierReference),
            Bolt(4, HeatSinkSlot.HeatSink2, 31, 21, carrierReference),
        ];
        var camera = new MissingBoltCamera(
            new VirtualCamera(
                motion.GetPosition,
                () => bolts.Select(
                    bolt => gantrySettings.GetBoltPosition(
                        bolt,
                        carrierReference)),
                () => [new(new() { X = 13, Y = 15 }, 4, 4, "PCB-1"),
                    new(new() { X = 31, Y = 15 }, 4, 4, "PCB-2")]),
            motion.GetPosition,
            gantrySettings.GetBoltPosition(bolts[1], carrierReference));
        var segmenter = new CountingSegmenter();
        var inspector = new BoltInspector(
            gantry,
            camera,
            new VirtualLightController(),
            new BoltPresenceDetector(
                () => new BoltInspectionRecipe(),
                segmenter, () => 0.5f),
            gantrySettings,
            carrierReference,
            new LightingSettings(), () => new(), TaughtPcbLayout, () => 0.05);
        var inspections = new List<BoltInspectionImage>();
        inspector.Inspected += inspections.Add;
        var shuttleFeedback = new NgShuttleFeedback(io);
        var conveyor = new NgCarrierConveyor(
            io,
            new NgConveyorSettings(),
            shuttleFeedback);
        var shuttle = new NgShuttle(io, conveyor, shuttleFeedback, transferFeedback);
        var transferSettings = new NgCarrierTransferSettings();
        var station = new InspectionStation(
            work,
            inspector,
            transfer,
            new NgCarrierMove(work, shuttle, transfer, gantry, transferSettings),
            shuttle,
            isTransferEnabled: () => false);

        io.Initialize();
        motion.Initialize();
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.InspectionHeatSink2Present, true);
        io.SetInput(InputIo.InspectionBackupPlateUp, true);
        io.SetInput(InputIo.InspectionStopperDown, true);
        io.SetInput(InputIo.InspectionCarrierPresent, true);

        var barcodeImage = await inspector.CaptureBarcodeAsync(HeatSinkSlot.HeatSink2);
        Assert.True(inspector.IsAtBarcode(HeatSinkSlot.HeatSink2));
        Assert.Equal("PCB-2", inspector.ReadBarcode(barcodeImage));
        var boltImage = await inspector.CaptureAsync(bolts[0]);
        Assert.True(gantry.IsAt(gantrySettings.GetBoltPosition(bolts[0], carrierReference)));
        Assert.NotEmpty(boltImage.Pixels);
        Assert.Empty(inspections);

        using var firstStop = new CancellationTokenSource();
        var firstRun = station.RunAsync(bolts, firstStop.Token);
        Assert.True(await WaitUntilAsync(
            () => work.Assembly(HeatSinkSlot.HeatSink1)
                .BoltPresenceResults.Count == 1,
            TimeSpan.FromSeconds(2)));
        firstStop.Cancel();
        await firstRun;

        Assert.False(work.Completed);
        Assert.Single(work.Assembly(HeatSinkSlot.HeatSink1)
            .BoltPresenceResults);

        io.SetInput(InputIo.InspectionHeatSink1Present, false);
        camera.AfterCapture = () =>
        {
            if (station.ActiveBolt(bolts) is null) return;
            camera.AfterCapture = null;
            io.SetInput(InputIo.InspectionHeatSink1Present, true);
            io.SetInput(InputIo.InspectionHeatSink2Present, false);
            Assert.Equal(HeatSinkSlot.HeatSink2, station.ActiveBolt(bolts)!.HeatSink);
        };
        using var cancellation = new CancellationTokenSource();
        var run = station.RunAsync(bolts, cancellation.Token);
        Assert.True(await WaitUntilAsync(
            () => work.Completed,
            TimeSpan.FromSeconds(2)));

        Assert.Empty(work.Assembly(HeatSinkSlot.HeatSink1).BoltPresenceResults);
        Assert.Equal(2, work.Assembly(HeatSinkSlot.HeatSink2).BoltPresenceResults.Count);
        Assert.Equal(
            AssemblyResult.Ok,
            work.Assembly(HeatSinkSlot.HeatSink2).InspectionResult);
        io.SetInput(InputIo.InspectionHeatSink1Present, false);
        io.SetInput(InputIo.InspectionHeatSink2Present, false);
        Assert.True(work.Completed);
        Assert.False(work.HasNg);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.InspectionHeatSink2Present, true);
        Assert.True(work.Completed);

        io.SetInput(InputIo.InspectionCarrierPresent, false);
        io.SetInput(InputIo.InspectionCarrierPresent, true);
        Assert.True(await WaitUntilAsync(
            () => work.Completed,
            TimeSpan.FromSeconds(2)));

        var heatSink1 = work.Assemblies.Single(
            assembly => assembly.HeatSink == HeatSinkSlot.HeatSink1);
        var heatSink2 = work.Assemblies.Single(
            assembly => assembly.HeatSink == HeatSinkSlot.HeatSink2);
        Assert.Equal(AssemblyResult.Ng, heatSink1.InspectionResult);
        Assert.True(heatSink1.BoltPresenceResults[1]);
        Assert.False(heatSink1.BoltPresenceResults[2]);
        Assert.Equal(AssemblyResult.Ok, heatSink2.InspectionResult);
        Assert.All(heatSink2.BoltPresenceResults.Values, Assert.True);

        io.SetInput(InputIo.InspectionCarrierPresent, false);
        io.SetInput(InputIo.InspectionHeatSink1Present, false);
        io.SetInput(InputIo.InspectionHeatSink2Present, false);
        io.SetInput(InputIo.InspectionCarrierPresent, true);

        Assert.True(await WaitUntilAsync(
            () => work.Completed,
            TimeSpan.FromSeconds(2)));
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
        Assert.Contains(inspections, image => image.BoltNumber == 2
            && image.HeatSink == HeatSinkSlot.HeatSink1 && !image.Present);
        Assert.All(inspections, image =>
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
            ConveyorStation.Inspection(io), transferFeedback, isEnabled: () => false);
        var transferStation = new InspectionStation(
            transferWork,
            inspector,
            transfer,
            new NgCarrierMove(transferWork, shuttle, transfer, gantry, transferSettings),
            shuttle,
            isTransferEnabled: () => true);
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

        Assert.Equal(
            InspectionStationState.WaitingForShuttleCarrier,
            transferStation.State([]));
        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        Assert.Equal(
            InspectionStationState.RaisingCarrierTransfer,
            transferStation.State([]));
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
        return new BoltTarget(new BoltPoint
        {
            Number = number,
            X = position.X,
            Y = position.Y,
        }, heatSink, new PcbLayout { Origins = new() { [heatSink] = new() } });
    }

    private sealed class CountingSegmenter : IBoltRecessSegmenter
    {
        private readonly VirtualBoltRecessSegmenter _inner = new();
        public int Calls { get; private set; }
        public void CheckReady() => _inner.CheckReady();
        public void Reload() => _inner.Reload();
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
        public Action? AfterCapture { get; set; }
        public (int Width, int Height) FrameSize => camera.FrameSize;

        public event Action<ImageFrame>? FrameReady
        {
            add { }
            remove { }
        }

        public event Action<Exception>? LiveViewFailed
        {
            add { }
            remove { }
        }

        public void Initialize() => camera.Initialize();

        public ImageFrame Capture(double exposureMicroseconds, double gain)
        {
            var image = camera.Capture(exposureMicroseconds, gain);
            var current = position();
            var missing = Math.Abs(current.X - missingPosition.X)
                              <= MotionService.PositionToleranceMillimeters
                          && Math.Abs(current.Y - missingPosition.Y)
                              <= MotionService.PositionToleranceMillimeters;
            var captured = missing
                ? image with
                {
                    Pixels = Enumerable.Repeat(
                        (byte)30,
                        image.Stride * image.Height).ToArray(),
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
