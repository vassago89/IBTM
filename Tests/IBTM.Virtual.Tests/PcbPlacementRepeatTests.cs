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
    public async Task RepeatReusesBothPcbsAndResumesTheSameTripAfterStopAtHandoff()
    {
        using var rig = new RepeatRig();
        await rig.InitializeAsync(loadPcbs: true);
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
            movedUnsafely |= rig.Motion.IsMovingHorizontal
                && (rig.Handler.Lift != PlacementCylinderState.Up || Math.Abs(z - 8) > 0.05);
            var atHandoff = Math.Abs(x - 50) < 0.05 && Math.Abs(y - 10) < 0.05 && Math.Abs(z - 8) < 0.05;
            if (atHandoff && !insideHandoff && rig.Handler.PcbSecured)
                handoffVisits++;
            insideHandoff = atHandoff;
        };
        rig.Placer.Trace += message =>
        {
            if (message.StartsWith("PcbPlacer: MovingToWaitPosition ", StringComparison.Ordinal)
                && rig.Handler.IsAtBufferXY())
                firstStop.Cancel();
        };

        await rig.Placer.RunAsync(rig.Recipe, firstStop.Token, repeat: true);
        Assert.True(rig.Handler.PcbSecured);
        Assert.True(rig.Handler.IsAtBufferXY());
        Assert.Equal(HeatSinkSlot.HeatSink1, rig.Placer.TargetHeatSink);
        Assert.Empty(rig.Work.Assemblies);
        Assert.False(rig.Work.Completed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Placer.RunAsync(rig.Recipe));
        Assert.Throws<InvalidOperationException>(
            () => rig.Placer.PrepareRecovery([(HeatSinkSlot.HeatSink1, true)]));
        rig.Placer.PrepareRecovery([(HeatSinkSlot.HeatSink1, false), (HeatSinkSlot.HeatSink2, false)]);

        using var resumed = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        rig.Work.Changed += () =>
        {
            if (rig.Work.Completed)
                resumed.Cancel();
        };
        await rig.Placer.RunAsync(rig.Recipe, resumed.Token, repeat: true);

        Assert.True(rig.Work.Completed, rig.Placer.State(rig.Recipe).ToString());
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
        using var rig = new RepeatRig();
        await rig.InitializeAsync(loadPcbs: true);
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

        if (changeCarrier)
        {
            rig.Io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => rig.Placer.RunAsync(rig.Recipe, timeout.Token, repeat: true));
            Assert.Equal(HeatSinkSlot.HeatSink1, rig.Placer.TargetHeatSink);
            Assert.Throws<InvalidOperationException>(
                () => rig.Placer.PrepareRecovery([(HeatSinkSlot.HeatSink2, false)]));
            await rig.Handler.SetVacuumAsync(false);
            await rig.Handler.SetIpmGripperAsync(false);
            rig.Placer.PrepareRecovery([(HeatSinkSlot.HeatSink2, false)]);
            Assert.Equal(HeatSinkSlot.HeatSink2, rig.Placer.TargetHeatSink);
        }
    }

    [Fact]
    public async Task RepeatDoesNotTreatAMissingPcbAsCompleted()
    {
        using var rig = new RepeatRig();
        await rig.InitializeAsync(loadPcbs: false);
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
        public VirtualIoService Io { get; }
        public VirtualMotionService Motion { get; }
        public PcbPlacementHandler Handler { get; }
        public PcbPlacementWork Work { get; }
        public PcbPlacer Placer { get; }
        public PcbPlacementRecipe Recipe { get; } = new()
        {
            HeatSink1PcbPlacementPosition = new() { X = 70, Y = 20, Z = 10 },
            HeatSink2PcbPlacementPosition = new() { X = 80, Y = 20, Z = 10 },
        };
        public AxisPosition[] Positions
        {
            get
            {
                return [Recipe.HeatSink1PcbPlacementPosition, Recipe.HeatSink2PcbPlacementPosition];
            }
        }

        public RepeatRig()
        {
            var motion = new MotionSettings { HorizontalSpeed = 2_000, ZSpeed = 2_000 };
            var settings = new PcbPlacementHandlerSettings
            {
                Motion = motion,
                BufferHandoffPosition = new() { X = 50, Y = 10, Z = 8 },
            };
            var supplySettings = new PcbSupplySettings { Motion = motion };
            var bufferSettings = new PcbBufferSettings
            {
                SupplyBoundary1 = 40, SupplyBoundary2 = 60,
                PlacementBoundary1 = new() { X = 40, Y = 0 },
                PlacementBoundary2 = new() { X = 60, Y = 15 },
            };
            Io = new(
                Outputs(new PcbPlacementHandlerHardwareSettings(), new PcbSupplyHardwareSettings(), new ConveyorHardwareSettings()),
                new MachineOptions { TimeoutMilliseconds = 1_000 });
            Motion = new(motion, new OperationCancellation(), horizontalZ: () => settings.BufferHandoffPosition.Z);
            _supplyMotion = VirtualTest.Motion(motion, new());
            var simulation = new VirtualMachine(Io, []);
            Motion.PositionChanged += (x, y, z) => simulation.UpdatePlacementPosition(
                x, y, z, settings.BufferHandoffPosition,
                Recipe.HeatSink1PcbPlacementPosition, Recipe.HeatSink2PcbPlacementPosition);
            Handler = new(Motion, Io, settings);
            var supply = new PcbSupplyHandler(_supplyMotion, Io, supplySettings, bufferSettings);
            var buffer = new BufferStage(
                bufferSettings, supply, Handler, supply.Motion, Handler.Motion,
                supplySettings.BufferHandoffPosition, settings.BufferHandoffPosition, () => 0);
            Work = new(ConveyorStation.PcbPlacement(Io));
            Placer = new(buffer, Handler, Work);
        }

        public async Task InitializeAsync(bool loadPcbs)
        {
            Io.Initialize();
            Motion.Initialize();
            _supplyMotion.Initialize();
            await Task.WhenAll(HomeAsync(Motion, 2_000), HomeAsync(_supplyMotion, 2_000));
            Io.SetInputs((InputIo.PcbPlacementHeatSink1Present, true), (InputIo.PcbPlacementHeatSink2Present, true));
            await Work.Station.SeatAsync(CancellationToken.None);
            await Handler.MoveToHorizontalZAsync();
            await Handler.SetRotatedAsync(true);
            if (!loadPcbs)
                return;

            // Load the virtual carrier through the simulator's normal grip/release response.
            foreach (var position in Positions)
            {
                await Handler.MoveToXYAsync(position);
                await Handler.MoveAxisAsync(MotionAxis.Z, position.Z);
                await Handler.SetLiftDownAsync(true);
                Io.SetInput(InputIo.PcbPlacementPcbDetected, true);
                await Handler.SetVacuumAsync(true);
                await Handler.SetIpmGripperAsync(true);
                await Handler.SetVacuumAsync(false);
                await Handler.SetIpmGripperAsync(false);
                await Handler.SetLiftDownAsync(false);
                await Handler.MoveToHorizontalZAsync();
            }
        }

        public void Dispose()
        {
            Motion.Dispose();
            _supplyMotion.Dispose();
        }
    }
}
