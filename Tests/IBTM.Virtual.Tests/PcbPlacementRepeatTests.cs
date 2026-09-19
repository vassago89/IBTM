using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbBuffer;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Virtual;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTest;

namespace IBTM.Virtual.Tests;

public sealed class PcbPlacementRepeatTests
{
    [Fact]
    public async Task RepeatReusesBothPcbsWithoutSupply()
    {
        using var rig = new RepeatRig(loadPcbs: true);
        await rig.InitializeAsync();
        using var firstStop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var handoffVisits = 0;
        var insideHandoff = false;
        var movedUnsafely = false;
        var supplyOutputs = new List<OutputIo>();
        var supplyHardware = new PcbSupplyHardwareSettings();
        rig.Io.OutputChanged += (output, _) =>
        {
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

        await rig.Placer.RunAsync(rig.Recipe, firstStop.Token, repeat: true);
        Assert.True(rig.Work.Completed, rig.Placer.GetState(rig.Recipe).ToString());
        Assert.Equal(2, rig.Work.Assemblies.Count());
        Assert.Equal(2, handoffVisits);
        Assert.False(movedUnsafely);
        Assert.Empty(supplyOutputs);
        Assert.Equal(PlacementCylinderState.Up, rig.Handler.Lift);
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
            () => rig.Placer.RunAsync(rig.Recipe, timeout.Token, repeat: true));
        Assert.True(interrupted);
        Assert.False(rig.Motion.IsMoving);
        Assert.Empty(rig.Work.Assemblies);
        Assert.False(rig.Work.Completed);
    }

    [Fact]
    public async Task StoppedRepeatWithHeldPcbRestartsWithoutReset()
    {
        using var rig = new RepeatRig(loadPcbs: true);
        await rig.InitializeAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        void StopWhileHolding(double x, double y, double z)
        {
            if (rig.Handler.PcbSecured && rig.Motion.IsMovingHorizontal && x < 69)
                stop.Cancel();
        }
        rig.Motion.PositionChanged += StopWhileHolding;
        await rig.Placer.RunAsync(rig.Recipe, stop.Token, repeat: true);
        rig.Motion.PositionChanged -= StopWhileHolding;
        Assert.True(rig.Handler.PcbSecured);
        Assert.Empty(rig.Work.Assemblies);
        var job = rig.Work.CurrentJob;

        using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        rig.Work.Changed += () =>
        {
            if (rig.Work.Completed)
                finish.Cancel();
        };
        await rig.Placer.RunAsync(rig.Recipe, finish.Token, repeat: true);
        Assert.Same(job, rig.Work.CurrentJob);
        Assert.True(rig.Work.Completed, rig.Placer.GetState(rig.Recipe).ToString());
        Assert.Equal(2, rig.Work.Assemblies.Count());
    }

    [Fact]
    public async Task RepeatDoesNotTreatAMissingPcbAsCompleted()
    {
        using var rig = new RepeatRig(loadPcbs: false);
        await rig.InitializeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await Assert.ThrowsAsync<IoTimeoutException>(
            () => rig.Placer.RunAsync(rig.Recipe, timeout.Token, repeat: true));
        Assert.Empty(rig.Work.Assemblies);
        Assert.False(rig.Work.Completed);
        Assert.False(rig.Io.GetOutput(OutputIo.PcbPlacementVacuumEjector));
        Assert.True(rig.Handler.IsAtXY(rig.Recipe.HeatSink1PcbPlacementPosition));
    }

    private sealed class RepeatRig : IDisposable
    {
        private readonly VirtualMotionService _supplyMotion;

        public RepeatRig(bool loadPcbs)
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
                BufferHandoffPosition = new() { X = 50, Y = 10, Z = 8 },
            };
            var supplySettings = new PcbSupplySettings { Motion = motion };
            Io = new(
                Outputs(new PcbPlacementHandlerHardwareSettings(), new PcbSupplyHardwareSettings(), new ConveyorHardwareSettings()),
                new MachineOptions { TimeoutMilliseconds = 1_000 });
            Motion = new(motion, new OperationCancellation(), horizontalZ: () => settings.BufferHandoffPosition.Z);
            _supplyMotion = VirtualTest.Motion(motion, new());
            var simulation = new VirtualMachine(Io, [], incomingCarrierHasPcbs: () => loadPcbs);
            Motion.PositionChanged += (x, y, z) => simulation.UpdatePlacementPosition(
                x, y, z, settings.BufferHandoffPosition,
                Recipe.HeatSink1PcbPlacementPosition, Recipe.HeatSink2PcbPlacementPosition);
            Handler = new(Motion, Io, settings);
            var supply = new PcbSupplyHandler(_supplyMotion, Io, supplySettings);
            var buffer = new BufferStage(
                supply, Handler, supply.Motion, Handler.Motion,
                supplySettings.BufferHandoffPosition, settings.BufferHandoffPosition, new());
            Work = new(ConveyorStation.CreatePcbPlacement(Io), new());
            Placer = new(buffer, Handler, Work);
        }

        public VirtualIoService Io { get; }
        public VirtualMotionService Motion { get; }
        public PcbPlacementHandler Handler { get; }
        public PcbPlacementWork Work { get; }
        public PcbPlacer Placer { get; }
        public PcbPlacementRecipe Recipe { get; }

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
            await Handler.SetRotatedAsync(true);
        }

        public void Dispose()
        {
            Motion.Dispose();
            _supplyMotion.Dispose();
        }
    }
}
