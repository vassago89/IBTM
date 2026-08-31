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

namespace IBTM.Virtual.Tests;

public sealed class InspectionTests
{
    [Fact]
    public void CarrierCoordinatesUseInspectionAxesWithoutScaling()
    {
        var sourceUpperLeft = new AxisPos { X = 10, Y = 20 };
        var sourceLowerRight = new AxisPos { X = 110, Y = 20 };
        var carrier = CarrierCoordinates.FromMachine(
            new AxisPos { X = 25, Y = 37 },
            sourceUpperLeft);

        Assert.Equal(15, carrier.X);
        Assert.Equal(17, carrier.Y);

        var target = CarrierCoordinates.ToMachine(
            carrier,
            sourceUpperLeft,
            sourceLowerRight,
            new AxisPos { X = 200, Y = 300 },
            new AxisPos { X = 200, Y = 500 });

        Assert.Equal(183, target.X);
        Assert.Equal(315, target.Y);
    }

    [Fact]
    public async Task InspectionRestartsAndRejectsMissingOrEmptyWork()
    {
        var io = new VirtualIoService(
            new NgCarrierTransferHardwareSettings().Outputs,
            new MachineOptions());
        var work = new InspectionWork(
            io,
            new TestInspectionGantryClearance(io));
        var operations = new OperationCancellation();
        var carrierReference = new CarrierReferenceSettings
        {
            UpperLeftPin = new AxisPos { X = 2, Y = 2 },
            LowerRightPin = new AxisPos { X = 38, Y = 28 },
        };
        var gantry = new InspectionGantrySettings
        {
            Motion = new MotionSettings { HorizontalSpeed = 200 },
        };
        using var motion = new VirtualMotionService(
            gantry.Motion,
            operations,
            hasZ: false,
            xRange: (0, 40),
            yRange: (0, 30));
        BoltPoint[] bolts =
        [
            Bolt(1, HeatSinkSlot.HeatSink1, 9, 9, carrierReference),
            Bolt(2, HeatSinkSlot.HeatSink1, 9, 21, carrierReference),
            Bolt(3, HeatSinkSlot.HeatSink2, 31, 9, carrierReference),
            Bolt(4, HeatSinkSlot.HeatSink2, 31, 21, carrierReference),
        ];
        var camera = new SequenceCamera(
            new VirtualCamera(
                motion.GetPosition,
                () => bolts.Select(
                    bolt => gantry.GetBoltPosition(
                        bolt,
                        carrierReference))),
            missingCapture: 3);
        var inspectionSettings = new BoltInspectionSettings();
        var light = new TestLightController();
        var imageCapture = new BoltImageCapture(
            motion,
            camera,
            light,
            gantry,
            carrierReference,
            new LightingSettings());
        var process = new InspectionProcess(
            work,
            imageCapture,
            new BoltPresenceInspector(
                inspectionSettings,
                new VirtualBoltRecessSegmenter()));
        io.Initialize();
        Assert.True(work.CanReceive);
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        Assert.False(work.CanReceive);
        io.SetInput(InputIo.NgCarrierPickupDown, false);
        io.SetInput(InputIo.NgCarrierPickupUp, true);
        motion.Initialize();
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.InspectionHeatSink2Present, true);
        io.SetInput(InputIo.InspectionBackupPlateUp, true);
        io.SetInput(InputIo.InspectionStopperDown, true);
        io.SetInput(InputIo.InspectionCarrierPresent, true);
        using var moveStop = new CancellationTokenSource();
        void StopDuringMove(double x, double y, double _)
        {
            if (x > 0 || y > 0)
            {
                moveStop.Cancel();
            }
        }

        motion.PositionChanged += StopDuringMove;
        await process.RunAsync(bolts, moveStop.Token);
        motion.PositionChanged -= StopDuringMove;

        Assert.False(work.Completed);
        Assert.Empty(work.Assemblies);
        Assert.Equal(0, camera.CaptureCount);
        using var firstStop = new CancellationTokenSource();
        camera.Captured = count =>
        {
            if (count == 1)
            {
                firstStop.Cancel();
            }
        };
        await process.RunAsync(bolts, firstStop.Token);

