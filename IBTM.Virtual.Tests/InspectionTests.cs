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
    public async Task InspectionRestartsAndRejectsMissingBoltAndEmptyCarrier()
    {
        var io = new VirtualIoService(
            new NgCarrierTransferHardwareSettings().Outputs,
            new MachineOptions());
        var work = new InspectionWork(
            ConveyorStation.Inspection(io),
            new TestInspectionGantryClearance(io));
        var carrierReference = new CarrierReferenceSettings
        {
            UpperLeftLocatingPin = new AxisPosition { X = 2, Y = 2 },
            LowerRightLocatingPin = new AxisPosition { X = 38, Y = 28 },
        };
        var gantrySettings = new InspectionGantrySettings
        {
            Motion = new MotionSettings { HorizontalSpeed = 200 },
        };
        using var motion = new VirtualMotionService(
            gantrySettings.Motion,
            new OperationCancellation(),
            hasZ: false,
            xRange: (0, 40),
            yRange: (0, 30));
        var gantry = new InspectionGantry(motion);
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
        var process = new InspectionProcess(
            work,
            new BoltInspector(
                gantry,
                camera,
                new InspectionCameraSettings(),
                new VirtualLightController(),
                new BoltPresenceDetector(
                    new BoltInspectionSettings(),
                    new VirtualBoltRecessSegmenter()),
                gantrySettings,
                carrierReference,
                new LightingSettings()));

        io.Initialize();
        motion.Initialize();
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.InspectionHeatSink2Present, true);
        io.SetInput(InputIo.InspectionBackupPlateUp, true);
        io.SetInput(InputIo.InspectionStopperDown, true);
        io.SetInput(InputIo.InspectionCarrierPresent, true);
        using var firstStop = new CancellationTokenSource();
        var firstRun = process.RunAsync(bolts, firstStop.Token);
        Assert.True(await WaitUntilAsync(
            () => work.Assembly(HeatSinkSlot.HeatSink1)
                .BoltPresenceResults.Count == 1,
            TimeSpan.FromSeconds(2)));
        firstStop.Cancel();
        await firstRun;

        Assert.False(work.Completed);
        Assert.Single(work.Assembly(HeatSinkSlot.HeatSink1)
            .BoltPresenceResults);

        using var cancellation = new CancellationTokenSource();
        var run = process.RunAsync(bolts, cancellation.Token);
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

        cancellation.Cancel();
        await run;
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

    private sealed class MissingBoltCamera(
        ICamera camera,
        Func<(double X, double Y, double Z)> position,
        AxisPosition missingPosition) : ICamera
    {
        public event Action<ImageFrame>? FrameReady
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
            return missing
                ? image with
                {
                    Pixels = Enumerable.Repeat(
                        (byte)30,
                        image.Stride * image.Height).ToArray(),
                }
                : image;
        }

        public void StartLiveView()
        {
        }

        public void StopLiveView()
        {
        }
    }
}
