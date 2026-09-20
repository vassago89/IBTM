using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbSupply;
using IBTM.Virtual;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTest;

namespace IBTM.Virtual.Tests;

public sealed class PcbSupplyHandoffTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task HandoffRequiresUnrotatedFeedbackInNormalAndRepeat(bool rotated, bool unrotated)
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        rig.Io.SetInputs((InputIo.PcbSupplyRotated, rotated), (InputIo.PcbSupplyUnrotated, unrotated));
        var valid = !rotated && unrotated;
        foreach (var repeat in new[] { false, true })
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var run = repeat ? rig.Supplier.RunAsync(new(), rig.Placement, stop.Token, repeat: true) : Task.CompletedTask;
            try
            {
                Assert.Equal(valid ? PcbSupplyHandoff.Released : PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);
                rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, true);
                await rig.Handler.SetGripperClosedAsync(true);
                await rig.Handler.SetIpmFixerAsync(true);
                Assert.Equal(valid ? PcbSupplyHandoff.Holding : PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);
                await rig.Handler.SetIpmFixerAsync(false);
                await rig.Handler.SetGripperClosedAsync(false);
                rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, false);
            }
            finally
            {
                stop.Cancel();
                await run.WaitAsync(TimeSpan.FromSeconds(1));
            }
        }
    }

    [Fact]
    public async Task NormalHandoffRejectsWrongRotationWithoutMovingOrReleasing()
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await rig.Handler.SetGripperClosedAsync(true);
        await rig.Handler.SetIpmFixerAsync(true);
        rig.Io.SetInputs((InputIo.PcbSupplyRotated, true), (InputIo.PcbSupplyUnrotated, false));
        rig.Placement.Handoff = PcbPlacementHandoff.Holding;
        var commanded = false;
        rig.Motion.MovingChanged += moving => commanded |= moving;
        rig.Io.OutputChanged += (output, on) => commanded |= output is OutputIo.PcbSupplyRotate
            or OutputIo.PcbSupplyGripperClosed or OutputIo.PcbSupplyIpmFixerForward;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<MotionInterlockException>(
            () => rig.Supplier.RunAsync(new(), rig.Placement, timeout.Token));

        Assert.False(commanded);
        Assert.True(rig.Handler.PcbSecured);
        Assert.True(rig.Handler.IsAtHandoff());
    }

    [Fact]
    public async Task RotationFeedbackLossDuringReleaseKeepsTheGripperClosed()
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await rig.Handler.SetGripperClosedAsync(true);
        await rig.Handler.SetIpmFixerAsync(true);
        rig.Placement.Handoff = PcbPlacementHandoff.Holding;
        rig.Io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbSupplyIpmFixerForward && !on)
                rig.Io.SetInput(InputIo.PcbSupplyRotated, true);
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<MotionInterlockException>(
            () => rig.Supplier.RunAsync(new(), rig.Placement, timeout.Token));

        Assert.True(rig.Io.GetOutput(OutputIo.PcbSupplyGripperClosed));
        Assert.Equal(PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);
        Assert.True(rig.Handler.IsAtHandoff());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepeatPreparesAndConfirmsRotationAtExistingHandoff(bool feedbackArrives)
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyRotate, true);
        rig.Io.AutoResponseEnabled = false;
        rig.Placement.ReturningPcb = HeatSinkSlot.HeatSink1;
        var rotationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbSupplyRotate && !on)
            {
                Assert.Equal(rig.Settings.RotationZ, rig.Motion.GetPosition().Z);
                rotationStarted.TrySetResult();
            }
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var run = rig.Supplier.RunAsync(new(), rig.Placement, stop.Token, repeat: true);
        try
        {
            await rotationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);
            Assert.False(run.IsCompleted);
            if (feedbackArrives)
            {
                rig.Io.SetInputs((InputIo.PcbSupplyRotated, false), (InputIo.PcbSupplyUnrotated, true));
                Assert.True(await WaitUntilAsync(
                    () => rig.Supplier.Handoff == PcbSupplyHandoff.Released, TimeSpan.FromSeconds(1)));
                Assert.True(rig.Handler.IsAtHandoff());
            }
            else
            {
                await Assert.ThrowsAsync<IoTimeoutException>(() => run);
                Assert.Equal(PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);
                Assert.Equal(rig.Settings.RotationZ, rig.Motion.GetPosition().Z);
            }
        }
        finally
        {
            stop.Cancel();
            // Observe a device timeout in the assertion above without rethrowing it in cleanup.
            if (!run.IsFaulted)
                await run.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task RepeatDoesNotRotateWhilePlacementIsReturningThePcb()
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        rig.Io.SetInputs((InputIo.PcbSupplyRotated, true), (InputIo.PcbSupplyUnrotated, true));
        rig.Placement.ReturningPcb = HeatSinkSlot.HeatSink1;
        rig.Placement.Handoff = PcbPlacementHandoff.Returning;
        var commanded = false;
        rig.Motion.MovingChanged += moving => commanded |= moving;
        rig.Io.OutputChanged += (output, on) => commanded |= output is OutputIo.PcbSupplyRotate
            or OutputIo.PcbSupplyGripperClosed or OutputIo.PcbSupplyIpmFixerForward;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<MotionInterlockException>(
            () => rig.Supplier.RunAsync(new(), rig.Placement, timeout.Token, repeat: true));

        Assert.False(commanded);
        Assert.True(rig.Handler.IsAtHandoff());
    }

    private sealed class HandoffRig : IDisposable
    {
        public HandoffRig()
        {
            Settings = new()
            {
                Motion = new() { HorizontalSpeed = 2_000, ZSpeed = 2_000 },
                RotationZ = 3,
                HandoffPosition = new() { X = 50, Y = 10, Z = 7 },
            };
            Io = new(new PcbSupplyHardwareSettings().Outputs, new MachineOptions { TimeoutMilliseconds = 500 });
            Motion = new(Settings.Motion, new(), horizontalZ: () => Settings.RotationZ);
            Handler = new(Motion, Io, Settings);
            Supplier = new(Handler, new());
            Placement = new();
        }

        public PcbSupplySettings Settings { get; }
        public VirtualIoService Io { get; }
        public VirtualMotionService Motion { get; }
        public PcbSupplyHandler Handler { get; }
        public PcbSupplier Supplier { get; }
        public PlacementFeedback Placement { get; }

        public async Task InitializeAsync()
        {
            Io.Initialize();
            Motion.Initialize();
            await HomeAsync(Motion, 2_000);
            await Handler.SetRotatedAsync(false);
            await Handler.SetGripperClosedAsync(false);
            await Handler.SetIpmFixerAsync(false);
            await Handler.MoveToHandoffAsync(CancellationToken.None);
        }

        public void Dispose()
        {
            Motion.Dispose();
        }
    }

    private sealed class PlacementFeedback : IPcbPlacementHandoff
    {
        public event Action? Changed { add { } remove { } }
        public PcbPlacementHandoff Handoff { get; set; }
        public HeatSinkSlot? ReturningPcb { get; set; }
    }
}
