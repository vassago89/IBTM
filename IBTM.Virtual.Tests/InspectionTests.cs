using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.Virtual;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class InspectionTests
{
    [Fact]
    public async Task InspectionContinuesAfterMissingBoltAndRejectsEmptyCarrier()
    {
        var io = new VirtualIoService(
            new Dictionary<OutputIo, OutputHardware>(),
            new MachineOptions());
        var work = new InspectionWork(io);
        var operations = new OperationCancellation();
        var gantry = new InspectionGantrySettings
        {
            Motion = new MotionSettings { HorizontalSpeed = 2_000 },
            UpperLeftLocatingPin = new AxisPos { X = 2, Y = 2 },
            LowerRightLocatingPin = new AxisPos { X = 38, Y = 28 },
        };
        using var motion = new VirtualMotionService(
            gantry.Motion,
            operations,
            hasZ: false,
            xRange: (0, 40),
            yRange: (0, 30));
        var camera = new SequenceCamera(
            new VirtualCamera(motion.GetPosition),
            missingCapture: 2);
        var inspectionSettings = new BoltInspectionSettings();
        var imageCapture = new BoltImageCapture(
            motion,
            camera,
            new VirtualLightController(),
            gantry,
            new LightingSettings());
        var process = new InspectionProcess(
            work,
            imageCapture,
            new BoltPresenceInspector(
                inspectionSettings,
                new VirtualBoltRecessSegmenter()));
        BoltPoint[] bolts =
        [
            Bolt(1, HousingSlot.Housing1, 12, 11, gantry),
            Bolt(2, HousingSlot.Housing1, 12, 19, gantry),
            Bolt(3, HousingSlot.Housing2, 28, 11, gantry),
            Bolt(4, HousingSlot.Housing2, 28, 19, gantry),
        ];

        io.Initialize();
        motion.Initialize();
        io.SetInput(InputIo.InspectionHousing1Present, true);
        io.SetInput(InputIo.InspectionHousing2Present, true);
        io.SetInput(InputIo.InspectionBackupPlateUp, true);
        io.SetInput(InputIo.InspectionStopperDown, true);
        io.SetInput(InputIo.InspectionCarrierJigPresent, true);
        using var cancellation = new CancellationTokenSource();
        var run = process.RunAsync(bolts, cancellation.Token);

        var completed = await WaitUntilAsync(
            () => work.Completed,
            TimeSpan.FromSeconds(2));

        Assert.True(completed);
        Assert.True(work.HasNg);
        Assert.Equal(InspectionState.WaitingForTransfer, work.State);
        Assert.Equal(4, camera.CaptureCount);
        var housing1 = work.Assemblies.Single(
            assembly => assembly.Housing == HousingSlot.Housing1);
        var housing2 = work.Assemblies.Single(
            assembly => assembly.Housing == HousingSlot.Housing2);
        Assert.Equal(PcbResult.Ng, housing1.InspectionResult);
        Assert.True(housing1.BoltPresenceResults[1]);
        Assert.False(housing1.BoltPresenceResults[2]);
        Assert.Equal(PcbResult.Ok, housing2.InspectionResult);
        Assert.True(housing2.BoltPresenceResults[3]);
        Assert.True(housing2.BoltPresenceResults[4]);

        io.SetInput(InputIo.InspectionCarrierJigPresent, false);
        io.SetInput(InputIo.InspectionHousing1Present, false);
        io.SetInput(InputIo.InspectionHousing2Present, false);
        io.SetInput(InputIo.InspectionCarrierJigPresent, true);

        Assert.True(await WaitUntilAsync(
            () => work.Completed,
            TimeSpan.FromSeconds(2)));
        Assert.True(work.HasNg);
        Assert.Empty(work.Assemblies);
        Assert.Equal(4, camera.CaptureCount);

        cancellation.Cancel();
        await run;
    }

    private static BoltPoint Bolt(
        int number,
        HousingSlot housing,
        double x,
        double y,
        InspectionGantrySettings settings)
    {
        var position = CarrierCoordinates.FromMachine(
            new AxisPos { X = x, Y = y },
            settings.UpperLeftLocatingPin!,
            settings.LowerRightLocatingPin!);
        return new BoltPoint
        {
            Number = number,
            Housing = housing,
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

        public void Initialize() => camera.Initialize();

        public ImageFrame Capture()
        {
            CaptureCount++;
            var image = camera.Capture();
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
}
