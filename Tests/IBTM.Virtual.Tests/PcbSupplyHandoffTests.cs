using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbSupply;
using IBTM.Storage;
using IBTM.Virtual;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTest;

namespace IBTM.Virtual.Tests;

public sealed class PcbSupplyHandoffTests
{
    [Fact]
    public async Task CarrierLeavingDuringSecondPickupResetsTheNextCarrierToPcbOne()
    {
        using var rig = new HandoffRig();
        rig.Io.Initialize();
        rig.Motion.Initialize();
        await HomeAsync(rig.Motion, 2_000);
        rig.Io.SetInput(InputIo.AutoMode, false);
        rig.Io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
        var recipe = new PcbSupplyRecipe
        {
            Pcb1PickPosition = new() { X = 10, Y = 10, Z = 8 },
            Pcb2PickPosition = new() { X = 20, Y = 10, Z = 8 },
        };
        rig.Recipes.Current.PcbSupply = recipe;
        var departed = false;
        var replacement = false;
        var approachingReplacement = false;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var picked = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        rig.Supplier.StepChanged += () =>
        {
            var position = rig.Motion.Position;
            if (!departed && rig.Supplier.Step is PcbSupplyState.PickingPcb
                && position.X == 20 && position.Z == 8)
            {
                departed = true;
                rig.Io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
            }
            if (departed && !replacement && rig.Supplier.Step is PcbSupplyState.WaitingForCarrier)
                waiting.TrySetResult();
            if (replacement && rig.Supplier.Step is PcbSupplyState.MovingToPickup)
                approachingReplacement = true;
            if (approachingReplacement && rig.Supplier.Step is PcbSupplyState.PickingPcb)
            {
                picked.TrySetResult(position.X);
                stop.Cancel();
            }
        };
        var run = rig.Supplier.RunAsync(rig.Placement, stop.Token);
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
            replacement = true;
            rig.Io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
            Assert.Equal(recipe.Pcb1PickPosition.X, await picked.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task DisabledRunDoesNotReplaceTheSupplyHandoffPhase()
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        var phase = rig.Supplier.Phase;
        rig.Units.PcbSupply = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        rig.Supplier.Trace += message =>
        {
            if (message.StartsWith("PcbSupplier: Disabled "))
            {
                Assert.Equal(PcbSupplyState.Disabled, rig.Supplier.Step);
                stop.Cancel();
            }
        };

        await rig.Supplier.RunAsync(rig.Placement, stop.Token);

        Assert.Null(rig.Supplier.Step);
        rig.Units.PcbSupply = true;
        Assert.Equal(phase, rig.Supplier.Phase);
        Assert.Equal(phase, rig.Supplier.GetNextStep(rig.Placement));
        Assert.Equal(PcbSupplyHandoff.Released, rig.Supplier.Handoff);
    }

    [Fact]
    public async Task RepeatForwardHoldingLossWhileWaitingDoesNotBecomeReturnReceipt()
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var run = rig.Supplier.RunAsync(rig.Placement, stop.Token, repeat: true);
        try
        {
            Assert.Equal(PcbSupplyState.HandingOff, rig.Supplier.Step);
            rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, false);
            await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.True(rig.Io.GetOutput(OutputIo.PcbSupplyGripperClosed));
            Assert.False(rig.Motion.IsMoving);
        }
        finally
        {
            stop.Cancel();
            if (!run.IsFaulted)
                await run;
        }
    }

    [Fact]
    public async Task ReadingUnavailableHandoffDoesNotChangeItsCompletedPhase()
    {
        using var rig = new HandoffRig(probeFeedback: true);
        await rig.InitializeAsync();
        var phase = rig.Supplier.Phase;
        var changes = 0;
        rig.Supplier.Changed += () => changes++;

        // A Watch evaluation observes current feedback; it cannot commit a transition.
        rig.FeedbackProbe!.OverrideState = state => state with { InPosition = false };
        Assert.Equal(PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);
        Assert.Equal(PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);
        rig.FeedbackProbe.OverrideState = null;

        Assert.Equal(PcbSupplyHandoff.Released, rig.Supplier.Handoff);
        Assert.Equal(phase, rig.Supplier.Phase);
        Assert.Null(rig.Supplier.Step);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task ServoLossInvalidatesHandoffUntilItsStageRunsAgain()
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        Assert.Equal(PcbSupplyHandoff.Released, rig.Supplier.Handoff);

        rig.Motion.SetServo(MotionAxis.X, false);
        Assert.Equal(PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);
        rig.Motion.SetServo(MotionAxis.X, true);
        Assert.True(MotionService.IsAt(rig.Supplier.Motion.Feedback, rig.Settings.HandoffPosition));
        Assert.Equal(PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);

        await rig.Supplier.PrepareHandoffAsync(CancellationToken.None);
        Assert.Equal(PcbSupplyHandoff.Released, rig.Supplier.Handoff);
    }

    [Fact]
    public async Task MatchingHandoffCoordinatesDoesNotStartTheHandoffStage()
    {
        using var rig = new HandoffRig();
        rig.Io.Initialize();
        rig.Motion.Initialize();
        await HomeAsync(rig.Motion, 2_000);
        await rig.Handler.SetRotatedAsync(false);
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
        rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        var target = rig.Settings.HandoffPosition;
        await rig.Motion.MoveToXYAsync(target.X, target.Y, rig.Settings.Motion.HorizontalSpeed);
        await rig.Motion.MoveAxisAsync(MotionAxis.Z, target.Z, rig.Settings.Motion.ZSpeed);

        Assert.True(MotionService.IsAt(rig.Handler.Motion.Feedback, rig.Settings.HandoffPosition));
        Assert.True(rig.Handler.PcbSecured);
        Assert.Equal(PcbSupplyState.MovingToPickup, rig.Supplier.Phase);
        Assert.Equal(PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);

        await rig.Handler.PrepareHandoffAsync(CancellationToken.None);
        Assert.Equal(PcbSupplyHandoff.Holding, rig.Supplier.Handoff);
        await rig.Motion.MoveAxisAsync(MotionAxis.X, target.X + 10, 2_000);
        Assert.Equal(PcbSupplyState.HandingOff, rig.Supplier.Phase);
        Assert.Equal(PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task HandoffRequiresUnrotatedFeedback(bool rotated, bool unrotated)
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        rig.Io.SetInputs((InputIo.PcbSupplyRotated, rotated), (InputIo.PcbSupplyUnrotated, unrotated));
        var valid = !rotated && unrotated;
        Assert.Equal(valid ? PcbSupplyHandoff.Released : PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);
        rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
        Assert.Equal(valid ? PcbSupplyHandoff.Holding : PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);
    }

    [Fact]
    public async Task NormalHandoffRejectsWrongRotationWithoutMovingOrReleasing()
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
        rig.Io.SetInputs((InputIo.PcbSupplyRotated, true), (InputIo.PcbSupplyUnrotated, false));
        rig.Placement.Handoff = PcbPlacementHandoff.Holding;
        var commanded = false;
        rig.Motion.MovingChanged += moving => commanded |= moving;
        rig.Io.OutputChanged += (output, on) => commanded |= output is OutputIo.PcbSupplyRotate
            or OutputIo.PcbSupplyGripperClosed or OutputIo.PcbSupplyIpmFixerForward;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<MotionInterlockException>(
            () => rig.Supplier.RunAsync(rig.Placement, timeout.Token));

        Assert.False(commanded);
        Assert.True(rig.Handler.PcbSecured);
        Assert.True(MotionService.IsAt(rig.Handler.Motion.Feedback, rig.Settings.HandoffPosition));
    }

    [Fact]
    public async Task RotationFeedbackLossDuringReleaseKeepsTheGripperClosed()
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
        rig.Placement.Handoff = PcbPlacementHandoff.Holding;
        rig.Io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbSupplyIpmFixerForward && !on)
                rig.Io.SetInput(InputIo.PcbSupplyRotated, true);
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<MotionInterlockException>(
            () => rig.Supplier.RunAsync(rig.Placement, timeout.Token));

        Assert.True(rig.Io.GetOutput(OutputIo.PcbSupplyGripperClosed));
        Assert.Equal(PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);
        Assert.True(MotionService.IsAt(rig.Handler.Motion.Feedback, rig.Settings.HandoffPosition));
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
                Assert.Equal(rig.Settings.RotationZ, rig.Motion.Position.Z);
                rotationStarted.TrySetResult();
            }
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var run = rig.Supplier.RunAsync(rig.Placement, stop.Token, repeat: true);
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
                Assert.True(MotionService.IsAt(rig.Handler.Motion.Feedback, rig.Settings.HandoffPosition));
            }
            else
            {
                await Assert.ThrowsAsync<IoTimeoutException>(() => run);
                Assert.Equal(PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);
                Assert.Equal(rig.Settings.RotationZ, rig.Motion.Position.Z);
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
            () => rig.Supplier.RunAsync(rig.Placement, timeout.Token, repeat: true));

        Assert.False(commanded);
        Assert.True(MotionService.IsAt(rig.Handler.Motion.Feedback, rig.Settings.HandoffPosition));
    }

    private sealed class HandoffRig : IDisposable
    {
        public HandoffRig(bool probeFeedback = false)
        {
            Settings = new()
            {
                Motion = new() { HorizontalSpeed = 2_000, ZSpeed = 2_000 },
                RotationZ = 3,
                HandoffPosition = new() { X = 50, Y = 10, Z = 7 },
            };
            Io = new(new PcbSupplyHardwareSettings().Outputs, new MachineOptions { TimeoutMilliseconds = 500 });
            Motion = new(Settings.Motion, new());
            Units = new();

            IMotionFeedback feedback = Motion;
            if (probeFeedback)
            {
                feedback = System.Reflection.DispatchProxy.Create<IXyMotion, MachineLifecycleTests.ScopedMotionProbe>();
                FeedbackProbe = (MachineLifecycleTests.ScopedMotionProbe)feedback;
                FeedbackProbe.Motion = Motion;
                FeedbackProbe.ReportReady = true;
            }
            Recipes = new(OpenMachineStore(), new());
            Supplier = new PcbSupplier((IXyMotion)feedback, new MotionStatus(feedback),
                Io,
                Settings,
                Recipes,
                Units);
            Handler = Supplier;
            Placement = new();
        }

        public RecipeManager Recipes { get; }
        public PcbSupplySettings Settings { get; }
        public UnitSettings Units { get; }
        public VirtualIoService Io { get; }
        public VirtualMotionService Motion { get; }
        public PcbSupplier Handler { get; }
        public PcbSupplier Supplier { get; }
        public MachineLifecycleTests.ScopedMotionProbe? FeedbackProbe { get; }
        public PlacementFeedback Placement { get; }

        public async Task InitializeAsync()
        {
            Io.Initialize();
            Motion.Initialize();
            await HomeAsync(Motion, 2_000);
            await Handler.SetRotatedAsync(false);
            await ((IIoService)Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, false);
            await ((IIoService)Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, false);
            await Handler.PrepareHandoffAsync(CancellationToken.None);
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
