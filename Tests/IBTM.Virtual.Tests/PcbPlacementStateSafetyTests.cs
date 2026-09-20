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
    public async Task PartialGripAwayFromSupportsDoesNotRestartByMovingOrOpening(bool repeat)
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.Handler.MoveToXYAsync(new() { X = 30, Y = 20, Z = 8 });
        rig.Io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        await rig.Handler.SetVacuumAsync(true);
        Assert.Equal(PlacementPcbState.Detected, rig.Handler.Pcb);
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
    [InlineData(PcbPlacementState.PlacingPcb, InputIo.PcbPlacementIpmGripperClosed)]
    public async Task NormalPlacementStopsOnGripLossBeforeRelease(PcbPlacementState expected, InputIo lostInput)
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        var start = expected == PcbPlacementState.PreparingPlacement
            ? new AxisPosition { X = 50, Y = 10, Z = 12 }
            : new AxisPosition { X = 30, Y = 20, Z = 8 };
        await rig.Handler.MoveToXYAsync(start);
        await rig.Handler.MoveAxisAsync(MotionAxis.Z, start.Z);
        rig.Io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        await rig.Handler.SetVacuumAsync(true);
        await rig.Handler.SetIpmGripperAsync(true);
        Assert.Equal(expected == PcbPlacementState.PreparingPlacement
            ? PcbPlacementState.WaitingForSupplyRelease : expected, rig.Placer.State);
        var lost = false;
        var lowered = false;
        var released = false;
        rig.Motion.PositionChanged += (x, y, z) =>
        {
            if (!lost && rig.Motion.IsMoving
                && (expected == PcbPlacementState.PreparingPlacement ? z < 11.9 : x > 30.1))
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
        Assert.False(rig.Io.GetOutput(OutputIo.PcbPlacementIpmGripperClose));
        Assert.Empty(rig.Work.Assemblies);
    }

    [Fact]
    public async Task CompletedPressCanResumeRetractionWithNearbyPcbDetectionStillOn()
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.Handler.MoveToXYAsync(rig.Position);
        await rig.Handler.MoveAxisAsync(MotionAxis.Z, rig.Position.Z);
        await rig.Handler.SetLiftDownAsync(true);
        await rig.Handler.SetIpmLiftDownAsync(false);
        rig.Io.SetInput(InputIo.PcbPlacementPcbDetected, true);
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
        Assert.Equal(PcbPlacementState.PreparingPlacement, rig.Placer.State);

        using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await rig.Placer.PlaceAsync(HeatSinkSlot.HeatSink1, finish.Token);

        Assert.True(rig.Handler.HandlerRaised);
        Assert.True(rig.Handler.IsAtHorizontalZ());
        Assert.Single(rig.Work.Assemblies);
    }

    [Fact]
    public async Task PressDoesNotRecordAnAssemblyIfPcbDisappears()
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.Handler.MoveToXYAsync(rig.Position);
        await rig.Handler.MoveAxisAsync(MotionAxis.Z, rig.Position.Z);
        await rig.Handler.SetLiftDownAsync(true);
        await rig.Handler.SetIpmLiftDownAsync(false);
        rig.Io.SetInput(InputIo.PcbPlacementPcbDetected, true);
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
            Motion = new(settings.Motion, new(), horizontalZ: () => settings.HandoffPosition.Z);

            Supply = new() { Handoff = PcbSupplyHandoff.Released };
            var units = new UnitSettings();
            Work = new(ConveyorStation.CreatePcbPlacement(Io), units);
            var recipes = new RecipeManager(OpenMachineStore(), new());
            recipes.Current.PcbPlacement.HeatSink1PcbPlacementPosition = Position;
            Placer = new PcbPlacer(Motion,
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

        public async Task InitializeAsync()
        {
            Io.Initialize();
            Motion.Initialize();
            await HomeAsync(Motion, 2_000);
            Io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
            await Work.Station.SeatAsync(CancellationToken.None);
            await Handler.SetLiftDownAsync(false);
            await Handler.SetIpmLiftDownAsync(true);
            await Handler.SetIpmGripperAsync(false);
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