        Assert.False(work.Completed);
        Assert.Single(work.Assembly(HeatSinkSlot.HeatSink1)
            .BoltPresenceResults);

        camera.Captured = null;
        using var cancellation = new CancellationTokenSource();
        var run = process.RunAsync(bolts, cancellation.Token);

        var completed = await WaitUntilAsync(
            () => work.Completed,
            TimeSpan.FromSeconds(2));

        Assert.True(completed);
        Assert.True(work.HasNg);
        Assert.Equal(InspectionState.WaitingForTransfer, work.State);
        Assert.Equal(5, camera.CaptureCount);
        var heatSink1 = work.Assemblies.Single(
            assembly => assembly.HeatSink == HeatSinkSlot.HeatSink1);
        var heatSink2 = work.Assemblies.Single(
            assembly => assembly.HeatSink == HeatSinkSlot.HeatSink2);
        Assert.Equal(PcbResult.Ng, heatSink1.InspectionResult);
        Assert.True(heatSink1.BoltPresenceResults[1]);
        Assert.False(heatSink1.BoltPresenceResults[2]);
        Assert.Equal(PcbResult.Ok, heatSink2.InspectionResult);
        Assert.True(heatSink2.BoltPresenceResults[3]);
        Assert.True(heatSink2.BoltPresenceResults[4]);
        Assert.Equal(5, light.TurnOnCount);
        Assert.Equal(5, light.TurnOffCount);
        Assert.False(light.IsOn);

        io.SetInput(InputIo.InspectionCarrierPresent, false);
        io.SetInput(InputIo.InspectionHeatSink1Present, false);
        io.SetInput(InputIo.InspectionHeatSink2Present, true);
        io.SetInput(InputIo.InspectionCarrierPresent, true);

        Assert.True(await WaitUntilAsync(
            () => work.Completed,
            TimeSpan.FromSeconds(2)));
        Assert.False(work.HasNg);
        Assert.Single(work.Assemblies);
        Assert.Equal(HeatSinkSlot.HeatSink2, work.Assemblies[0].HeatSink);
        Assert.Equal(7, camera.CaptureCount);

        io.SetInput(InputIo.InspectionCarrierPresent, false);
        io.SetInput(InputIo.InspectionHeatSink2Present, false);
        io.SetInput(InputIo.InspectionCarrierPresent, true);

        Assert.True(await WaitUntilAsync(
            () => work.Completed,
            TimeSpan.FromSeconds(2)));
        Assert.True(work.HasNg);
        Assert.Empty(work.Assemblies);
        Assert.Equal(7, camera.CaptureCount);
        Assert.Equal(7, light.TurnOnCount);
        Assert.Equal(7, light.TurnOffCount);
        Assert.False(light.IsOn);

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
            new AxisPos { X = x, Y = y },
            reference.UpperLeftPin!);
        return new BoltPoint
        {
            Number = number,
            HeatSink = heatSink,
            X = position.X,
            Y = position.Y,
        };
    }

    private static async Task<bool> WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        var started = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - started >= timeout)
            {
                return false;
            }

            await Task.Delay(10);
        }

        return true;
    }

    private sealed class SequenceCamera(
        ICamera camera,
        int missingCapture) : ICamera
    {
        public event Action<ImageFrame>? FrameReady
        {
            add { }
            remove { }
        }

        public int CaptureCount { get; private set; }
        public Action<int>? Captured { get; set; }

        public void Initialize() => camera.Initialize();

        public ImageFrame Capture()
        {
            CaptureCount++;
            var image = camera.Capture();
            Captured?.Invoke(CaptureCount);
            return CaptureCount == missingCapture
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

    private sealed class TestLightController : ILightController
    {
        public int TurnOnCount { get; private set; }
        public int TurnOffCount { get; private set; }
        public bool IsOn { get; private set; }

        public void Initialize()
        {
        }

        public void SetLevel(int channel, int level)
        {
        }

        public void TurnOn(int channel)
        {
            TurnOnCount++;
            IsOn = true;
        }

        public void TurnOff(int channel)
        {
            TurnOffCount++;
            IsOn = false;
        }

        public void TurnOffAll() => IsOn = false;
    }
}
