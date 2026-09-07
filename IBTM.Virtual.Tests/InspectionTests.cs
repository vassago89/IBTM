using System;
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
        BoltPoint[] bolts =
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
                        carrierReference))),
            motion.GetPosition,
            gantrySettings.GetBoltPosition(bolts[1], carrierReference));
        var segmenter = new CountingSegmenter();
        var inspector = new BoltInspector(
            gantry,
            camera,
            new InspectionCameraSettings(),
            new VirtualLightController(),
            new BoltPresenceDetector(
                new BoltInspectionSettings(),
                segmenter),
            gantrySettings,
            carrierReference,
            new LightingSettings());
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
            gantry,
            transferSettings,
            shuttle,
            isTransferEnabled: () => false);

        io.Initialize();
        motion.Initialize();
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.InspectionHeatSink2Present, true);
        io.SetInput(InputIo.InspectionBackupPlateUp, true);
        io.SetInput(InputIo.InspectionStopperDown, true);
        io.SetInput(InputIo.InspectionCarrierPresent, true);
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
        Assert.Empty(work.Assemblies);
        Assert.False(work.Completed);

        var position = motion.GetPosition();
        transferSettings.ShuttlePlacePosition = new AxisPosition
        {
            X = position.X,
            Y = position.Y,
        };
        var transferStation = new InspectionStation(
            new InspectionWork(
                ConveyorStation.Inspection(io),
                transferFeedback,
                isEnabled: () => false),
            inspector,
            transfer,
            gantry,
            transferSettings,
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

    private static BoltPoint Bolt(
        int number,
        HeatSinkSlot heatSink,
        double x,
        double y,
        CarrierReferenceSettings reference)
    {
        var position = CarrierCoordinates.FromMachine(
            new AxisPosition { X = x, Y = y },
            reference.UpperLeftLocatingPin!);
        return new BoltPoint
        {
            Number = number,
            HeatSink = heatSink,
            X = position.X,
            Y = position.Y,
        };
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

        public ImageFrame Capture()
        {
            var image = camera.Capture();
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

        public void StartLiveView()
        {
        }

        public void StopLiveView()
        {
        }
    }
}
