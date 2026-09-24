using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Storage;
using IBTM.Virtual;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTest;

namespace IBTM.Virtual.Tests;

public sealed class PcbPlacementRepeatTests
{
    [Fact]
    public async Task RepeatHoldingLossWhileWaitingForSupplyStopsWithoutPlacement()
    {
        using var rig = new RepeatRig(loadPcbs: true, enableSupply: true);
        await rig.InitializeAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = rig.Placer.RunAsync(stop.Token, repeat: true);
        try
        {
            Assert.True(await WaitUntilAsync(
                () => rig.Placer.State == PcbPlacementState.WaitingForSupplyReceipt,
                TimeSpan.FromSeconds(2)));
            rig.Io.SetInput(InputIo.PcbPlacementVacuumDetected, false);
            await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Empty(rig.Work.Assemblies);
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
    public async Task CompletedCarrierRepeatReportsWaitingFromItsAuthoritativeState()
    {
        using var rig = new RepeatRig(loadPcbs: true);
        await rig.InitializeAsync();
        rig.Work.Complete(rig.Work.CurrentJob);
        using var stop = new CancellationTokenSource();
        var run = rig.Placer.RunAsync(stop.Token, repeat: true);
        try
        {
            Assert.False(run.IsCompleted);
            Assert.Equal(PcbPlacementState.WaitingForCarrier, rig.Placer.State);
            Assert.Equal(rig.Placer.State, rig.Placer.Step);
            Assert.True(rig.Work.Completed);
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.Null(rig.Placer.Step);
    }

    [Fact]
    public async Task RepeatExchangesPcbsWithSupplyUsingCompletedStages()
    {
        using var rig = new RepeatRig(loadPcbs: true, enableSupply: true);
        await rig.InitializeAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        rig.Supply.Changed += () =>
        {
            if (rig.Supply.IsRunning)
                Assert.Equal(rig.Supply.State, rig.Supply.Step);
        };
        rig.Placer.Changed += () =>
        {
            if (rig.Placer.IsRunning)
                Assert.Equal(rig.Placer.State, rig.Placer.Step);
        };
        rig.Work.Changed += () =>
        {
            if (rig.Work.Completed)
                stop.Cancel();
        };
        var supply = rig.Supply.RunAsync(rig.SupplyRecipe, rig.Placer, stop.Token, repeat: true);
        var placement = rig.Placer.RunAsync(stop.Token, repeat: true);
        try
        {
            await Task.WhenAll(supply, placement);
            Assert.True(rig.Work.Completed, $"Supply={rig.Supply.State}, Placement={rig.Placer.State}");
            Assert.Equal(2, rig.Work.Assemblies.Count());
            Assert.False(rig.Supply.PcbSecured);
            Assert.False(rig.Placer.PcbSecured);
            Assert.Null(rig.Supply.Step);
            Assert.Null(rig.Placer.Step);
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(supply, placement);
        }
    }

    [Fact]
    public async Task MatchingReceiveOrHeatSinkCoordinatesDoesNotChangeTheProcessStage()
    {
        using var rig = new RepeatRig(loadPcbs: true);
        await rig.InitializeAsync();
        await rig.Motion.MoveToXYAsync(50, 10, 2_000);
        await rig.Motion.MoveAxisAsync(MotionAxis.Z, 12, 2_000);
        rig.Io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        await rig.Handler.SetVacuumAsync(true);
        Assert.Equal(PcbPlacementState.MovingToHandoff, rig.Placer.State);
        Assert.Equal(PcbPlacementHandoff.Unavailable, rig.Placer.Handoff);

        var target = rig.Recipe.HeatSink2PcbPlacementPosition;
        await rig.Motion.MoveToXYAsync(target.X, target.Y, 2_000);
        await rig.Motion.MoveAxisAsync(MotionAxis.Z, target.Z, 2_000);
        Assert.Equal(PcbPlacementState.MovingToHandoff, rig.Placer.State);
        Assert.Equal(HeatSinkSlot.HeatSink1, rig.Placer.TargetHeatSink);
        Assert.Empty(rig.Work.Assemblies);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RepeatApproachesHandoffXThenYAndCanStopBetweenAxes(bool enableSupply, bool stopAfterX)
    {
        using var rig = new RepeatRig(loadPcbs: true, enableSupply: enableSupply);
        await rig.InitializeAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var positions = new List<(double X, double Y, double Z)>();
        var reachedCorner = false;
        var reachedHandoff = false;
        rig.Motion.PositionChanged += (x, y, z) =>
        {
            if (!rig.Handler.PcbSecured || !rig.Motion.IsMovingHorizontal)
                return;
            positions.Add((x, y, z));
            if (x == 50 && y == 20)
            {
                reachedCorner = true;
                if (stopAfterX)
                    stop.Cancel();
            }
            if (x == 50 && y == 10)
            {
                reachedHandoff = true;
                stop.Cancel();
            }
        };

        await rig.Placer.RunAsync(stop.Token, repeat: true);

        Assert.True(reachedCorner);
        Assert.Equal(!stopAfterX, reachedHandoff);
        Assert.NotEmpty(positions);
        Assert.All(positions, position =>
        {
            Assert.Equal(8, position.Z);
            if (position.Y != 20)
                Assert.Equal(50, position.X);
        });
        Assert.Equal((50.0, stopAfterX ? 20.0 : 10.0, 8.0), rig.Motion.GetPosition());
        Assert.False(rig.Motion.IsMoving);
        Assert.True(rig.Handler.PcbSecured);
        Assert.Null(rig.Placer.ReturningPcb);
        Assert.Empty(rig.Work.Assemblies);
    }

    [Fact]
    public async Task RepeatStillPicksWhenSupplyOnlyDetectsANearbyPcb()
    {
        using var rig = new RepeatRig(loadPcbs: true, enableSupply: true);
        await rig.InitializeAsync();
        rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        rig.Io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        Assert.False(rig.Handler.PcbSecured);
        var descendedAtPickup = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        rig.Io.OutputChanged += (output, on) =>
        {
            if (output != OutputIo.PcbPlacementHandlerDown || !on)
                return;
            var position = rig.Recipe.HeatSink1PcbPlacementPosition;
            descendedAtPickup = rig.Handler.IsAtXY(position) && rig.Handler.IsAtZ(position);
            stop.Cancel();
        };
        await rig.Placer.RunAsync(stop.Token, repeat: true);
        Assert.True(descendedAtPickup);
        Assert.Empty(rig.Work.Assemblies);
        Assert.False(rig.Work.Completed);
    }

    [Fact]
    public async Task RepeatReusesBothPcbsWithoutSupply()
    {
        using var rig = new RepeatRig(loadPcbs: true);
        await rig.InitializeAsync();
        await rig.Handler.SetIpmLiftDownAsync(true);
        using var firstStop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var handoffVisits = 0;
        var insideHandoff = false;
        var movedUnsafely = false;
        var supplyOutputs = new List<OutputIo>();
        var supplyHardware = new PcbSupplyHardwareSettings();
        rig.Io.OutputChanged += (output, on) =>
        {
            if (output is OutputIo.PcbPlacementHandlerRotate or OutputIo.PcbPlacementIpmDown)
                Assert.False(on);
            if (supplyHardware.Outputs.ContainsKey(output))
                supplyOutputs.Add(output);
        };
        rig.Motion.PositionChanged += (x, y, z) =>
        {
            movedUnsafely |= (rig.Motion.IsMoving && rig.Handler.Lift != PlacementCylinderState.Up)
                || (rig.Motion.IsMovingHorizontal && Math.Abs(z - 8) > 0.05);
            var atHandoff = Math.Abs(x - 50) < 0.05 && Math.Abs(y - 10) < 0.05 && Math.Abs(z - 8) < 0.05;
            if (atHandoff && !insideHandoff && rig.Handler.PcbSecured)
                handoffVisits++;
            insideHandoff = atHandoff;
        };
        rig.Work.Changed += () =>
        {
            if (rig.Work.Completed)
                firstStop.Cancel();
        };

        await rig.Placer.RunAsync(firstStop.Token, repeat: true);
        Assert.True(rig.Work.Completed, rig.Placer.State.ToString());
        Assert.Equal(2, rig.Work.Assemblies.Count());
        Assert.Equal(2, handoffVisits);
        Assert.False(movedUnsafely);
        Assert.Empty(supplyOutputs);
        Assert.Equal(PlacementCylinderState.Up, rig.Handler.Lift);
        Assert.Equal(PlacementCylinderState.Up, rig.Handler.IpmLift);
        Assert.True(rig.Handler.IsAtHorizontalZ());
        Assert.False(rig.Handler.VacuumDetected);

        // The material remains on both original heat sinks after the repeat.
        foreach (var position in rig.Positions)
        {
            await rig.Handler.MoveToXYAsync(position);
            await rig.Handler.MoveAxisAsync(MotionAxis.Z, position.Z);
            await rig.Handler.SetLiftDownAsync(true);
            Assert.Equal(PlacementPcbState.Detected, rig.Handler.Pcb);
            await rig.Handler.SetLiftDownAsync(false);
            await rig.Handler.MoveToHorizontalZAsync();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepeatStopsOnCarrierOrHoldingLossAndDoesNotRecordPlacement(bool changeCarrier)
    {
        using var rig = new RepeatRig(loadPcbs: true);
        await rig.InitializeAsync();
        var interrupted = false;
        rig.Motion.PositionChanged += (x, _, _) =>
        {
            if (interrupted || !rig.Handler.PcbSecured || !rig.Motion.IsMovingHorizontal || x >= 69)
                return;
            interrupted = true;
            if (changeCarrier)
                rig.Io.SetInputs(
                    (InputIo.PcbPlacementHeatSink1Present, false),
                    (InputIo.PcbPlacementHeatSink2Present, false));
            else
                rig.Io.SetInput(InputIo.PcbPlacementVacuumDetected, false);
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Placer.RunAsync(timeout.Token, repeat: true));
        Assert.True(interrupted);
        Assert.False(rig.Motion.IsMoving);
        Assert.Empty(rig.Work.Assemblies);
        Assert.False(rig.Work.Completed);
    }

    [Fact]
    public async Task RepeatDoesNotLiftWhenPcbDetectionDropsDuringVacuumPickup()
    {
        using var rig = new RepeatRig(loadPcbs: true);
        await rig.InitializeAsync();
        var lost = false;
        var raisedAfterLoss = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        rig.Io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbPlacementVacuumEjector && on)
            {
                lost = true;
                rig.Io.SetInput(InputIo.PcbPlacementPcbDetected, false);
                rig.Io.SetInput(InputIo.PcbPlacementVacuumDetected, true);
            }
            if (lost && output == OutputIo.PcbPlacementHandlerDown && !on)
            {
                raisedAfterLoss = true;
                stop.Cancel();
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Placer.RunAsync(stop.Token, repeat: true));
        Assert.True(lost);
        Assert.False(raisedAfterLoss);
        Assert.True(rig.Handler.VacuumDetected);
        Assert.Equal(PlacementCylinderState.Down, rig.Handler.Lift);
        Assert.Empty(rig.Work.Assemblies);
        Assert.False(rig.Work.Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledRepeatReturnsHeldPcbWithoutPickingItAgain(bool stopDuringPickup)
    {
        using var rig = new RepeatRig(loadPcbs: true);
        await rig.InitializeAsync();
        rig.Io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbPlacementIpmDown)
                Assert.False(on);
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        void StopWhileHolding(double x, double y, double z)
        {
            if (!stopDuringPickup && rig.Handler.PcbSecured && rig.Motion.IsMovingHorizontal && x < 69)
                stop.Cancel();
        }
        void StopOnVacuum(InputIo input, bool on)
        {
            if (stopDuringPickup && input == InputIo.PcbPlacementVacuumDetected && on)
                stop.Cancel();
        }
        rig.Motion.PositionChanged += StopWhileHolding;
        rig.Io.InputChanged += StopOnVacuum;
        await rig.Placer.RunAsync(stop.Token, repeat: true);
        rig.Motion.PositionChanged -= StopWhileHolding;
        rig.Io.InputChanged -= StopOnVacuum;
        Assert.True(rig.Handler.PcbSecured);
        Assert.Empty(rig.Work.Assemblies);
        Assert.False(rig.Work.Completed);
        Assert.False(rig.Motion.IsMoving);
        Assert.Null(rig.Placer.ReturningPcb);

        var repicked = false;
        rig.Placer.StepChanged += () => repicked |= rig.Placer.Step is PcbPlacementState.PickingPcb
            && rig.Placer.TargetHeatSink == HeatSinkSlot.HeatSink1;
        using var resume = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        rig.Work.Changed += () =>
        {
            if (rig.Work.Completed)
                resume.Cancel();
        };
        await rig.Placer.RunAsync(resume.Token, repeat: true);
        Assert.False(repicked);
        Assert.True(rig.Work.Completed, rig.Placer.State.ToString());
        Assert.Equal(2, rig.Work.Assemblies.Count());
        Assert.False(rig.Placer.PcbSecured);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatRestartsTheSecondPcbHandoffWithItsOriginalSlot(bool stopWhileSupplyHolds)
    {
        using var rig = new RepeatRig(loadPcbs: true, enableSupply: true);
        await rig.InitializeAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var stoppedAtHandoff = false;
        void StopAtHandoff()
        {
            if (rig.Placer.TargetHeatSink == HeatSinkSlot.HeatSink2
                && (stopWhileSupplyHolds
                    ? rig.Supply.Step is PcbSupplyState.ReturningToPickup
                    : rig.Placer.Step is PcbPlacementState.WaitingForSupplyReceipt))
            {
                stoppedAtHandoff = true;
                stop.Cancel();
            }
        }
        rig.Placer.StepChanged += StopAtHandoff;
        rig.Supply.StepChanged += StopAtHandoff;
        await Task.WhenAll(
            rig.Supply.RunAsync(rig.SupplyRecipe, rig.Placer, stop.Token, repeat: true),
            rig.Placer.RunAsync(stop.Token, repeat: true));
        rig.Placer.StepChanged -= StopAtHandoff;
        rig.Supply.StepChanged -= StopAtHandoff;
        Assert.True(stoppedAtHandoff);
        Assert.Single(rig.Work.Assemblies);
        Assert.Equal(HeatSinkSlot.HeatSink2, rig.Placer.TargetHeatSink);

        var repicked = false;
        var visitedOriginalSlot = false;
        rig.Placer.StepChanged += () => repicked |= rig.Placer.Step is PcbPlacementState.PickingPcb;
        rig.SupplyMotion.PositionChanged += (x, y, z) =>
        {
            if (rig.Supply.PcbSecured && rig.Supply.State == PcbSupplyState.ReturningToPickup
                && rig.SupplyMotion.IsMovingHorizontal)
            {
                Assert.NotEqual(10, x);
                visitedOriginalSlot |= x == 20 && y == 30;
            }
        };
        using var resume = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        rig.Work.Changed += () =>
        {
            if (rig.Work.Completed)
                resume.Cancel();
        };
        await Task.WhenAll(
            rig.Supply.RunAsync(rig.SupplyRecipe, rig.Placer, resume.Token, repeat: true),
            rig.Placer.RunAsync(resume.Token, repeat: true));

        Assert.False(repicked);
        Assert.True(visitedOriginalSlot);
        Assert.True(rig.Work.Completed, $"Supply={rig.Supply.State}, Placement={rig.Placer.State}");
        Assert.Equal(2, rig.Work.Assemblies.Count());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepeatRestartRejectsLostCarrierOrGripBeforeCommanding(bool loseCarrier)
    {
        using var rig = new RepeatRig(loadPcbs: true, enableSupply: true);
        await rig.InitializeAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        rig.Placer.StepChanged += () =>
        {
            if (rig.Placer.Step is PcbPlacementState.WaitingForSupplyReceipt)
                stop.Cancel();
        };
        await rig.Placer.RunAsync(stop.Token, repeat: true);
        Assert.True(rig.Placer.PcbSecured);
        if (loseCarrier)
            rig.Io.SetInputs((InputIo.PcbPlacementHeatSink1Present, false), (InputIo.PcbPlacementHeatSink2Present, false));
        else
            rig.Io.SetInput(InputIo.PcbPlacementVacuumDetected, false);
        var commanded = false;
        rig.Motion.MovingChanged += moving => commanded |= moving;
        rig.Io.OutputChanged += (output, on) => commanded = true;
        using var resume = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Placer.RunAsync(resume.Token, repeat: true));

        Assert.False(commanded);
        Assert.Empty(rig.Work.Assemblies);
        Assert.False(rig.Work.Completed);
    }

    [Fact]
    public async Task UnfinishedRepeatCannotBypassTheCarrierOrStartAsNormalPlacement()
    {
        using var rig = new RepeatRig(loadPcbs: true, enableSupply: true);
        await rig.InitializeAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        rig.Placer.StepChanged += () =>
        {
            if (rig.Placer.Step is PcbPlacementState.WaitingForSupplyReceipt)
                stop.Cancel();
        };
        await rig.Placer.RunAsync(stop.Token, repeat: true);
        Assert.True(rig.Placer.PcbSecured);
        rig.Units.PcbPlacement = false;
        using var disabled = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        rig.Placer.Trace += message =>
        {
            if (message.StartsWith("PcbPlacer: Disabled "))
                disabled.Cancel();
        };
        await rig.Placer.RunAsync(disabled.Token);
        Assert.False(rig.Work.Completed);
        Assert.False(rig.Work.IsTransferAllowed);

        rig.Units.PcbPlacement = true;
        var commanded = false;
        rig.Motion.MovingChanged += moving => commanded |= moving;
        rig.Io.OutputChanged += (output, on) => commanded = true;
        using var normal = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Placer.RunAsync(normal.Token));
        Assert.False(commanded);
        Assert.True(rig.Placer.PcbSecured);
        Assert.Equal(HeatSinkSlot.HeatSink1, rig.Placer.TargetHeatSink);
    }

    [Fact]
    public async Task RepeatDoesNotTreatAMissingPcbAsCompleted()
    {
        using var rig = new RepeatRig(loadPcbs: false);
        await rig.InitializeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await Assert.ThrowsAsync<IoTimeoutException>(
            () => rig.Placer.RunAsync(timeout.Token, repeat: true));
        Assert.Empty(rig.Work.Assemblies);
        Assert.False(rig.Work.Completed);
        Assert.False(rig.Io.GetOutput(OutputIo.PcbPlacementVacuumEjector));
        Assert.True(rig.Handler.IsAtXY(rig.Recipe.HeatSink1PcbPlacementPosition));
    }

    private sealed class RepeatRig : IDisposable
    {
        private readonly VirtualMotionService _supplyMotion;

        public RepeatRig(bool loadPcbs, bool enableSupply = false)
        {
            Recipe = new()
            {
                HeatSink1PcbPlacementPosition = new() { X = 70, Y = 20, Z = 10 },
                HeatSink2PcbPlacementPosition = new() { X = 80, Y = 20, Z = 10 },
            };

            var motion = new MotionSettings { HorizontalSpeed = 2_000, ZSpeed = 2_000 };
            var settings = new PcbPlacementHandlerSettings
            {
                Motion = motion,
                HandoffPosition = new() { X = 50, Y = 10, Z = 8 },
                ReceiveZ = 12,
            };
            var supplySettings = new PcbSupplySettings
            {
                Motion = motion,
                RotationZ = 3,
                HandoffPosition = new() { X = 50, Y = 10, Z = 7 },
            };
            SupplyRecipe = new()
            {
                Pcb1PickPosition = new() { X = 10, Y = 30, Z = 5 },
                Pcb2PickPosition = new() { X = 20, Y = 30, Z = 5 },
            };
            Io = new(
                Outputs(new PcbPlacementHandlerHardwareSettings(), new PcbSupplyHardwareSettings(), new ConveyorHardwareSettings()),
                new MachineOptions { TimeoutMilliseconds = 1_000 });
            Motion = new(motion, new OperationCancellation());
            _supplyMotion = new(motion, new());
            var simulation = new VirtualMachine(Io, [], incomingCarrierHasPcbs: () => loadPcbs);
            Motion.PositionChanged += (x, y, z) => simulation.UpdatePlacementPosition(
                x, y, z, settings.HandoffPosition, settings.ReceiveZ,
                Recipe.HeatSink1PcbPlacementPosition, Recipe.HeatSink2PcbPlacementPosition);
            _supplyMotion.PositionChanged += (x, y, z) => simulation.UpdateSupplyPosition(
                x, y, z, (10, 30, 5), (20, 30, 5), supplySettings.HandoffPosition);

            Units = new() { PcbSupply = enableSupply };
            Supply = new PcbSupplier(_supplyMotion, new MotionStatus(_supplyMotion), Io, supplySettings, Units);
            Work = new(ConveyorStation.CreatePcbPlacement(Io), Units);
            var recipes = new RecipeManager(OpenMachineStore(), new());
            recipes.Current.PcbPlacement = Recipe;
            Placer = new PcbPlacer(Motion, new MotionStatus(Motion),
                Io,
                settings,
                Supply,
                Work,
                recipes,
                Units);
            Handler = Placer;
        }

        public VirtualIoService Io { get; }
        public UnitSettings Units { get; }
        public VirtualMotionService Motion { get; }
        public VirtualMotionService SupplyMotion => _supplyMotion;
        public PcbPlacer Handler { get; }
        public PcbPlacementWork Work { get; }
        public PcbPlacer Placer { get; }
        public PcbPlacementRecipe Recipe { get; }
        public PcbSupplier Supply { get; }
        public PcbSupplyRecipe SupplyRecipe { get; }

        public AxisPosition[] Positions => [Recipe.HeatSink1PcbPlacementPosition, Recipe.HeatSink2PcbPlacementPosition];

        public async Task InitializeAsync()
        {
            Io.Initialize();
            Motion.Initialize();
            _supplyMotion.Initialize();
            await Task.WhenAll(HomeAsync(Motion, 2_000), HomeAsync(_supplyMotion, 2_000));
            // Receive the simulator's material instead of overwriting its PCB detection inputs.
            Io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
            Io.SetOutput(OutputIo.MainConveyorForward, true);
            await ((IIoService)Io).SetOutputAndWaitAsync(OutputIo.PcbPlacementStopperUp, true);
            Io.SetOutput(OutputIo.MainConveyorRun, true);
            await ((IIoService)Io).WaitForInputAsync(InputIo.PcbPlacementHeatSink2Present, true);
            Io.SetOutput(OutputIo.MainConveyorRun, false);
            await Work.Station.SeatAsync(CancellationToken.None);
            await Handler.MoveToHorizontalZAsync();
        }

        public void Dispose()
        {
            Motion.Dispose();
            _supplyMotion.Dispose();
        }
    }
}
