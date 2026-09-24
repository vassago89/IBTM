using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbPlacement;
using IBTM.Storage;
using IBTM.Virtual;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTest;

namespace IBTM.Virtual.Tests;

public sealed class PcbPlacementStateSafetyTests
{
    [Fact]
    public async Task DepartureClearRemainsAvailableUntilSupplyAcknowledges()
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.ReceiveAsync();
        await rig.Placer.ExecuteStepAsync(
            rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, CancellationToken.None);
        Assert.Equal(PcbPlacementHandoff.Clear, rig.Placer.Handoff);
        var position = rig.Motion.GetPosition();

        // Supply has not observed Clear yet. Placement must keep that handoff
        // available, even if its own loop runs again before Supply is scheduled.
        Assert.False(await rig.Placer.ExecuteStepAsync(
            rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, CancellationToken.None));
        Assert.Equal(position, rig.Motion.GetPosition());
        Assert.True(rig.Placer.PcbSecured);
        Assert.Equal(PcbPlacementHandoff.Clear, rig.Placer.Handoff);

        rig.Supply.Handoff = PcbSupplyHandoff.Unavailable;
        Assert.Equal(PcbPlacementState.PlacingPcb, rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledRunPreservesThePhaseForReenable(bool holdingPcb)
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        if (holdingPcb)
            await rig.ReceiveAsync();
        var phase = rig.Placer.State;
        rig.Units.PcbPlacement = false;
        var commanded = false;
        rig.Motion.MovingChanged += moving => commanded |= moving;
        rig.Io.OutputChanged += (output, value) => commanded = true;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        rig.Placer.Trace += message =>
        {
            if (message.StartsWith("PcbPlacer: Disabled "))
            {
                Assert.Equal(PcbPlacementState.Disabled, rig.Placer.Step);
                stop.Cancel();
            }
        };

        await rig.Placer.RunAsync(stop.Token);

        Assert.False(commanded);
        Assert.Null(rig.Placer.Step);
        Assert.Equal(!holdingPcb, rig.Work.Completed);
        rig.Units.PcbPlacement = true;
        Assert.Equal(phase, rig.Placer.State);
        Assert.Equal(holdingPcb ? PcbPlacementState.PreparingPlacement : PcbPlacementState.MovingToHandoff,
            rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1));
    }

    [Fact]
    public async Task SelectingNextPhaseDoesNotReleaseHandoffOrStartMotion()
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.ReceiveAsync();
        var commanded = false;
        rig.Motion.MovingChanged += moving => commanded |= moving;
        rig.Io.OutputChanged += (output, value) => commanded = true;

        var step = rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1);

        Assert.Equal(PcbPlacementState.PreparingPlacement, step);
        Assert.Equal(PcbPlacementState.WaitingForSupplyRelease, rig.Placer.State);
        Assert.Equal(PcbPlacementHandoff.Holding, rig.Placer.Handoff);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Placer.ExecuteStepAsync(
            step, HeatSinkSlot.HeatSink1, stop.Token));
        Assert.Equal(PcbPlacementState.WaitingForSupplyRelease, rig.Placer.State);
        Assert.False(commanded);
    }

    [Fact]
    public async Task AxisAlarmInvalidatesHeldPcbHandoffUntilItsStageRunsAgain()
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.ReceiveAsync();
        Assert.Equal(PcbPlacementHandoff.Holding, rig.Placer.Handoff);

        rig.Motion.SetAlarm(MotionAxis.Y, true);
        Assert.Equal(PcbPlacementHandoff.Unavailable, rig.Placer.Handoff);
        rig.Motion.SetAlarm(MotionAxis.Y, false);
        Assert.True(rig.Placer.IsAtReceivePosition());
        Assert.Equal(PcbPlacementHandoff.Unavailable, rig.Placer.Handoff);

        await rig.Placer.PrepareReceiptAsync();
        Assert.Equal(PcbPlacementHandoff.Holding, rig.Placer.Handoff);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VacuumWithoutPcbAwayFromSupportsDoesNotRestartByMovingOrReleasing(bool repeat)
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.Handler.MoveToXYAsync(new() { X = 30, Y = 20, Z = 8 });
        await rig.Handler.SetVacuumAsync(true);
        Assert.Equal(PlacementPcbState.None, rig.Handler.Pcb);
        var commanded = false;
        rig.Motion.MovingChanged += moving => commanded |= moving;
        rig.Io.OutputChanged += (output, on) => commanded = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repeat
            ? rig.Placer.RunAsync(timeout.Token, repeat: true)
            : rig.Placer.ExecuteStepAsync(rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, timeout.Token));
        Assert.False(commanded);
        Assert.True(rig.Handler.VacuumDetected);
        Assert.Empty(rig.Work.Assemblies);
    }

    [Theory]
    [InlineData(PcbPlacementState.PreparingPlacement, InputIo.PcbPlacementVacuumDetected)]
    [InlineData(PcbPlacementState.PlacingPcb, InputIo.PcbPlacementPcbDetected)]
    public async Task NormalPlacementStopsOnGripLossBeforeRelease(PcbPlacementState expected, InputIo lostInput)
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.ReceiveAsync();
        if (expected == PcbPlacementState.PlacingPcb)
        {
            await rig.Placer.ExecuteStepAsync(
                rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, CancellationToken.None);
            rig.Supply.Handoff = PcbSupplyHandoff.Unavailable;
        }
        Assert.Equal(expected == PcbPlacementState.PreparingPlacement
            ? PcbPlacementState.WaitingForSupplyRelease : PcbPlacementState.WaitingForSupplyClear, rig.Placer.State);
        var lost = false;
        var lowered = false;
        var released = false;
        rig.Motion.PositionChanged += (x, y, z) =>
        {
            if (!lost && rig.Motion.IsMoving
                && (expected == PcbPlacementState.PreparingPlacement ? z < 11.9 : x > 50.1))
            {
                lost = true;
                rig.Io.SetInput(lostInput, false);
            }
        };
        rig.Io.OutputChanged += (output, on) =>
        {
            lowered |= output == OutputIo.PcbPlacementHandlerDown && on;
            released |= output == OutputIo.PcbPlacementVacuumEjector && !on;
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Placer.ExecuteStepAsync(rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, timeout.Token));
        Assert.True(lost);
        Assert.False(rig.Motion.IsMoving);
        Assert.False(lowered);
        Assert.False(released);
        Assert.Empty(rig.Work.Assemblies);
    }

    [Fact]
    public async Task ReceiptStopsIfSupplyLosesHoldingDuringReceiveZ()
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.Handler.PrepareHandoffAsync();
        rig.Io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        rig.Supply.Handoff = PcbSupplyHandoff.Holding;
        var lost = false;
        rig.Motion.PositionChanged += (x, y, z) =>
        {
            if (!lost && rig.Motion.IsMoving && z > 8.1)
            {
                lost = true;
                rig.Supply.Handoff = PcbSupplyHandoff.Unavailable;
            }
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Placer.ExecuteStepAsync(rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, timeout.Token));
        Assert.True(lost);
        Assert.False(rig.Motion.IsMoving);
        Assert.False(rig.Io.GetOutput(OutputIo.PcbPlacementVacuumEjector));
        Assert.Empty(rig.Work.Assemblies);
    }

    [Fact]
    public async Task HeldPcbAtReceiveWithIpmUpCanLeaveAfterSupplyRelease()
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.ReceiveAsync();
        await rig.Handler.SetIpmLiftDownAsync(false);
        // A stopped Repeat leaves a held PCB at receive Z with IPM Up.
        Assert.Equal(PcbPlacementHandoff.Holding, rig.Placer.Handoff);
        Assert.Equal(PcbPlacementState.WaitingForSupplyRelease, rig.Placer.State);

        rig.Io.SetInput(InputIo.PcbPlacementIpmDown, true);
        Assert.Equal(PcbPlacementHandoff.Unavailable, rig.Placer.Handoff);
        rig.Io.SetInput(InputIo.PcbPlacementIpmDown, false);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.True(await rig.Placer.ExecuteStepAsync(
            rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, timeout.Token));
        Assert.True(rig.Handler.IsAtHorizontalZ());
        Assert.True(rig.Handler.IsAtY(rig.Position));
        Assert.Equal(50, rig.Motion.GetPosition().X);
        Assert.True(rig.Handler.PcbSecured);
        Assert.Equal(PlacementCylinderState.Down, rig.Handler.IpmLift);
        Assert.Equal(PcbPlacementHandoff.Clear, rig.Placer.Handoff);
    }

    [Fact]
    public async Task CompletedPressKeepsItsResultWhenRetractionIsCancelled()
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.ReceiveAsync();
        await rig.Placer.ExecuteStepAsync(rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, CancellationToken.None);
        rig.Supply.Handoff = PcbSupplyHandoff.Unavailable;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var interrupted = false;
        rig.Motion.PositionChanged += (x, y, z) =>
        {
            if (!interrupted && rig.Work.Assemblies.Any() && z < rig.Position.Z - 0.1)
            {
                interrupted = true;
                stop.Cancel();
            }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.Placer.ExecuteStepAsync(rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, stop.Token));
        Assert.True(interrupted);
        Assert.Single(rig.Work.Assemblies);
        Assert.Equal(PlacementPcbState.Detected, rig.Handler.Pcb);
        Assert.False(rig.Work.Completed);
        Assert.False(rig.Motion.IsMoving);
    }

    [Fact]
    public async Task PressDoesNotRecordAnAssemblyIfPcbDisappears()
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.ReceiveAsync();
        await rig.Placer.ExecuteStepAsync(rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, CancellationToken.None);
        rig.Supply.Handoff = PcbSupplyHandoff.Unavailable;
        var lost = false;
        rig.Io.InputChanged += (input, on) =>
        {
            if (input == InputIo.PcbPlacementIpmDown && on)
            {
                lost = true;
                rig.Io.SetInput(InputIo.PcbPlacementPcbDetected, false);
            }
        };
        Assert.Equal(PcbPlacementState.PlacingPcb, rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Placer.ExecuteStepAsync(rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, timeout.Token));
        Assert.True(lost);
        Assert.Empty(rig.Work.Assemblies);
        Assert.False(rig.Work.Completed);
    }

    private sealed class PlacementRig : IDisposable
    {
        public PlacementRig()
        {
            var settings = new PcbPlacementHandlerSettings
            {
                Motion = new() { HorizontalSpeed = 200, ZSpeed = 50 },
                HandoffPosition = new() { X = 50, Y = 10, Z = 8 },
                ReceiveZ = 12,
            };
            Position = new() { X = 70, Y = 20, Z = 10 };
            Io = new(Outputs(new PcbPlacementHandlerHardwareSettings(), new ConveyorHardwareSettings()), new());
            Motion = new(settings.Motion, new());

            Supply = new() { Handoff = PcbSupplyHandoff.Released };
            Units = new();
            Work = ConveyorStation.CreatePcbPlacement(Io);
            var recipes = new RecipeManager(OpenMachineStore(), new());
            recipes.Current.PcbPlacement.HeatSink1PcbPlacementPosition = Position;
            Placer = new PcbPlacer(Motion, new MotionStatus(Motion),
                Io,
                settings,
                Supply,
                Work,
                recipes,
                Units);
            Handler = Placer;
            Io.OutputChanged += OnOutputChanged;
        }

        public AxisPosition Position { get; }
        public UnitSettings Units { get; }
        public VirtualIoService Io { get; }
        public VirtualMotionService Motion { get; }
        public PcbPlacer Handler { get; }
        public ConveyorStation Work { get; }
        public PcbPlacer Placer { get; }
        public SupplyFeedback Supply { get; }

        public async Task ReceiveAsync()
        {
            await Handler.PrepareHandoffAsync();
            Supply.Handoff = PcbSupplyHandoff.Holding;
            Io.SetInput(InputIo.PcbPlacementPcbDetected, true);
            await Placer.ExecuteStepAsync(Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, CancellationToken.None);
            Supply.Handoff = PcbSupplyHandoff.Released;
        }

        public async Task InitializeAsync()
        {
            Io.Initialize();
            Motion.Initialize();
            await HomeAsync(Motion, 2_000);
            Io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
            await Work.SeatAsync(CancellationToken.None);
            await Handler.SetLiftDownAsync(false);
            await Handler.SetIpmLiftDownAsync(true);
            await Handler.MoveToHorizontalZAsync();
        }

        private void OnOutputChanged(OutputIo output, bool on)
        {
            if (output == OutputIo.PcbPlacementVacuumEjector)
                Io.SetInput(InputIo.PcbPlacementVacuumDetected, on);
        }

        public void Dispose()
        {
            Motion.Dispose();
        }
    }

    private sealed class SupplyFeedback : IPcbSupplyHandoff
    {
        public event Action? Changed;
        public bool PcbSecured => Handoff == PcbSupplyHandoff.Holding;
        public PcbSupplyHandoff Handoff
        {
            get;
            set
            {
                field = value;
                Changed?.Invoke();
            }
        }
    }
}
