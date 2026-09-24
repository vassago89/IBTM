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
            : rig.Placer.PlaceAsync(HeatSinkSlot.HeatSink1, timeout.Token));
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
            await rig.Placer.PlaceAsync(HeatSinkSlot.HeatSink1, CancellationToken.None);
        Assert.Equal(expected == PcbPlacementState.PreparingPlacement
            ? PcbPlacementState.WaitingForSupplyRelease : expected, rig.Placer.State);
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
            () => rig.Placer.PlaceAsync(HeatSinkSlot.HeatSink1, timeout.Token));
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
        await rig.Handler.MoveToHandoffXYAsync();
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
            () => rig.Placer.PlaceAsync(HeatSinkSlot.HeatSink1, timeout.Token));
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
        Assert.True(await rig.Placer.PlaceAsync(HeatSinkSlot.HeatSink1, timeout.Token));
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
        await rig.Placer.PlaceAsync(HeatSinkSlot.HeatSink1, CancellationToken.None);
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
            () => rig.Placer.PlaceAsync(HeatSinkSlot.HeatSink1, stop.Token));
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
        await rig.Placer.PlaceAsync(HeatSinkSlot.HeatSink1, CancellationToken.None);
        var lost = false;
        rig.Io.InputChanged += (input, on) =>
        {
            if (input == InputIo.PcbPlacementIpmDown && on)
            {
                lost = true;
                rig.Io.SetInput(InputIo.PcbPlacementPcbDetected, false);
            }
        };
        Assert.Equal(PcbPlacementState.PlacingPcb, rig.Placer.State);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Placer.PlaceAsync(HeatSinkSlot.HeatSink1, timeout.Token));
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
            var units = new UnitSettings();
            Work = new(ConveyorStation.CreatePcbPlacement(Io), units);
            var recipes = new RecipeManager(OpenMachineStore(), new());
            recipes.Current.PcbPlacement.HeatSink1PcbPlacementPosition = Position;
            Placer = new PcbPlacer(Motion, new MotionStatus(Motion),
                Io,
                settings,
                Supply,
                Work,
                recipes,
                units);
            Handler = Placer;
            Io.OutputChanged += OnOutputChanged;
        }

        public AxisPosition Position { get; }
        public VirtualIoService Io { get; }
        public VirtualMotionService Motion { get; }
        public PcbPlacer Handler { get; }
        public PcbPlacementWork Work { get; }
        public PcbPlacer Placer { get; }
        public SupplyFeedback Supply { get; }

        public async Task ReceiveAsync()
        {
            await Handler.MoveToHandoffXYAsync();
            Supply.Handoff = PcbSupplyHandoff.Holding;
            Io.SetInput(InputIo.PcbPlacementPcbDetected, true);
            await Placer.PlaceAsync(HeatSinkSlot.HeatSink1, CancellationToken.None);
            Supply.Handoff = PcbSupplyHandoff.Released;
        }

        public async Task InitializeAsync()
        {
            Io.Initialize();
            Motion.Initialize();
            await HomeAsync(Motion, 2_000);
            Io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
            await Work.Station.SeatAsync(CancellationToken.None);
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
