using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.BoltFeeder;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Hantas;
using IBTM.Storage;
using IBTM.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTestSupport;

namespace IBTM.Virtual.Tests;

public sealed class BoltFasteningTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task ShootingFinalRequiresVacuumBeforeLoweringAndStarting(bool vacuumDetected, bool dryRun)
    {
        var settings = new BoltFasteningSettings
        {
            MonitorTorqueCurves = false,
            ShootingArrivalDelaySeconds = 0,
            DryRunMilliseconds = 30,
            SafeZ = 3,
            ShootingHead = new() { FasteningZ = 12 },
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
        };
        var units = new UnitSettings { ShootingBoltFeeder = !dryRun };
        var io = new VirtualIoService(Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()),
            new() { TimeoutMilliseconds = 500 });
        io.Initialize();
        io.SetInput(InputIo.ShootingFeederBoltDetected, true);
        using var motion = new VirtualMotionService(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var shootingBus = new AdcControllerStub();
        using var pickupBus = new AdcControllerStub();
        var shooting = CreateAdcHead(shootingBus, io, FasteningHead.Shooting,
            new() { StatusPollMilliseconds = 10 }, 1, "Shooting", 115200);
        var pickup = CreateAdcHead(pickupBus, io, FasteningHead.Pickup, new(), 1, "Pickup", 115200);
        var bolt = Bolt(1, FasteningHead.Shooting, 20, 30);
        var work = ConveyorStation.CreateBoltFastening(io);
        var station = new BoltFasteningStation(shooting, pickup, io, motion, new(motion), settings, work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = new() { BoltPoints = [bolt] } } },
            units, new BoltFeederUnit(io, new(), units));
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        var headLowered = false;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.ShootBolt && on)
            {
                io.SetInput(InputIo.ShootingTubeBoltDetected, true);
                io.SetInput(InputIo.ShootingTubeBoltDetected, false);
                io.SetInput(InputIo.ShootingHeadVacuumDetected, true);
                io.SetInput(InputIo.ShootingHeadVacuumDetected, vacuumDetected);
            }
            if (output == OutputIo.ShootingHeadDown && on)
            {
                headLowered = true;
                Assert.True(dryRun || io.GetInput(InputIo.ShootingHeadVacuumDetected));
                Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
            }
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = station.RunAsync(stop.Token, selectedBolts: [bolt.Id]);
        if (!vacuumDetected && !dryRun)
        {
            var error = await Assert.ThrowsAsync<IoTimeoutException>(() => run);
            Assert.Equal(InputIo.ShootingHeadVacuumDetected, error.Input);
            Assert.False(headLowered);
            Assert.Equal(0, shootingBus.StartWrites);
            Assert.Empty(work.GetAssembly(bolt.HeatSink).ShootingBoltResults);
            Assert.False(work.Completed);
        }
        else
        {
            await run;
            Assert.True(headLowered);
            Assert.Equal(1, shootingBus.StartWrites);
            Assert.True(work.Completed);
            var result = Assert.Single(work.GetAssembly(bolt.HeatSink).ShootingBoltResults).Value;
            Assert.Equal(dryRun ? BoltResultSource.DryRun : BoltResultSource.Controller, result.Source);
        }
        Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
    }

    [Theory]
    [InlineData(100, false)]
    [InlineData(0, false)]
    [InlineData(100, true)]
    public async Task ShootingPickupAlignmentRunsAtSafeZAndStopsBeforeTravel(int spinMilliseconds, bool cancel)
    {
        var settings = new BoltFasteningSettings
        {
            ShootingPickupSpinMilliseconds = spinMilliseconds,
            ShootingPreliminaryZ = 12,
            ShootingIpmPreset = 3,
            FirstFasteningHead = FasteningHead.Pickup,
            PickupVacuumDelayMilliseconds = 0,
            MonitorTorqueCurves = false,
            SafeZ = 3,
            PickupPosition = new() { X = 100, Y = 100, Z = 5 },
            PickupHead = new() { FasteningZ = 10 },
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
        };
        var units = new UnitSettings { ShootingBoltFeeder = false };
        var io = new VirtualIoService(Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()), new());
        io.Initialize();
        io.SetInput(InputIo.PickupFeederBoltDetected, true);
        using var motion = new VirtualMotionService(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var shootingBus = new AdcControllerStub();
        using var pickupBus = new AdcControllerStub();
        var shooting = CreateAdcHead(shootingBus, io, FasteningHead.Shooting,
            new() { StatusPollMilliseconds = 10 }, 1, "Shooting", 115200);
        var pickup = CreateAdcHead(pickupBus, io, FasteningHead.Pickup,
            new() { StatusPollMilliseconds = 10 }, 1, "Pickup", 115200);
        var bolt = Bolt(1, FasteningHead.Pickup, 10, 20);
        var work = ConveyorStation.CreateBoltFastening(io);
        var station = new BoltFasteningStation(shooting, pickup, io, motion, new(motion), settings, work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = new() { BoltPoints = [bolt] } } },
            units, new BoltFeederUnit(io, new(), units));
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        long? alignmentStarted = null;
        double? alignmentElapsed = null;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.ShootingHeadVacuumPump)
                io.SetInput(InputIo.ShootingHeadVacuumDetected, on);
            if (output != OutputIo.ShootingBoltStart)
                return;
            if (on && spinMilliseconds > 0 && shootingBus.StartWrites == 1)
            {
                Assert.Equal((100d, 100d, settings.SafeZ), motion.Position);
                Assert.Equal(StationCylinderState.Down, station.ShootingHeadPosition);
                Assert.True(io.GetOutput(OutputIo.ShootingHeadVacuumPump));
                Assert.True(io.GetOutput(OutputIo.ShootingBoltPreset3));
                Assert.False(io.GetOutput(OutputIo.ShootingBoltDirection));
                alignmentStarted = Stopwatch.GetTimestamp();
                if (cancel)
                    stop.CancelAfter(20);
            }
            else if (!on && alignmentStarted is { } started)
            {
                alignmentElapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                alignmentStarted = null;
                Assert.Equal((100d, 100d, settings.SafeZ), motion.Position);
                Assert.False(motion.IsMoving);
                Assert.Empty(work.GetAssembly(HeatSinkSlot.HeatSink1).PickupBoltResults);
            }
            else if (on)
            {
                Assert.Equal(settings.ShootingPreliminaryZ, motion.Position.Z);
                Assert.Equal(bolt.PreliminaryFasteningPosition!.X, motion.Position.X);
                Assert.Empty(work.GetAssembly(HeatSinkSlot.HeatSink1).PickupBoltResults);
            }
        };
        await station.RunAsync(stop.Token, selectedBolts: [bolt.Id]);
        Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
        if (cancel)
        {
            Assert.True(alignmentElapsed >= spinMilliseconds - 5);
            Assert.Equal(1, shootingBus.StartWrites);
            Assert.Equal(0, pickupBus.StartWrites);
            Assert.Empty(work.GetAssembly(HeatSinkSlot.HeatSink1).PickupBoltResults);
            Assert.False(work.Completed);
            return;
        }
        if (spinMilliseconds > 0)
            Assert.True(alignmentElapsed >= spinMilliseconds - 5);
        else
            Assert.Null(alignmentElapsed);
        Assert.True(work.Completed);
        Assert.Equal(spinMilliseconds > 0 ? 2 : 1, shootingBus.StartWrites);
        Assert.Equal(1, pickupBus.StartWrites);
        var result = Assert.Single(work.GetAssembly(HeatSinkSlot.HeatSink1).PickupBoltResults).Value;
        Assert.True(result.Success);
        Assert.True(result.PreliminaryResult!.Success);
        Assert.Equal((ushort)shootingBus.StartWrites, result.PreliminaryResult.Controller!.EventCount);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    public async Task ShootingPreliminaryThenPickupFinalFeedsEachBolt(
        bool earlyTorque, bool stopFeedOnCompletion, bool retighten)
    {
        var settings = new BoltFasteningSettings
        {
            ShootingPickupSpinMilliseconds = 0,
            ShootingPreliminaryZ = 14.5,
            FirstFasteningHead = FasteningHead.Pickup,
            ShootingIpmPreset = 3,
            PickupFinalPreset = 2,
            ShootingPcbPreset = 2,
            PickupVacuumDelayMilliseconds = 0,
            SafeZ = 3,
            PickupPosition = new() { X = 100, Y = 100, Z = 5 },
            PickupHead = new()
            {
                FasteningZ = 10,
                UpperLeftLocatingPin = new() { X = 0, Y = 0 },
                LowerRightLocatingPin = new() { X = 100, Y = 100 },
            },
            ShootingHead = new()
            {
                UpperLeftLocatingPin = new() { X = 20, Y = 20 },
                LowerRightLocatingPin = new() { X = 120, Y = 120 },
            },
            FeedAccelerationSeconds = 0.05,
            FeedDecelerationSeconds = 0.1,
            StopZFeedOnCompletion = stopFeedOnCompletion,
            IpmRetighteningEnabled = retighten,
            Motion = new()
            {
                HorizontalSpeed = 20_000, ZSpeed = 20_000,
                ZAccelerationSeconds = 0.02, ZDecelerationSeconds = 0.03,
            },
        };
        var units = new UnitSettings { ShootingBoltFeeder = false };
        var io = new VirtualIoService(Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()), new());
        io.Initialize();
        io.SetInput(InputIo.PickupFeederBoltDetected, true);
        using var motion = new VirtualMotionService(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var pickupBus = new AdcControllerStub { SuppressCompletion = true };
        using var shootingBus = new AdcControllerStub { SuppressCompletion = true };
        var pickup = CreateAdcHead(pickupBus, io, FasteningHead.Pickup,
            new() { StatusPollMilliseconds = 10 }, 1, "Pickup", 115200);
        var shooting = CreateAdcHead(shootingBus, io, FasteningHead.Shooting,
            new() { StatusPollMilliseconds = 10 }, 1, "Shooting", 115200);
        var bolts = new[]
        {
            new BoltPoint
            {
                Head = FasteningHead.Pickup, X = 10, Y = 20,
                FeedDistance = 2, FeedSeconds = 0.12,
                PreliminaryFasteningZOffset = 0.5,
                PreliminaryFeedDistance = 3, PreliminaryFeedSeconds = 0.18,
            },
            new BoltPoint
            {
                Head = FasteningHead.Pickup, X = 20, Y = 20,
                FeedDistance = 4, FeedSeconds = 0.16,
                PreliminaryFasteningZOffset = 1.5,
                PreliminaryFeedDistance = 5, PreliminaryFeedSeconds = 0.2,
            },
        };
        var work = ConveyorStation.CreateBoltFastening(io);
        var observedMotion = DispatchProxy.Create<IXyMotion, MachineTestSupport.DisplayReadMotion>();
        var motionCalls = (MachineTestSupport.DisplayReadMotion)observedMotion;
        motionCalls.Motion = motion;
        var reference = new CarrierReferenceSettings
        {
            UpperLeftLocatingPin = new() { X = 0, Y = 0 },
            LowerRightLocatingPin = new() { X = 100, Y = 100 },
        };
        foreach (var bolt in bolts)
            settings.InitializeBoltPosition(bolt, reference);
        var station = new BoltFasteningStation(shooting, pickup, io, observedMotion, new(motion), settings, work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = new() { BoltPoints = [.. bolts] } } },
            units, new BoltFeederUnit(io, new(), units));
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        var starts = new List<(int Bolt, FasteningHead Head, BoltFasteningStage? Stage)>();
        var feeds = new List<(int Bolt, double StartZ, double EndZ)>();
        double? feedStartZ = null;
        long feedStartedAt = 0;
        var tableChanges = new List<bool>();
        var pickups = 0;
        var descentCount = 0;
        var shootingHeadChanges = new List<bool>();
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.ShootingHeadDown)
            {
                shootingHeadChanges.Add(on);
                if (!on)
                    Assert.Equal(2, starts.Count);
            }
            if (output == OutputIo.ShootingHeadVacuumPump)
            {
                io.SetInput(InputIo.ShootingHeadVacuumDetected, on);
                if (on)
                {
                    pickups++;
                    Assert.Equal((settings.PickupPosition.X, settings.PickupPosition.Y, settings.PickupPosition.Z), motion.Position);
                    Assert.Equal(StationCylinderState.Down, station.ShootingHeadPosition);
                    Assert.Equal(StationCylinderState.Up, station.PickupTablePosition);
                }
            }
            if (output == OutputIo.PickupTableDown)
                tableChanges.Add(on);
            Assert.False(output == OutputIo.PickupHeadVacuumPump && on);
            if (!on)
                return;
            Assert.NotEqual(OutputIo.ShootBolt, output);
            if (output is OutputIo.PickupHeadDown or OutputIo.ShootingHeadDown)
            {
                descentCount++;
                Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
                Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
            }
            if (output is OutputIo.PickupBoltStart or OutputIo.ShootingBoltStart)
            {
                var isPickup = output == OutputIo.PickupBoltStart;
                var bolt = station.ActiveBolt!;
                Assert.Equal(StationCylinderState.Down, isPickup ? station.PickupHeadPosition : station.ShootingHeadPosition);
                Assert.Equal(isPickup ? StationCylinderState.Down : StationCylinderState.Up, station.PickupTablePosition);
                starts.Add((Array.IndexOf(bolts, bolt) + 1, isPickup ? FasteningHead.Pickup : FasteningHead.Shooting, station.ActiveStage));
                var expectedX = (bolt == bolts[0] ? 10 : 20) + (isPickup ? 0 : 20);
                Assert.Equal((expectedX, isPickup ? 20d : 40d), (motion.Position.X, motion.Position.Y));
                var bus = isPickup ? pickupBus : shootingBus;
                bus.SuppressCompletion = station.ActiveStage != BoltFasteningStage.Retightening;
                var position = settings.GetBoltPosition(bolt, !isPickup);
                if (!isPickup)
                    Assert.Equal(bolt == bolts[0] ? 15 : 16, motion.Position.Z);
                if (station.ActiveStage == BoltFasteningStage.Retightening)
                    position.Z = feeds[^1].EndZ;
                Assert.Equal((position.X, position.Y, position.Z), motion.Position);
            }
        };
        motion.MovingChanged += moving =>
        {
            if (moving && motion.IsMovingHorizontal && station.ActiveBolt is not null)
            {
                Assert.Equal(settings.SafeZ, motion.Position.Z);
                if (station.ActiveStage == BoltFasteningStage.Preliminary)
                    Assert.Equal(StationCylinderState.Down, station.ShootingHeadPosition);
            }
            var pickupRunning = io.GetOutput(OutputIo.PickupBoltStart);
            var shootingRunning = io.GetOutput(OutputIo.ShootingBoltStart);
            if (!pickupRunning && !shootingRunning)
                return;
            var bus = pickupRunning ? pickupBus : shootingBus;
            if (moving)
            {
                Assert.True(bus.Monitor.Sample!.Status!.Running);
                Assert.NotEqual(BoltFasteningStage.Retightening, station.ActiveStage);
                Assert.Equal(MotionAxis.Z, motionCalls.LastMovedAxis);
                Assert.Equal(0.02, settings.Motion.ZAccelerationSeconds);
                Assert.Equal(0.03, settings.Motion.ZDecelerationSeconds);
                Assert.Equal(0.05, motionCalls.LastMoveAccelerationSeconds);
                Assert.Equal(0.1, motionCalls.LastMoveDecelerationSeconds);
                var bolt = station.ActiveBolt!;
                var distance = pickupRunning ? bolt.FeedDistance : bolt.PreliminaryFeedDistance;
                var seconds = pickupRunning ? bolt.FeedSeconds : bolt.PreliminaryFeedSeconds;
                var velocity = motionCalls.LastMoveVelocity!.Value;
                var acceleration = velocity / 0.05;
                var deceleration = velocity / 0.1;
                var peakVelocity = Math.Min(velocity, Math.Sqrt(2 * distance / (1 / acceleration + 1 / deceleration)));
                var movingSeconds = distance / peakVelocity + (peakVelocity / acceleration + peakVelocity / deceleration) / 2;
                Assert.Equal(seconds, movingSeconds, 6);
                feedStartZ = motion.Position.Z;
                feedStartedAt = Stopwatch.GetTimestamp();
                if (earlyTorque)
                    bus.SuppressCompletion = false;
            }
            else if (feedStartZ is { } startZ)
            {
                var bolt = station.ActiveBolt!;
                feeds.Add((Array.IndexOf(bolts, bolt) + 1, startZ, motion.Position.Z));
                var distance = pickupRunning ? bolt.FeedDistance : bolt.PreliminaryFeedDistance;
                if (earlyTorque && stopFeedOnCompletion)
                    Assert.InRange(motion.Position.Z, startZ, startZ + distance - 0.01);
                else
                {
                    Assert.Equal(startZ + distance, motion.Position.Z, 3);
                    var seconds = pickupRunning ? bolt.FeedSeconds : bolt.PreliminaryFeedSeconds;
                    Assert.True(Stopwatch.GetElapsedTime(feedStartedAt).TotalSeconds >= seconds - 0.01);
                }
                bus.SuppressCompletion = false;
                feedStartZ = null;
            }
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var results = new List<BoltResult>();
        await station.RunAsync(timeout.Token, selectedBolts: bolts.Select(bolt => bolt.Id).ToArray(),
            resultReceived: (bolt, result) => results.Add(result));
        Assert.True(work.Completed);
        Assert.Equal(retighten ? new[] { 1, 2, 2, 1, 1 } : [1, 2, 2, 1], starts.Select(row => row.Bolt));
        Assert.Equal(FasteningHead.Shooting, starts[0].Head);
        Assert.Equal(FasteningHead.Shooting, starts[1].Head);
        Assert.All(starts.Skip(2), row => Assert.Equal(FasteningHead.Pickup, row.Head));
        Assert.Equal(3, descentCount);
        Assert.Equal(new[] { true, false }, shootingHeadChanges);
        Assert.Equal(2, pickups);
        Assert.Equal(new[] { true, false }, tableChanges);
        Assert.Equal(4, feeds.Count);
        Assert.Equal(retighten ? 5 : 4, results.Count);
        if (retighten)
        {
            Assert.Equal(BoltFasteningStage.Retightening, results[^1].Stage);
            Assert.Equal(0, results[^1].ZFeedDistance);
            Assert.Same(results[^2], results[^1].PreviousFinalResult);
        }
        Assert.All(results, result =>
        {
            var preliminary = result.Stage == BoltFasteningStage.Preliminary;
            Assert.Equal((ushort)(preliminary ? 3 : 2), result.Controller!.Preset);
            Assert.Equal((ushort)(preliminary ? 100 : 80), result.Controller.TorqueCompensationPercent);
        });
        for (var index = 0; index < feeds.Count; index++)
            Assert.Equal(feeds[index].EndZ - feeds[index].StartZ,
                results[index].ZFeedDistance!.Value, 3);
        Assert.All(work.GetAssembly(HeatSinkSlot.HeatSink1).PickupBoltResults.Values, result =>
        {
            Assert.True(result.IsComplete);
            Assert.Equal(FasteningHead.Pickup, result.ToolHead);
            Assert.Equal(FasteningHead.Shooting, (result.PreliminaryResult ?? result.PreviousFinalResult!.PreliminaryResult)!.ToolHead);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShootingPreliminaryTravelStopsWhenHeadOrTableFeedbackIsLost(bool loseTable)
    {
        var settings = new BoltFasteningSettings
        {
            ShootingPickupSpinMilliseconds = 0,
            FirstFasteningHead = FasteningHead.Pickup,
            ShootingPreliminaryZ = 12, SafeZ = 3,
            PickupPosition = new() { X = 100, Y = 100, Z = 5 },
            PickupVacuumDelayMilliseconds = 0,
            Motion = new() { HorizontalSpeed = 1_000, ZSpeed = 20_000 },
        };
        var units = new UnitSettings { ShootingBoltFeeder = false };
        var io = new VirtualIoService(Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()), new());
        io.Initialize();
        io.SetInput(InputIo.PickupFeederBoltDetected, true);
        using var motion = new VirtualMotionService(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var bus = new AdcControllerStub();
        var head = CreateAdcHead(bus, io, FasteningHead.Shooting,
            new() { StatusPollMilliseconds = 10 }, 1, "Virtual", 115200);
        var bolts = new[] { Bolt(1, FasteningHead.Pickup, 10, 20), Bolt(2, FasteningHead.Pickup, 20, 20) };
        var work = ConveyorStation.CreateBoltFastening(io);
        var station = new BoltFasteningStation(head, head, io, motion, new(motion), settings, work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = new() { BoltPoints = [.. bolts] } } },
            units, new BoltFeederUnit(io, new(), units));
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        var pickups = 0;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.ShootingHeadVacuumPump)
            {
                io.SetInput(InputIo.ShootingHeadVacuumDetected, on);
                if (on)
                    pickups++;
            }
        };
        var lost = false;
        motion.PositionChanged += (x, y, z) =>
        {
            if (lost || pickups != 2 || !motion.IsMovingHorizontal)
                return;
            lost = true;
            Assert.Equal(settings.SafeZ, z);
            Assert.Equal(StationCylinderState.Down, station.ShootingHeadPosition);
            if (loseTable)
                io.SetInputs((InputIo.PickupTableUp, false), (InputIo.PickupTableDown, false));
            else
                io.SetInputs((InputIo.ShootingHeadUp, false), (InputIo.ShootingHeadDown, false));
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<MotionInterlockException>(() => station.RunAsync(timeout.Token));
        Assert.True(lost);
        Assert.False(motion.IsMoving);
        Assert.False(station.IsHorizontalMoveAllowed);
        Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
        Assert.Equal(1, bus.StartWrites);
        var results = work.GetAssembly(HeatSinkSlot.HeatSink1).PickupBoltResults;
        Assert.Equal(BoltFasteningStage.Preliminary, results[bolts[0].Id].Stage);
        Assert.False(results.ContainsKey(bolts[1].Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdcTorqueMonitoringCompletesCarrierWithoutWritingMdcEnableRegister(bool earlierGraphError)
    {
        var settings = new BoltFasteningSettings
        {
            ShootingPickupSpinMilliseconds = 0,
            ShootingPreliminaryZ = 12,
            PickupVacuumDelayMilliseconds = 0,
            SafeZ = 0, PickupPosition = new() { X = 100, Y = 100, Z = 5 },
            PickupHead = new() { FasteningZ = 10 },
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
            TorqueCurveSampling = earlierGraphError ? AdcTorqueCurveSampling.Milliseconds10 : AdcTorqueCurveSampling.Milliseconds30,
        };
        var units = new UnitSettings { ShootingBoltFeeder = false };
        var io = new VirtualIoService(Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()), new());
        io.Initialize();
        io.SetInput(InputIo.PickupFeederBoltDetected, true);
        using var motion = new VirtualMotionService(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        var rejection = new AdcResponseException(2, "HComm error 0x02; RX=018602C3A1.");
        using var bus = new AdcControllerStub { RegisterWriteFailure = (4100, 0, rejection) };
        var head = CreateAdcHead(bus, io, FasteningHead.Shooting, new() { StatusPollMilliseconds = 10 }, 1, "Virtual", 115200);
        var work = ConveyorStation.CreateBoltFastening(io);
        var bolt = new BoltPoint { Head = FasteningHead.Pickup, FasteningX = 10, FasteningY = 10, PreliminaryFasteningPosition = new() { X = 30, Y = 20 } };
        var station = new BoltFasteningStation(head, head, io, motion, new(motion), settings, work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = new() { BoltPoints = [bolt] } } },
            units, new BoltFeederUnit(io, new(), units));
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.ShootingHeadVacuumPump)
                io.SetInput(InputIo.ShootingHeadVacuumDetected, on);
            if (earlierGraphError && output == OutputIo.ShootingBoltStart && !on && bus.StartWrites > 0)
                bus.Monitor.ReceiveTorqueCurve(null, "Previous graph block was incomplete.");
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await station.RunAsync(timeout.Token, selectedBolts: [bolt.Id]);
        Assert.True(work.Completed);
        Assert.Equal(2, bus.StartWrites);
        Assert.Equal(8, bus.RegisterWrites.Count); // This test shares one stub between both heads.
        Assert.All(bus.RegisterWrites.Chunk(4), writes =>
            Assert.Equal(new (ushort, ushort)[] { (4101, 1), (4102, 4), (4103, (ushort)settings.TorqueCurveSampling), (4104, 1) }, writes));
        Assert.True(bus.GraphRequests >= 2);
        Assert.False(bus.Monitor.IsTorqueCurveMonitoringRequested);
        var result = work.GetAssembly(bolt.HeatSink).PickupBoltResults[bolt.Id];
        Assert.True(result.Success);
        Assert.True(result.IsComplete);
        Assert.NotNull(result.TorqueCurve);
        Assert.Null(result.TorqueCurveError);
        Assert.NotNull(result.PreliminaryResult!.TorqueCurve);
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public async Task AllShootingPreliminaryStagesPrecedeReversePickupFinalsAndResumeWithoutAnotherPickup(
        bool firstNg, bool resume, bool missingCurve, bool pcbFeeding)
    {
        var settings = new BoltFasteningSettings
        {
            ShootingPickupSpinMilliseconds = 0,
            ShootingPreliminaryZ = 12,
            FirstFasteningHead = FasteningHead.Pickup,
            IpmRetighteningEnabled = true,
            PickupFinalPreset = 2, ShootingIpmPreset = 3,
            ShootingPcbPreset = 2,
            ShootingArrivalDelaySeconds = 0,
            TorqueCurveTimeoutMilliseconds = missingCurve ? 50 : 1_000,
            PickupVacuumDelayMilliseconds = 0,
            DryRunMilliseconds = 10,
            SafeZ = 0, PickupPosition = new() { X = 100, Y = 100, Z = 5 },
            PickupHead = new() { FasteningZ = 10 },
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
        };
        var units = new UnitSettings { ShootingBoltFeeder = pcbFeeding };
        var io = new VirtualIoService(Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()), new());
        io.Initialize();
        io.SetInput(InputIo.PickupFeederBoltDetected, true);
        io.SetInput(InputIo.ShootingFeederBoltDetected, true);
        using var motion = new VirtualMotionService(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var bus = new AdcControllerStub { SuppressTorqueCurve = missingCurve };
        using var shootingBus = new AdcControllerStub { SuppressTorqueCurve = missingCurve };
        shootingBus.TorqueCompensations[2] = 70;
        var log = new ApplicationLog();
        using var factory = log.CreateLoggerFactory();
        bus.BindIo(io, FasteningHead.Pickup);
        shootingBus.BindIo(io, FasteningHead.Shooting);
        var head = new AdcBoltHead(bus, io, FasteningHead.Pickup,
            new() { StatusPollMilliseconds = 10 }, 1, "Virtual", 115200, factory.CreateLogger<AdcBoltHead>());
        var shootingHead = new AdcBoltHead(shootingBus, io, FasteningHead.Shooting,
            new(), 2, "Virtual", 115200, factory.CreateLogger<AdcBoltHead>());
        var bolts = Enumerable.Range(1, 4).Select(number => new BoltPoint
        {
            Id = BoltId(number), Head = FasteningHead.Pickup,
            HeatSink = number <= 2 ? HeatSinkSlot.HeatSink1 : HeatSinkSlot.HeatSink2,
            FasteningX = number * 10, FasteningY = 10, PreliminaryFasteningPosition = new() { X = number * 10 + 100, Y = 20 },
        }).ToArray();
        var work = ConveyorStation.CreateBoltFastening(io);
        var shootingBolts = Enumerable.Range(1, pcbFeeding ? 4 : 1).Select(number => new BoltPoint
        {
            Head = FasteningHead.Shooting,
            HeatSink = number <= 2 ? HeatSinkSlot.HeatSink1 : HeatSinkSlot.HeatSink2,
            FasteningX = 60 + number * 10, FasteningY = 10,
        }).ToArray();
        var station = new BoltFasteningStation(shootingHead, head, io, motion, new(motion), settings, work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = new() { BoltPoints = [.. shootingBolts, .. bolts] } } },
            units, new BoltFeederUnit(io, new(), units));
        io.SetInputs((InputIo.BoltFasteningHeatSink1Present, true), (InputIo.BoltFasteningHeatSink2Present, true));
        await work.SeatAsync(CancellationToken.None);
        var starts = new List<(int Bolt, ushort Preset)>();
        var pcbStarts = new List<Guid>();
        var headOrder = new List<FasteningHead>();
        var pickups = 0;
        var loweredHeads = 0;
        var tableLowerings = 0;
        var expectedCompensationReads = 1;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PickupTableDown && on)
            {
                tableLowerings++;
                Assert.Equal(4, starts.Count);
                Assert.All(headOrder, head => Assert.Equal(FasteningHead.Shooting, head));
            }
            if (output == OutputIo.ShootBolt && on)
            {
                io.SetInput(InputIo.ShootingTubeBoltDetected, true);
                io.SetInput(InputIo.ShootingTubeBoltDetected, false);
                io.SetInput(InputIo.ShootingHeadVacuumDetected, true);
            }
            if (output is OutputIo.PickupHeadDown or OutputIo.ShootingHeadDown)
            {
                if (on)
                {
                    Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
                    Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
                    loweredHeads++;
                }
            }
            if (output == OutputIo.ShootingHeadVacuumPump && !io.GetOutput(OutputIo.ShootBolt))
            {
                io.SetInput(InputIo.ShootingHeadVacuumDetected, on);
                if (on && station.ActiveStage == BoltFasteningStage.Preliminary)
                    pickups++;
            }
            if (!on || output is not (OutputIo.PickupBoltStart or OutputIo.ShootingBoltStart))
                return;
            var tool = output == OutputIo.PickupBoltStart ? FasteningHead.Pickup : FasteningHead.Shooting;
            headOrder.Add(tool);
            Assert.Equal(StationCylinderState.Down, tool == FasteningHead.Pickup ? station.PickupHeadPosition : station.ShootingHeadPosition);
            Assert.Equal(tool == FasteningHead.Pickup ? StationCylinderState.Down : StationCylinderState.Up, station.PickupTablePosition);
            if (station.ActiveBolt!.Head == FasteningHead.Shooting)
            {
                pcbStarts.Add(station.ActiveBolt.Id);
                Assert.True(io.GetOutput(OutputIo.ShootingBoltPreset2));
                Assert.False(io.GetOutput(OutputIo.ShootingBoltPreset3));
                return;
            }
            var preset = tool == FasteningHead.Pickup ? settings.PickupFinalPreset : settings.ShootingIpmPreset;
            var controller = tool == FasteningHead.Pickup ? bus : shootingBus;
            controller.RunReplies.Enqueue(true);
            controller.RunReplies.Enqueue(true);
            controller.RunReplies.Enqueue(true);
            controller.RunReplies.Enqueue(false);
            starts.Add((Array.IndexOf(bolts, station.ActiveBolt) + 1, preset));
            controller.ResultStatus = firstNg && starts.Count == 1 ? AdcEventStatus.FasteningNg : AdcEventStatus.FasteningOk;
            Assert.Equal(expectedCompensationReads * (pcbFeeding && tool == FasteningHead.Shooting ? 2 : 1),
                controller.CompensationReads);
            bus.TorqueCompensations[2] = 85;
            shootingBus.TorqueCompensations[3] = 95;
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        BoltResult? beforeStop = null;
        if (resume)
        {
            await station.RunAsync(stop.Token, resultReceived: (bolt, result) =>
            {
                if (bolt.Id == bolts[^1].Id && result.Stage == BoltFasteningStage.Preliminary)
                    stop.Cancel();
            });
            Assert.False(work.Completed);
            Assert.Equal(4, starts.Count);
            Assert.All(starts, item => Assert.Equal((ushort)3, item.Preset));
            beforeStop = work.GetAssembly(HeatSinkSlot.HeatSink1).PickupBoltResults[bolts[0].Id];
            Assert.Equal(BoltFasteningStage.Preliminary, beforeStop.Stage);
            Assert.False(beforeStop.IsComplete);
            Assert.Equal((ushort)100, beforeStop.Controller!.TorqueCompensationPercent);
            Assert.False(station.IsFasteningRecorded);
            Assert.Equal("Final tightening pending",
                new FasteningResumeRow(bolts[0].Id, "Bolt", HeatSinkSlot.HeatSink1, beforeStop).Status);
        }
        if (resume)
            expectedCompensationReads++;
        var shootingGraphWritesBeforeResume = shootingBus.RegisterWrites.Count;
        var shootingGraphRequestsBeforeResume = shootingBus.GraphRequests;
        using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var remaining = resume ? bolts.Where(bolt => work.GetAssembly(bolt.HeatSink).PickupBoltResults
            .GetValueOrDefault(bolt.Id) is not { IsComplete: true }).Select(bolt => bolt.Id)
            .Concat(shootingBolts.Select(bolt => bolt.Id)).ToArray() : null;
        var run = station.RunAsync(finish.Token, selectedBolts: remaining, resultReceived: (bolt, result) =>
        {
            if (bolt.Head == FasteningHead.Shooting)
            {
                if (pcbFeeding)
                {
                    Assert.Equal((ushort)2, result.Controller!.Preset);
                    Assert.Equal((ushort)70, result.Controller.TorqueCompensationPercent);
                    Assert.True(result.Success);
                }
                else
                    Assert.Null(result.Controller);
            }
            else
            {
                var expected = result.Controller!.Preset == 2 ? (resume ? 85 : 80) : (resume ? 95 : 100);
                Assert.Equal((ushort)expected, result.Controller.TorqueCompensationPercent);
                Assert.Equal(1, result.Torque);
                if (result.Stage == BoltFasteningStage.Final)
                    Assert.Equal(settings.SafeZ, motion.Position.Z);
            }
        });
        try
        {
            Assert.True(await WaitUntilAsync(() => work.Completed || run.IsCompleted, TimeSpan.FromSeconds(20)));
            Assert.True(work.Completed, run.Exception?.ToString() ?? station.NextStep.ToString());
            Assert.Equal(expectedCompensationReads, bus.CompensationReads);
            Assert.Equal(expectedCompensationReads * (pcbFeeding ? 2 : 1), shootingBus.CompensationReads);
            if (resume)
            {
                if (!pcbFeeding)
                {
                    Assert.Equal(shootingGraphWritesBeforeResume, shootingBus.RegisterWrites.Count);
                    Assert.Equal(shootingGraphRequestsBeforeResume, shootingBus.GraphRequests);
                }
            }
            (int, ushort)[] expected = [(1, 3), (2, 3), (3, 3), (4, 3), (4, 2), (3, 2), (3, 2), (2, 2), (1, 2), (1, 2)];
            if (firstNg)
                expected = expected.Where(item => item != (1, (ushort)2)).ToArray();
            Assert.Equal(expected, starts);
            Assert.Equal(shootingBolts.Select(bolt => bolt.Id), pcbStarts);
            Assert.Equal(expected.Select(item => item.Item2 == 2 ? FasteningHead.Pickup : FasteningHead.Shooting)
                .Concat(shootingBolts.Select(bolt => bolt.Head)), headOrder);
            Assert.Equal(4, pickups);
            Assert.Equal(1, tableLowerings);
            var stageResults = bolts.SelectMany(bolt => new PcbBoltResultView(bolt.Id, null, bolt.Head,
                work.GetAssembly(bolt.HeatSink).PickupBoltResults[bolt.Id], true, null).StageResults).ToArray();
            Assert.Equal(expected.Length, stageResults.Length);
            Assert.All(stageResults.Where(row => row.Result.ToolHead == FasteningHead.Pickup), row =>
            {
                if (missingCurve)
                {
                    Assert.Null(row.Result.TorqueCurve);
                    Assert.NotNull(row.Result.TorqueCurveError);
                }
                else
                {
                    Assert.NotNull(row.Result.TorqueCurve);
                    Assert.Null(row.Result.TorqueCurveError);
                    Assert.Equal(row.Result.Controller!.ScrewCount, row.Result.TorqueCurve.ScrewCount);
                }
            });
            Assert.False(head.Monitor.IsTorqueCurveMonitoringRequested);
            Assert.True(station.IsFasteningRecorded);
            foreach (var bolt in bolts)
            {
                var result = work.GetAssembly(bolt.HeatSink).PickupBoltResults[bolt.Id];
                Assert.True(result.IsComplete);
                if (firstNg && bolt.Id == bolts[0].Id)
                {
                    Assert.False(result.Success);
                    Assert.Equal(BoltFasteningStage.Preliminary, result.Stage);
                    Assert.Equal(AssemblyResult.Ng, work.GetAssembly(bolt.HeatSink).FasteningResult);
                }
                else
                {
                    var retightened = bolt == bolts[0] || bolt == bolts[2];
                    Assert.Equal(retightened ? BoltFasteningStage.Retightening : BoltFasteningStage.Final, result.Stage);
                    var preliminary = result.PreliminaryResult ?? result.PreviousFinalResult!.PreliminaryResult;
                    Assert.Equal((ushort)3, preliminary!.Controller!.Preset);
                    Assert.Equal((ushort)2, result.Controller!.Preset);
                    Assert.Equal(FasteningHead.Pickup, result.ToolHead);
                    Assert.Equal(FasteningHead.Shooting, preliminary.ToolHead);
                    Assert.Equal(retightened, result.PreviousFinalResult is not null);
                }
            }
            Assert.Equal(headOrder.Count - 3 - (firstNg ? 1 : 2), loweredHeads);
        }
        finally
        {
            finish.Cancel();
            await run;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task IpmRetighteningResumesOnlyPendingPassAndPreservesFinalNg(bool firstFinalNg, bool interruptAdditional)
    {
        var settings = new BoltFasteningSettings
        {
            ShootingPickupSpinMilliseconds = 0,
            ShootingPreliminaryZ = 12,
            FirstFasteningHead = FasteningHead.Pickup,
            PickupFinalPreset = 2, ShootingIpmPreset = 3,
            IpmRetighteningEnabled = true,
            PickupVacuumDelayMilliseconds = 0,
            PickupPosition = new() { X = 100, Y = 100, Z = 5 },
            PickupHead = new() { FasteningZ = 10 },
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
        };
        var units = new UnitSettings { ShootingBoltFeeder = false };
        var io = new VirtualIoService(Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()), new());
        io.Initialize();
        io.SetInput(InputIo.PickupFeederBoltDetected, true);
        using var motion = new VirtualMotionService(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var bus = new AdcControllerStub();
        using var shootingBus = new AdcControllerStub();
        var shootingHead = CreateAdcHead(shootingBus, io, FasteningHead.Shooting, new() { StatusPollMilliseconds = 10 }, 1, "Shooting", 115200);
        var head = CreateAdcHead(bus, io, FasteningHead.Pickup, new() { StatusPollMilliseconds = 10 }, 1, "Virtual", 115200);
        var bolts = new[]
        {
            new BoltPoint { Head = FasteningHead.Pickup, FasteningX = 10, FasteningY = 10, PreliminaryFasteningPosition = new() { X = 30, Y = 20 } },
            new BoltPoint { Head = FasteningHead.Pickup, FasteningX = 20, FasteningY = 10, PreliminaryFasteningPosition = new() { X = 40, Y = 20 } },
        };
        var work = ConveyorStation.CreateBoltFastening(io);
        var station = new BoltFasteningStation(shootingHead, head, io, motion, new(motion), settings, work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = new() { BoltPoints = [.. bolts] } } },
            units, new BoltFeederUnit(io, new(), units));
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        var starts = new List<BoltFasteningStage?>();
        var pickups = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.ShootingHeadVacuumPump)
            {
                io.SetInput(InputIo.ShootingHeadVacuumDetected, on);
                if (on)
                    pickups++;
            }
            if (output is OutputIo.PickupBoltStart or OutputIo.ShootingBoltStart && on)
            {
                var controller = output == OutputIo.PickupBoltStart ? bus : shootingBus;
                controller.RunReplies.Enqueue(true);
                controller.RunReplies.Enqueue(true);
                controller.RunReplies.Enqueue(true);
                controller.RunReplies.Enqueue(false);
                starts.Add(station.ActiveStage);
                if (station.ActiveStage == BoltFasteningStage.Retightening)
                {
                    if (interruptAdditional)
                        stop.Cancel();
                    else
                        Assert.Equal(12.5, motion.Position.Z);
                }
                bus.ResultStatus = firstFinalNg && station.ActiveStage == BoltFasteningStage.FinalBeforeRetightening
                    ? AdcEventStatus.FasteningNg : AdcEventStatus.FasteningOk;
            }
        };
        await station.RunAsync(stop.Token, selectedBolts: bolts.Select(bolt => bolt.Id).ToArray(), resultReceived: (bolt, result) =>
        {
            if (!firstFinalNg && !interruptAdditional && result.Stage == BoltFasteningStage.FinalBeforeRetightening)
                stop.Cancel();
        });
        var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        var firstFinal = assembly.PickupBoltResults[bolts[0].Id];
        if (interruptAdditional)
        {
            Assert.Equal(5, starts.Count);
            Assert.Equal(BoltResultSource.Interrupted, firstFinal.Source);
            Assert.Equal(BoltFasteningStage.Retightening, firstFinal.Stage);
            Assert.Equal(0, firstFinal.ZFeedDistance);
            Assert.NotNull(firstFinal.PreviousFinalResult!.TorqueCurve);
            Assert.NotNull(firstFinal.PreviousFinalResult.PreliminaryResult!.TorqueCurve);
            Assert.False(work.Completed);
            Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
            Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
            await Assert.ThrowsAsync<InvalidOperationException>(() => station.RunAsync(selectedBolts: [bolts[0].Id]));
            return;
        }
        Assert.Equal(4, starts.Count);
        Assert.Equal(2, pickups);
        Assert.Equal(BoltFasteningStage.FinalBeforeRetightening, firstFinal.Stage);
        Assert.Equal(firstFinalNg, firstFinal.IsComplete);
        Assert.Equal(firstFinalNg, work.Completed);
        if (firstFinalNg)
        {
            Assert.False(firstFinal.Success);
            Assert.Equal(AssemblyResult.Ng, assembly.FasteningResult);
            return;
        }
        // The pending result still owes only the extra pass.
        firstFinal = firstFinal with { ZFeedDistance = 2.5 };
        assembly.RecordBolt(FasteningHead.Pickup, bolts[0].Id, firstFinal);
        Assert.Equal("Retightening pending",
            new FasteningResumeRow(bolts[0].Id, "Bolt", HeatSinkSlot.HeatSink1, firstFinal).Status);
        // Changing the option does not replay or discard the already-pending pass.
        settings.IpmRetighteningEnabled = false;
        using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await station.RunAsync(finish.Token, selectedBolts: [bolts[0].Id]);
        var result = assembly.PickupBoltResults[bolts[0].Id];
        Assert.Equal(5, starts.Count);
        Assert.Equal(BoltFasteningStage.Retightening, starts[^1]);
        Assert.Equal(0, result.ZFeedDistance);
        Assert.Equal(2, pickups);
        Assert.Same(firstFinal, result.PreviousFinalResult);
        Assert.NotNull(result.PreviousFinalResult!.TorqueCurve);
        Assert.NotNull(result.PreviousFinalResult.PreliminaryResult!.TorqueCurve);
        Assert.NotNull(result.TorqueCurve);
        Assert.Equal((ushort)2, result.Controller!.Preset);
        Assert.True(result.IsComplete);
        Assert.True(work.Completed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PickupFinalFeedbackLossPreservesPreliminary(bool duringRun, bool losePickupHead)
    {
        var settings = new BoltFasteningSettings
        {
            ShootingPickupSpinMilliseconds = 0,
            ShootingPreliminaryZ = 12,
            FirstFasteningHead = FasteningHead.Pickup, PickupFinalPreset = 3,
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
        };
        var io = new VirtualIoService(Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()), new());
        io.Initialize();
        using var motion = new VirtualMotionService(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var pickupBus = new AdcControllerStub { SuppressCompletion = true };
        using var shootingBus = new AdcControllerStub { SuppressCompletion = true };
        var pickup = CreateAdcHead(pickupBus, io, FasteningHead.Pickup, new(), 1, "Pickup", 115200);
        var shooting = CreateAdcHead(shootingBus, io, FasteningHead.Shooting, new(), 1, "Shooting", 115200);
        var bolt = new BoltPoint
        {
            Head = FasteningHead.Pickup, FasteningX = 10, FasteningY = 10,
            PreliminaryFasteningPosition = new() { X = 30, Y = 20 },
        };
        var work = ConveyorStation.CreateBoltFastening(io);
        var station = new BoltFasteningStation(shooting, pickup, io, motion, new(motion), settings, work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = new() { BoltPoints = [bolt] } } },
            new(), new BoltFeederUnit(io, new(), new()));
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        var preliminary = new BoltResult(true, 1) { Stage = BoltFasteningStage.Preliminary };
        assembly.RecordBolt(FasteningHead.Pickup, bolt.Id, preliminary);
        io.OutputChanged += (output, on) =>
        {
            if (on && output == (duringRun ? OutputIo.PickupBoltStart : OutputIo.PickupBoltPreset3))
            {
                if (losePickupHead)
                    io.SetInputs((InputIo.ShootingHeadUp, false), (InputIo.ShootingHeadDown, true));
                else
                    io.SetInputs((InputIo.PickupTableDown, false), (InputIo.PickupTableUp, true));
            }
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<MotionInterlockException>(() => station.RunAsync(timeout.Token, selectedBolts: [bolt.Id]));
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.Equal(duringRun ? 1 : 0, pickupBus.StartWrites);
        Assert.Equal(0, shootingBus.StartWrites);
        var result = assembly.PickupBoltResults[bolt.Id];
        if (duringRun)
        {
            Assert.Equal(BoltResultSource.Interrupted, result.Source);
            Assert.Same(preliminary, result.PreliminaryResult);
        }
        else
            Assert.Same(preliminary, result);
        Assert.False(work.Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalResumeVacuumReleaseFailureKeepsPreliminaryAndDoesNotStartMotor(bool cancel)
    {
        var settings = new BoltFasteningSettings
        {
            ShootingPickupSpinMilliseconds = 0,
            ShootingPreliminaryZ = 12,
            FirstFasteningHead = FasteningHead.Pickup,
            ShootingIpmPreset = 3,
            SafeZ = 0, PickupPosition = new() { X = 100, Y = 100, Z = 5 },
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
        };
        var units = new UnitSettings { ShootingBoltFeeder = false };
        var io = new VirtualIoService(Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()),
            new() { TimeoutMilliseconds = 1_000 });
        io.Initialize();
        using var motion = new VirtualMotionService(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var bus = new AdcControllerStub();
        var head = CreateAdcHead(bus, io, FasteningHead.Pickup, new() { StatusPollMilliseconds = 10 }, 1, "Virtual", 115200);
        var work = ConveyorStation.CreateBoltFastening(io);
        var bolt = new BoltPoint { Head = FasteningHead.Pickup, FasteningX = 10, FasteningY = 10, PreliminaryFasteningPosition = new() { X = 30, Y = 20 } };
        var station = new BoltFasteningStation(head, head, io, motion, new(motion), settings, work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = new() { BoltPoints = [bolt] } } },
            units, new BoltFeederUnit(io, new(), units));
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        var preliminary = new BoltResult(true, 1) { Stage = BoltFasteningStage.Preliminary };
        assembly.RecordBolt(FasteningHead.Pickup, bolt.Id, preliminary);
        io.SetOutput(OutputIo.PickupHeadVacuumPump, true);
        io.SetInput(InputIo.PickupHeadVacuumDetected, true);
        var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PickupHeadVacuumPump)
            {
                Assert.False(on); // Final resume never picks another bolt or re-enables vacuum.
                preparing.TrySetResult();
            }
            if (output == OutputIo.ShootingHeadDown && on)
            {
                Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
            }
            if (output == OutputIo.PickupBoltStart && on)
            {
                Assert.False(io.GetOutput(OutputIo.PickupHeadVacuumPump));
                Assert.False(io.GetInput(InputIo.PickupHeadVacuumDetected));
                Assert.Equal(StationCylinderState.Down, station.PickupHeadPosition);
                Assert.Equal(StationCylinderState.Up, station.ShootingHeadPosition);
            }
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = station.RunAsync(stop.Token, selectedBolts: [bolt.Id]);
        await preparing.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(run.IsCompleted);
        Assert.Equal(0, bus.StartWrites);
        if (cancel)
        {
            stop.Cancel();
            await run;
        }
        else
        {
            var failure = await Assert.ThrowsAsync<IoTimeoutException>(() => run);
            Assert.Equal(InputIo.PickupHeadVacuumDetected, failure.Input);
        }
        Assert.Equal(0, bus.StartWrites);
        Assert.Equal(0, bus.ResultReads);
        Assert.False(io.GetOutput(OutputIo.PickupHeadVacuumPump));
        Assert.Same(preliminary, assembly.PickupBoltResults[bolt.Id]);
        Assert.False(work.Completed);

        io.SetInput(InputIo.PickupHeadVacuumDetected, false);
        using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await station.RunAsync(finish.Token, selectedBolts: [bolt.Id]);
        var result = assembly.PickupBoltResults[bolt.Id];
        Assert.Equal(BoltFasteningStage.Final, result.Stage);
        Assert.Same(preliminary, result.PreliminaryResult);
        Assert.True(result.Success);
        Assert.True(work.Completed);
        Assert.Equal(1, bus.StartWrites);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCanceledRunCompensationReadCannotStartFastening(bool cancel)
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new System.IO.IOException("Compensation read failed.");
        using var bus = new AdcControllerStub
        {
            CompensationReadFailure = cancel ? null : failure,
            CompensationReadBarrier = cancel ? barrier.Task : null,
        };
        var settings = new BoltFasteningSettings { ShootingPreliminaryZ = 12, FirstFasteningHead = FasteningHead.Pickup };
        var units = new UnitSettings { ShootingBoltFeeder = false };
        var io = new VirtualIoService(Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()), new());
        io.Initialize();
        using var motion = new VirtualMotionService(settings.Motion, new());
        var head = CreateAdcHead(bus, io, FasteningHead.Pickup, new(), 1, "Virtual", 115200);
        var work = ConveyorStation.CreateBoltFastening(io);
        var bolt = new BoltPoint { Head = FasteningHead.Pickup, FasteningX = 10, FasteningY = 10, PreliminaryFasteningPosition = new() { X = 30, Y = 20 } };
        var station = new BoltFasteningStation(head, head, io, motion, new(motion), settings, work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = new() { BoltPoints = [bolt] } } },
            units, new BoltFeederUnit(io, new(), units));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var run = station.RunAsync(stop.Token);
        if (cancel)
        {
            Assert.True(await WaitUntilAsync(() => bus.CompensationReads == 1, TimeSpan.FromSeconds(2)));
            stop.Cancel();
            await run;
        }
        else
            Assert.Same(failure, await Assert.ThrowsAsync<System.IO.IOException>(() => run));
        Assert.Equal(1, bus.CompensationReads);
        Assert.Equal(0, bus.StartWrites);
        Assert.Equal(0, bus.EventReads);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.Empty(work.Assemblies);
    }

    [Fact]
    public async Task PickupStageSettingsAndCombinedTurnsSurviveStorage()
    {
        // Old mode switches are ignored, and no longer appear when settings are saved.
        var settings = System.Text.Json.JsonSerializer.Deserialize<BoltFasteningSettings>(
            """{"PickupFasteningMode":0,"PickupFirstBoltRetighteningEnabled":false,"PickupFinalHeadCycleEnabled":true,"HeadDownDelayMilliseconds":5000}""")!;
        var savedSettings = System.Text.Json.JsonSerializer.Serialize(settings);
        Assert.DoesNotContain("PickupFasteningMode", savedSettings);
        Assert.DoesNotContain("PickupFirstBoltRetighteningEnabled", savedSettings);
        Assert.DoesNotContain("PickupFinalHeadCycleEnabled", savedSettings);
        Assert.DoesNotContain("HeadDownDelayMilliseconds", savedSettings);
        Assert.Equal(FasteningHead.Shooting, settings.FirstFasteningHead);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.FirstFasteningHead = (FasteningHead)99);
        settings.FirstFasteningHead = FasteningHead.Pickup;
        Assert.Equal((ushort)2, settings.ShootingIpmPreset);
        Assert.Equal((ushort)1, settings.ShootingPcbPreset);
        var previous = System.Text.Json.JsonSerializer.Deserialize<BoltFasteningSettings>("""{"PickupFinalPreset":2}""")!;
        Assert.Equal((ushort)2, previous.ShootingIpmPreset);
        Assert.Equal((ushort)1, previous.ShootingPcbPreset);
        Assert.False(settings.IpmRetighteningEnabled);
        Assert.True(settings.MonitorTorqueCurves);
        Assert.Equal(AdcTorqueCurveSampling.Milliseconds30, settings.TorqueCurveSampling);
        Assert.Equal(1_000, settings.TorqueCurveTimeoutMilliseconds);
        Assert.Equal(3_000, settings.TorqueCurveStopTimeoutMilliseconds);
        Assert.Equal(100, settings.ShootingPickupSpinMilliseconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.ShootingPickupSpinMilliseconds = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.PickupFinalPreset = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.ShootingIpmPreset = 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.ShootingPcbPreset = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.ShootingPcbPreset = 4);
        settings.PickupFinalPreset = 1;
        settings.ShootingIpmPreset = 3;
        settings.ShootingPcbPreset = 2;
        settings.IpmRetighteningEnabled = true;
        settings.MonitorTorqueCurves = false;
        settings.TorqueCurveSampling = AdcTorqueCurveSampling.Milliseconds15;
        settings.TorqueCurveTimeoutMilliseconds = 2_000;
        settings.TorqueCurveStopTimeoutMilliseconds = 5_000;
        settings.ShootingPickupSpinMilliseconds = 125;
        var store = OpenMachineStore();
        await store.SaveSettingsAsync([settings]);
        var loaded = (await MachineSettings.LoadAsync(store)).BoltFastening;
        Assert.Equal(FasteningHead.Pickup, loaded.FirstFasteningHead);
        Assert.Equal(settings.PickupFinalPreset, loaded.PickupFinalPreset);
        Assert.Equal(settings.ShootingIpmPreset, loaded.ShootingIpmPreset);
        Assert.Equal(settings.ShootingPcbPreset, loaded.ShootingPcbPreset);
        Assert.True(loaded.IpmRetighteningEnabled);
        Assert.False(loaded.MonitorTorqueCurves);
        Assert.Equal(AdcTorqueCurveSampling.Milliseconds15, loaded.TorqueCurveSampling);
        Assert.Equal(2_000, loaded.TorqueCurveTimeoutMilliseconds);
        Assert.Equal(5_000, loaded.TorqueCurveStopTimeoutMilliseconds);
        Assert.Equal(125, loaded.ShootingPickupSpinMilliseconds);
        settings.FirstFasteningHead = FasteningHead.Shooting;
        await store.SaveSettingsAsync([settings]);
        loaded = (await MachineSettings.LoadAsync(store)).BoltFastening;
        Assert.Equal(FasteningHead.Shooting, loaded.FirstFasteningHead);
        Assert.Equal((ushort)3, loaded.ShootingIpmPreset);
        var preliminary = new BoltResult(true, 2)
        {
            Stage = BoltFasteningStage.Preliminary,
            Controller = new("Virtual", 1, 1, 100, 1, 2, 100, 0, 0, 3600, 1, 0, 0, 1, 0, null)
            {
                TorqueCompensationPercent = 80,
            },
        };
        var final = preliminary with
        {
            Stage = BoltFasteningStage.Final, PreliminaryResult = preliminary,
            Controller = preliminary.Controller with { Preset = 3, Angle3 = 720, TorqueCompensationPercent = 100 },
            MinimumTurns = 11, MaximumTurns = 13,
        };
        var restored = System.Text.Json.JsonSerializer.Deserialize<BoltResult>(System.Text.Json.JsonSerializer.Serialize(final))!;
        Assert.Equal(10, restored.PreliminaryResult!.MeasuredTurns);
        Assert.Equal((ushort)80, restored.PreliminaryResult.Controller!.TorqueCompensationPercent);
        Assert.Equal((ushort)100, restored.Controller!.TorqueCompensationPercent);
        Assert.Equal(2, restored.MeasuredTurns);
        Assert.Equal(12, restored.TotalTurns);
        Assert.Equal(AssemblyResult.Ok, restored.TurnsResult);
        Assert.Equal(AssemblyResult.Ng, (restored with { MaximumTurns = 11 }).TurnsResult);
        Assert.Equal(AssemblyResult.Ng, (restored with { MinimumTurns = 13 }).TurnsResult);
        Assert.Null((restored with { PreliminaryResult = preliminary with { Controller = null } }).TotalTurns);

        var firstFinal = restored with { Stage = BoltFasteningStage.FinalBeforeRetightening, MinimumTurns = 13 };
        Assert.False((preliminary with { Source = BoltResultSource.DryRun }).IsComplete);
        Assert.False(firstFinal.IsComplete);
        Assert.False((firstFinal with { Source = BoltResultSource.DryRun }).IsComplete);
        Assert.True((firstFinal with { Success = false }).IsComplete);
        Assert.Equal(AssemblyResult.Pending, firstFinal.TurnsResult);
        var assembly = new HeatSinkAssembly(HeatSinkSlot.HeatSink1);
        var boltId = Guid.NewGuid();
        assembly.RecordBolt(FasteningHead.Pickup, boltId, preliminary);
        assembly.RecordBolt(FasteningHead.Pickup, boltId, firstFinal);
        Assert.Equal(AssemblyResult.Pending, assembly.TurnsResult);
        var retightened = restored with
        {
            Stage = BoltFasteningStage.Retightening, PreliminaryResult = null, PreviousFinalResult = firstFinal,
            Controller = restored.Controller! with { Angle3 = 360 },
            MinimumTurns = 13,
        };
        retightened = System.Text.Json.JsonSerializer.Deserialize<BoltResult>(System.Text.Json.JsonSerializer.Serialize(retightened))!;
        Assert.True(retightened.IsComplete);
        Assert.Equal(13, retightened.TotalTurns);
        Assert.Equal(12, retightened.PreviousFinalResult!.TotalTurns);
        Assert.Equal(AssemblyResult.Ok, retightened.TurnsResult);
        Assert.Equal(AssemblyResult.Ng, (retightened with { MaximumTurns = 12 }).TurnsResult);
        assembly.RecordBolt(FasteningHead.Pickup, boltId, retightened);
        assembly.CompleteFastening();
        assembly.CompleteInspection();
        Assert.Equal(AssemblyResult.Ok, assembly.TurnsResult);
        Assert.Equal(AssemblyResult.Ok, assembly.Result);
    }

    [Fact]
    public async Task UnsupportedBoltHeadDoesNotCompleteTheCarrierWithoutFastening()
    {
        var settings = new BoltFasteningSettings { SafeZ = 0 };
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()), new());
        io.Initialize();
        using var motion = new VirtualMotionService(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var bus = new VirtualAdcBus();
        var head = CreateAdcHead(bus, io, FasteningHead.Shooting, new(), 1, "Virtual", 115200);
        var bolt = new BoltPoint { Head = (FasteningHead)123, FasteningX = 0, FasteningY = 0 };
        var work = ConveyorStation.CreateBoltFastening(io);
        var station = new BoltFasteningStation(head, head, io, motion, new(motion), settings, work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = new() { BoltPoints = [bolt] } } },
            new() { ShootingBoltFeeder = false, PickupBoltFeeder = false },
            new BoltFeederUnit(io, new(), new() { ShootingBoltFeeder = false, PickupBoltFeeder = false }));
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        work.Changed += () =>
        {
            if (work.Completed)
                stop.Cancel();
        };
        var started = false;
        io.OutputChanged += (output, on) =>
        {
            if (on && output is OutputIo.ShootingBoltStart or OutputIo.PickupBoltStart)
                started = true;
        };

        var failure = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => station.RunAsync(stop.Token));

        Assert.Equal(bolt.Head, failure.ActualValue);
        Assert.False(started);
        Assert.False(work.Completed);
        Assert.Empty(work.Assemblies);
    }

    [Theory]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, false, true)]
    public async Task PcbPresenceChangeStopsFasteningWithoutRestarting(
        bool selectedTest, bool betweenBolts, bool otherPcbPresent, bool secondPcbArrives)
    {
        var settings = new BoltFasteningSettings
        {
            ShootingPickupSpinMilliseconds = 0,
            SafeZ = 0,
            DryRunMilliseconds = 20,
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
        };
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()), new());
        io.Initialize();
        using var motion = new VirtualMotionService(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var bus = new VirtualAdcBus();
        var head = CreateAdcHead(bus, io, FasteningHead.Shooting, new(), 1, "Virtual", 115200);
        var bolt = Bolt(1, FasteningHead.Shooting, 0, 0);
        var otherBolt = Bolt(2, FasteningHead.Shooting, 0, 0);
        otherBolt.HeatSink = HeatSinkSlot.HeatSink2;
        var work = ConveyorStation.CreateBoltFastening(io);
        var station = new BoltFasteningStation(head, head, io, motion, new(motion), settings, work,
            new RecipeManager(OpenMachineStore(), new())
            {
                Current = { Pcb = new() { BoltPoints = [bolt, otherBolt], FasteningOrder = [bolt.Id, otherBolt.Id] } },
            },
            new() { ShootingBoltFeeder = false },
            new BoltFeederUnit(io, new(), new() { ShootingBoltFeeder = false }));
        io.SetInputs((InputIo.BoltFasteningHeatSink1Present, true),
            (InputIo.BoltFasteningHeatSink2Present, otherPcbPresent));
        await work.SeatAsync(CancellationToken.None);
        var job = work.CurrentJob;
        var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        var started = false;
        var starts = 0;
        var lowered = false;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.ShootingBoltStart && on)
            {
                started = true;
                starts++;
                if (!betweenBolts)
                    io.SetInput(secondPcbArrives ? InputIo.BoltFasteningHeatSink2Present
                        : InputIo.BoltFasteningHeatSink1Present, secondPcbArrives);
            }
            if (output == OutputIo.ShootingHeadDown && on)
                lowered = true;
        };
        station.Changed += () =>
        {
            if (betweenBolts && started && station.ActiveBolt == (otherPcbPresent ? otherBolt : null))
                io.SetInput(InputIo.BoltFasteningHeatSink1Present, false);
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var failure = await Record.ExceptionAsync(
            () => station.RunAsync(stop.Token, selectedBolts: selectedTest ? [bolt.Id] : null));
        Assert.True(failure is MotionInterlockException,
            $"Failure={failure}, starts={starts}, step={station.Step}, HS1={io.GetInput(InputIo.BoltFasteningHeatSink1Present)}, results={assembly.ShootingBoltResults.Count}");
        Assert.True(started);
        Assert.Equal(1, starts);
        Assert.True(lowered);
        Assert.Equal(otherPcbPresent || secondPcbArrives, work.CarrierSeated);
        Assert.Same(job, work.CurrentJob);
        Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
        var result = Assert.Single(assembly.ShootingBoltResults).Value;
        Assert.Equal(betweenBolts ? BoltResultSource.DryRun : BoltResultSource.Interrupted, result.Source);
        Assert.False(work.Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PickupVacuumWaitRequiresCurrentFeedback(bool on)
    {
        var settings = new BoltFasteningSettings();
        var io = new VirtualIoService(Outputs(new BoltFasteningHardwareSettings()), new() { TimeoutMilliseconds = 100 })
        { AutoResponseEnabled = false };
        using var motion = new VirtualMotionService(settings.Motion, new());
        using var bus = new VirtualAdcBus();
        var head = CreateAdcHead(bus, io, FasteningHead.Pickup, new(), 1, "Virtual", 115200);
        var station = CreateFastening(head, head, io, motion, settings);
        io.SetInput(InputIo.PickupHeadVacuumDetected, !on);
        var scheduler = new ConcurrentExclusiveSchedulerPair();
        try
        {
            await Task.Factory.StartNew(async () =>
            {
                var waiting = station.SetVacuumAsync(FasteningHead.Pickup, on, CancellationToken.None);
                io.SetInput(InputIo.PickupHeadVacuumDetected, on);
                io.SetInput(InputIo.PickupHeadVacuumDetected, !on);
                await Assert.ThrowsAsync<IoTimeoutException>(() => waiting);

                io.SetInput(InputIo.PickupHeadVacuumDetected, on);
                await station.SetVacuumAsync(FasteningHead.Pickup, on, CancellationToken.None);
            }, CancellationToken.None, TaskCreationOptions.None, scheduler.ExclusiveScheduler).Unwrap();
        }
        finally
        {
            scheduler.Complete();
            await scheduler.Completion;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task MeasuredResultSurvivesStopClearanceAndStorageFailures(bool failClearance, bool failStop)
    {
        var settings = new BoltFasteningSettings
        {
            ShootingPickupSpinMilliseconds = 0,
            SafeZ = 5,
            ShootingArrivalDelaySeconds = 0,
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
        settings.ShootingHead.FasteningZ = 12;
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()), new());
        io.Initialize();
        using var motion = new VirtualMotionService(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var bus = new AdcControllerStub();
        using var pickupBus = new VirtualAdcBus();
        var shooting = CreateAdcHead(bus, io, FasteningHead.Shooting, new(), 1, "Virtual", 115200);
        var pickup = CreateAdcHead(pickupBus, io, FasteningHead.Pickup, new(), 1, "Virtual", 115200);
        var units = new UnitSettings();
        var work = ConveyorStation.CreateBoltFastening(io);
        var recipes = new RecipeManager(OpenMachineStore(), new())
        {
            Current = { Pcb = new() { BoltPoints = [Bolt(1, FasteningHead.Shooting, 20, 30)] } },
        };
        var bolt = recipes.Current.Pcb.BoltPoints[0];
        bolt.MinimumTurns = 3;
        bolt.MaximumTurns = 15;
        var station = new BoltFasteningStation(shooting, pickup, io, motion, new MotionStatus(motion), settings,
            work, recipes, units,
            new BoltFeederUnit(io, new(), units));
        SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.ShootingFeederBoltDetected, true);
        await work.SeatAsync(CancellationToken.None);
        var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        var clearFailure = new IOException("Head rise failed.");
        var storageFailure = new IOException("Result storage failed.");
        var stopFailure = new IOException("START OFF failed.");
        var headWasLowered = false;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.ShootBolt && on)
            {
                io.SetInput(InputIo.ShootingTubeBoltDetected, true);
                io.SetInput(InputIo.ShootingTubeBoltDetected, false);
                io.SetInput(InputIo.ShootingHeadVacuumDetected, true);
            }
            if (output == OutputIo.ShootingHeadDown)
            {
                if (on)
                {
                    headWasLowered = true;
                    bolt.MinimumTurns = 20;
                    bolt.MaximumTurns = 30;
                    if (failStop)
                        bus.StopWriteFailure = stopFailure;
                }
                else if (headWasLowered && failClearance)
                    throw clearFailure;
            }
        };
        assembly.ResultsChanged += updated =>
        {
            if (!updated.ShootingBoltResults.ContainsKey(VirtualTestSupport.BoltId(1)))
                return;
            Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
            Assert.False(bus.Running);
            if (!failClearance && !failStop)
            {
                Assert.Equal(StationCylinderState.Up, station.ShootingHeadPosition);
                Assert.Equal(settings.SafeZ, motion.Position.Z);
            }
            throw storageFailure;
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (failClearance || failStop)
        {
            var failure = await Assert.ThrowsAsync<AggregateException>(() => station.RunAsync(stop.Token));
            Assert.Equal(new Exception[] { failStop ? stopFailure : clearFailure, storageFailure }, failure.InnerExceptions);
        }
        else
        {
            Assert.Same(storageFailure, await Assert.ThrowsAsync<IOException>(() => station.RunAsync(stop.Token)));
        }
        Assert.True(assembly.ShootingBoltResults[VirtualTestSupport.BoltId(1)].Success);
        Assert.NotNull(assembly.ShootingBoltResults[VirtualTestSupport.BoltId(1)].Torque);
        Assert.NotNull(assembly.ShootingBoltResults[VirtualTestSupport.BoltId(1)].Controller);
        Assert.Equal(3, assembly.ShootingBoltResults[VirtualTestSupport.BoltId(1)].MinimumTurns);
        Assert.Equal(15, assembly.ShootingBoltResults[VirtualTestSupport.BoltId(1)].MaximumTurns);
        Assert.Equal(1, bus.StartWrites);
        Assert.False(work.Completed);
    }

    [Fact]
    public async Task ResultTimeoutRecordsNgRaisesHeadAndContinuesToNextBolt()
    {
        var settings = new BoltFasteningSettings
        {
            ShootingPickupSpinMilliseconds = 0,
            SafeZ = 5,
            ShootingArrivalDelaySeconds = 0,
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
        settings.ShootingHead.FasteningZ = 12;
        var controllerSettings = new IoBoltHardwareSettings();
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings(), controllerSettings), new());
        io.Initialize();
        using var motion = new VirtualMotionService(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var pickupBus = new VirtualAdcBus();
        var pickup = CreateAdcHead(pickupBus, io, FasteningHead.Pickup, new(), 1, "Virtual", 115200);
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        IBoltHead head = CreateAdcHead(bus, io, FasteningHead.Shooting,
            new HantasSettings { FasteningTimeoutMilliseconds = 100, StatusPollMilliseconds = 10 }, 1, "Virtual", 115200);
        var units = new UnitSettings();
        var work = ConveyorStation.CreateBoltFastening(io);
        Assert.Null(work.LastCycleSeconds);
        var layout = new PcbLayout
        {
            BoltPoints = [Bolt(1, FasteningHead.Shooting, 20, 30), Bolt(2, FasteningHead.Shooting, 30, 40)],
        };
        var station = new BoltFasteningStation(head, pickup, io, motion, new MotionStatus(motion), settings,
            work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = layout } }, units,
            new BoltFeederUnit(io, new(), units));
        SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.ShootingFeederBoltDetected, true);
        await work.SeatAsync(CancellationToken.None);
        var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        var raisedAfterTimeout = false;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.ShootBolt && on)
            {
                io.SetInput(InputIo.ShootingTubeBoltDetected, true);
                io.SetInput(InputIo.ShootingTubeBoltDetected, false);
                io.SetInput(InputIo.ShootingHeadVacuumDetected, true);
            }
        };
        assembly.ResultsChanged += updated =>
        {
            if (updated.ShootingBoltResults.ContainsKey(VirtualTestSupport.BoltId(1)))
            {
                Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
                Assert.False(bus.Running);
                Assert.Equal(StationCylinderState.Up, station.ShootingHeadPosition);
                Assert.Equal(settings.SafeZ, motion.Position.Z);
                raisedAfterTimeout = true;
                bus.SuppressCompletion = false;
            }
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = station.RunAsync(stop.Token);
        try
        {
            Assert.True(await WaitUntilAsync(() => work.Completed || run.IsCompleted, TimeSpan.FromSeconds(4)));
            Assert.True(work.Completed, run.Exception?.ToString());
            Assert.True(work.LastCycleSeconds > 0);
            Assert.True(raisedAfterTimeout);
            Assert.Equal(2, bus.StartWrites);
            var failed = assembly.ShootingBoltResults[VirtualTestSupport.BoltId(1)];
            Assert.False(failed.Success);
            Assert.Null(failed.Torque);
            Assert.Null(failed.Controller);
            Assert.Contains("timed out", failed.Error);
            Assert.NotNull(failed.RecordedAt);
            Assert.True(assembly.ShootingBoltResults[VirtualTestSupport.BoltId(2)].Success);
            Assert.Equal(AssemblyResult.Ng, assembly.FasteningResult);
            Assert.Equal(StationCylinderState.Up, station.ShootingHeadPosition);
        }
        finally
        {
            stop.Cancel();
            await run;
        }
    }

    public enum RetractionScenario { HeadFirst, ZFirst, Stop, MotionFailure, HeadAndMotionFailure }

    [Theory]
    [InlineData(FasteningHead.Pickup, RetractionScenario.HeadFirst)]
    [InlineData(FasteningHead.Shooting, RetractionScenario.ZFirst)]
    [InlineData(FasteningHead.Pickup, RetractionScenario.Stop)]
    [InlineData(FasteningHead.Shooting, RetractionScenario.MotionFailure)]
    [InlineData(FasteningHead.Shooting, RetractionScenario.HeadAndMotionFailure)]
    [InlineData(FasteningHead.Shooting, RetractionScenario.ZFirst, true)]
    [InlineData(FasteningHead.Shooting, RetractionScenario.Stop, true)]
    public async Task RetractionOverlapsHeadAndZAndDrainsBothBeforeNextBolt(
        FasteningHead selectedHead, RetractionScenario scenario, bool pickupNext = false)
    {
        var settings = new BoltFasteningSettings
        {
            ShootingPickupSpinMilliseconds = 0,
            ShootingPreliminaryZ = 12,
            SafeZ = 5,
            DryRunMilliseconds = 20,
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
        settings.PickupHead.FasteningZ = 12;
        settings.ShootingHead.FasteningZ = 12;
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()),
            new() { TimeoutMilliseconds = 3_000 });
        io.Initialize();
        using var motion = new VirtualMotionService(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        using var otherBus = new AdcControllerStub { SuppressCompletion = true };
        var head = CreateAdcHead(bus, io, selectedHead, new(), 1, "Virtual", 115200);
        var otherHead = CreateAdcHead(otherBus, io,
            selectedHead == FasteningHead.Pickup ? FasteningHead.Shooting : FasteningHead.Pickup,
            new(), 1, "Other", 115200);
        var nextHead = pickupNext ? FasteningHead.Pickup : selectedHead;
        var retractionZ = settings.SafeZ;
        var bolts = new[] { Bolt(1, selectedHead, 20, 30), Bolt(2, nextHead, 40, 50) };
        var work = ConveyorStation.CreateBoltFastening(io);
        var station = new BoltFasteningStation(
            selectedHead == FasteningHead.Shooting ? head : otherHead,
            selectedHead == FasteningHead.Pickup ? head : otherHead,
            io, motion, new(motion), settings,
            work, new RecipeManager(OpenMachineStore(), new())
            {
                Current = { Pcb = new()
                {
                    BoltPoints = [.. bolts],
                    FasteningOrder = selectedHead == FasteningHead.Pickup ? [bolts[1].Id, bolts[0].Id] : [],
                } },
            },
            new() { ShootingBoltFeeder = false, PickupBoltFeeder = false },
            new BoltFeederUnit(io, new(), new() { ShootingBoltFeeder = false, PickupBoltFeeder = false }));
        SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        foreach (var bolt in bolts.Where(bolt => bolt.Head == FasteningHead.Pickup))
            assembly.RecordBolt(bolt.Head, bolt.Id, new(true, 1) { Stage = BoltFasteningStage.Preliminary });
        await ((IIoService)io).SetOutputAndWaitAsync(
            OutputIo.PickupTableDown, selectedHead == FasteningHead.Pickup);
        await station.MoveToBoltAsync(bolts[0]);
        var (start, down, vacuum, upInput, downInput) = selectedHead == FasteningHead.Pickup
            ? (OutputIo.PickupBoltStart, OutputIo.PickupHeadDown, OutputIo.PickupHeadVacuumPump,
                InputIo.PickupHeadUp, InputIo.PickupHeadDown)
            : (OutputIo.ShootingBoltStart, OutputIo.ShootingHeadDown, OutputIo.ShootingHeadVacuumPump,
                InputIo.ShootingHeadUp, InputIo.ShootingHeadDown);
        var fasteningStarted = false;
        var riseRequested = false;
        var vacuumReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextXy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var motionFailure = new IOException("Retraction Z failed.");
        var headFailure = new IOException("Head UP failed.");
        var failMotion = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        io.OutputChanged += (output, on) =>
        {
            if (output == start && on)
            {
                fasteningStarted = true;
                io.SetOutput(vacuum, true);
                if (selectedHead == FasteningHead.Pickup)
                    io.SetInput(InputIo.PickupHeadVacuumDetected, true);
            }
            if (output == down && on)
            {
                io.AutoResponseEnabled = false;
                io.SetInputs((upInput, false), (downInput, true));
            }
            if (!fasteningStarted)
                return;
            if (output == start && !on)
                settings.Motion.ZSpeed = 10; // Hold Z in motion while cylinder feedback is controlled independently.
            if (output == vacuum && !on)
            {
                Assert.False(io.GetOutput(start));
                vacuumReleased.TrySetResult();
            }
            if (output == down && !on)
            {
                Assert.False(io.GetOutput(start));
                Assert.False(io.GetOutput(vacuum));
                riseRequested = true;
                if (scenario == RetractionScenario.HeadAndMotionFailure)
                {
                    // The head command fails first; draining the cancelled Z move also fails.
                    failMotion = true;
                    throw headFailure;
                }
            }
            if (output == OutputIo.PickupTableDown && on)
            {
                Assert.True(io.GetInput(upInput));
                Assert.False(motion.IsMoving);
                Assert.Equal(settings.SafeZ, motion.Position.Z);
                io.SetInputs((InputIo.PickupTableUp, false), (InputIo.PickupTableDown, true));
            }
        };
        motion.PositionChanged += (x, y, z) =>
        {
            if (failMotion)
                throw motionFailure;
            if (!fasteningStarted || x == 20 && y == 30)
                return;
            Assert.True(io.GetInput(upInput));
            Assert.Equal(retractionZ, z);
            nextXy.TrySetResult();
            stop.Cancel();
        };
        var results = selectedHead == FasteningHead.Pickup ? assembly.PickupBoltResults : assembly.ShootingBoltResults;
        var run = station.RunAsync(stop.Token, selectedBolts: bolts.Select(bolt => bolt.Id).ToArray());
        try
        {
            await vacuumReleased.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (selectedHead == FasteningHead.Pickup)
            {
                Assert.False(riseRequested);
                Assert.False(motion.IsMoving);
                Assert.Equal(12, motion.Position.Z);
                io.SetInput(InputIo.PickupHeadVacuumDetected, false);
            }
            if (scenario != RetractionScenario.HeadAndMotionFailure)
            {
                Assert.True(await WaitUntilAsync(() => riseRequested && motion.IsMoving, TimeSpan.FromSeconds(1)));
                Assert.False(io.GetInput(upInput));
                Assert.DoesNotContain(results.Values, result => result.Stage != BoltFasteningStage.Preliminary);
                Assert.False(nextXy.Task.IsCompleted);
            }
            switch (scenario)
            {
                case RetractionScenario.HeadFirst:
                    io.SetInputs((upInput, true), (downInput, false));
                    Assert.True(motion.IsMoving);
                    Assert.False(nextXy.Task.IsCompleted);
                    await nextXy.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    await run;
                    break;
                case RetractionScenario.ZFirst:
                    Assert.True(await WaitUntilAsync(() => !motion.IsMoving, TimeSpan.FromSeconds(2)));
                    Assert.Equal(retractionZ, motion.Position.Z);
                    Assert.False(nextXy.Task.IsCompleted);
                    Assert.DoesNotContain(results.Values, result => result.Stage != BoltFasteningStage.Preliminary);
                    io.SetInputs((upInput, true), (downInput, false));
                    await nextXy.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    await run;
                    break;
                case RetractionScenario.Stop:
                    stop.Cancel();
                    await run.WaitAsync(TimeSpan.FromSeconds(1));
                    Assert.False(nextXy.Task.IsCompleted);
                    break;
                case RetractionScenario.MotionFailure:
                    failMotion = true;
                    Assert.Same(motionFailure, await Assert.ThrowsAsync<IOException>(
                        () => run.WaitAsync(TimeSpan.FromSeconds(1))));
                    Assert.False(nextXy.Task.IsCompleted);
                    break;
                case RetractionScenario.HeadAndMotionFailure:
                    var failures = (await Assert.ThrowsAsync<AggregateException>(
                        () => run.WaitAsync(TimeSpan.FromSeconds(1)))).Flatten().InnerExceptions;
                    Assert.Equal(2, failures.Count);
                    Assert.Contains(headFailure, failures);
                    Assert.Contains(motionFailure, failures);
                    Assert.False(nextXy.Task.IsCompleted);
                    break;
            }
            Assert.False(motion.IsMoving);
            Assert.False(io.GetOutput(start));
            Assert.Equal(1, bus.StartWrites);
            Assert.True(results[bolts[0].Id].Success);
            Assert.Equal(BoltResultSource.DryRun, results[bolts[0].Id].Source);
            Assert.Equal(selectedHead, results[bolts[0].Id].ToolHead);
        }
        finally
        {
            stop.Cancel();
            await run.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Theory]
    [InlineData(ShootingPreparationFailure.None)]
    [InlineData(ShootingPreparationFailure.None, 1.5)]
    [InlineData(ShootingPreparationFailure.Stop)]
    [InlineData(ShootingPreparationFailure.StopAndCleanup)]
    [InlineData(ShootingPreparationFailure.Motion)]
    [InlineData(ShootingPreparationFailure.MotionAndCleanup)]
    [InlineData(ShootingPreparationFailure.Supply)]
    [InlineData(ShootingPreparationFailure.SupplyBeforeTravel)]
    [InlineData(ShootingPreparationFailure.ClearanceLost)]
    [InlineData(ShootingPreparationFailure.CarrierLost)]
    public async Task NextShootingSupplyOverlapsRetractionAndTravelWithoutDuplicateShot(
        ShootingPreparationFailure failure, double arrivalDelaySeconds = 0)
    {
        var settings = new BoltFasteningSettings
        {
            ShootingPickupSpinMilliseconds = 0,
            ShootingPreliminaryZ = 12,
            SafeZ = 5,
            ShootingArrivalDelaySeconds = 0,
            DryRunMilliseconds = 20,
            ShootingDetectionTimeoutMilliseconds = failure is ShootingPreparationFailure.Supply
                or ShootingPreparationFailure.SupplyBeforeTravel ? 150 : 2_000,
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
        settings.ShootingHead.FasteningZ = 12;
        settings.PickupHead.FasteningZ = 12;
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()),
            new() { TimeoutMilliseconds = 2_000 }) { AutoResponseEnabled = false };
        io.SetInputs((InputIo.BoltFasteningHeatSink1Present, true),
            (InputIo.BoltFasteningBackupPlateUp, true), (InputIo.BoltFasteningStopperDown, true),
            (InputIo.ShootingFeederBoltDetected, true), (InputIo.ShootingEscapeBackward, true));
        using var motion = new VirtualMotionService(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var bus = new AdcControllerStub();
        using var pickupBus = new AdcControllerStub();
        var shooting = CreateAdcHead(bus, io, FasteningHead.Shooting,
            new() { StatusPollMilliseconds = 10 }, 1, "Shooting", 115200);
        var pickup = CreateAdcHead(pickupBus, io, FasteningHead.Pickup, new(), 1, "Pickup", 115200);
        var bolts = new[] { Bolt(1, FasteningHead.Shooting, 20, 30),
            Bolt(2, FasteningHead.Shooting, 30, 40), Bolt(3, FasteningHead.Shooting, 40, 50),
            Bolt(4, FasteningHead.Pickup, 60, 50) };
        bolts[3].PreliminaryFasteningPosition = new() { X = 60, Y = 50 };
        var work = ConveyorStation.CreateBoltFastening(io);
        var stationIo = new WriteNotifyingIo(io);
        var station = new BoltFasteningStation(shooting, pickup, stationIo, motion, new(motion), settings,
            work, new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = new() { BoltPoints = [.. bolts] } } },
            new() { PickupBoltFeeder = false },
            new BoltFeederUnit(stationIo, new(), new() { PickupBoltFeeder = false }));
        await station.MoveToBoltAsync(bolts[0]);
        io.SetOutput(OutputIo.PickupHeadVacuumPump, true);
        io.SetInput(InputIo.PickupHeadVacuumDetected, true);
        var shots = 0;
        var secondSupply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSupplyStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextPointReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long secondPassageAt = 0;
        var nextTravelStarted = false;
        var motionFailure = new IOException("Retraction failed while supplying the next bolt.");
        var supplyCleanupFailure = new IOException("Pending shooting supply could not turn its output off.");
        var finalCleanupFailure = new IOException("Final shooting output OFF failed.");
        var cleanupWrites = 0;
        var failMotion = false;
        stationIo.OutputChanged += (output, on) =>
        {
            if (failure is ShootingPreparationFailure.MotionAndCleanup or ShootingPreparationFailure.StopAndCleanup
                && shots == 2 && output == OutputIo.ShootBolt && !on)
            {
                cleanupWrites++;
                throw cleanupWrites == 1 ? supplyCleanupFailure : finalCleanupFailure;
            }
        };
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.ShootingEscapeForward)
                io.SetInputs((InputIo.ShootingEscapeForward, on),
                    (InputIo.ShootingEscapeBackward, !on && !(arrivalDelaySeconds > 0 && shots == 2)));
            if (output == OutputIo.ShootingBoltStart)
            {
                settings.Motion.ZSpeed = !on && bus.StartWrites == 1 ? 10 : 20_000;
                if (on && bus.StartWrites == 2 && arrivalDelaySeconds > 0)
                {
                    // Task.Delay uses the system timer's resolution.
                    Assert.InRange(Stopwatch.GetElapsedTime(secondPassageAt).TotalSeconds,
                        arrivalDelaySeconds - 0.02, double.MaxValue);
                    Assert.True(VirtualTestSupport.IsAt(motion, settings.GetBoltPosition(bolts[2])));
                }
            }
            if (output == OutputIo.PickupTableDown)
                io.SetInputs((InputIo.PickupTableUp, !on), (InputIo.PickupTableDown, on));
            if (output == OutputIo.PickupHeadDown)
                io.SetInputs((InputIo.PickupHeadUp, !on), (InputIo.PickupHeadDown, on));
            if (output == OutputIo.PickupHeadVacuumPump)
                io.SetInput(InputIo.PickupHeadVacuumDetected, on);
            if (output == OutputIo.ShootingHeadVacuumPump && !on)
                io.SetInput(InputIo.ShootingHeadVacuumDetected, false);
            if (output == OutputIo.ShootingHeadDown)
            {
                // Hold the first rise until the test releases its feedback.
                if (on || bus.StartWrites != 1)
                    io.SetInputs((InputIo.ShootingHeadUp, !on), (InputIo.ShootingHeadDown, on));
            }
            if (output == OutputIo.ShootBolt && on)
            {
                shots++;
                if (shots == 2)
                {
                    settings.ShootingArrivalDelaySeconds = arrivalDelaySeconds;
                    Assert.Equal(bolts[0].Id, station.ActiveBolt!.Id);
                    Assert.Equal(1, bus.StartWrites);
                    Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
                    Assert.False(io.GetOutput(OutputIo.ShootingHeadDown));
                    Assert.False(io.GetInput(InputIo.ShootingHeadUp));
                    Assert.True(motion.IsMoving);
                    secondSupply.TrySetResult();
                }
                else
                {
                    io.SetInput(InputIo.ShootingTubeBoltDetected, true);
                    io.SetInput(InputIo.ShootingTubeBoltDetected, false);
                    io.SetInput(InputIo.ShootingHeadVacuumDetected, true);
                }
            }
            if (output == OutputIo.ShootBolt && !on && shots == 2)
                secondSupplyStopped.TrySetResult();
        };
        motion.StateChanged += () =>
        {
            if (station.ActiveBolt?.Id == bolts[2].Id && motion.IsMovingHorizontal)
            {
                nextTravelStarted = true;
                settings.Motion.ZSpeed = 20_000;
            }
        };
        motion.PositionChanged += (x, y, z) =>
        {
            if (failMotion)
                throw motionFailure;
            if (station.ActiveBolt?.Id == bolts[2].Id && bus.StartWrites == 1 && (x != 20 || y != 30))
            {
                Assert.True(station.IsHorizontalMoveAllowed);
                if (z == settings.ShootingHead.FasteningZ)
                    nextPointReached.TrySetResult();
            }
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = station.RunAsync(stop.Token, selectedBolts: [bolts[0].Id, bolts[2].Id, bolts[3].Id],
            resultReceived: (bolt, result) =>
            {
                if (bolt.Id == bolts[0].Id && failure == ShootingPreparationFailure.ClearanceLost)
                    io.SetInputs((InputIo.ShootingHeadUp, false), (InputIo.ShootingHeadDown, false));
            });
        var results = work.GetAssembly(HeatSinkSlot.HeatSink1).ShootingBoltResults;
        try
        {
            await secondSupply.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(nextPointReached.Task.IsCompleted);
            switch (failure)
            {
                case ShootingPreparationFailure.None:
                    if (arrivalDelaySeconds > 0)
                    {
                        secondPassageAt = Stopwatch.GetTimestamp();
                        io.SetInput(InputIo.ShootingTubeBoltDetected, true);
                        io.SetInput(InputIo.ShootingTubeBoltDetected, false);
                        io.SetInput(InputIo.ShootingHeadVacuumDetected, true);
                    }
                    io.SetInputs((InputIo.ShootingHeadUp, true), (InputIo.ShootingHeadDown, false));
                    Assert.True(await WaitUntilAsync(() => nextPointReached.Task.IsCompleted || run.IsCompleted, TimeSpan.FromSeconds(2)),
                        $"Step={station.NextStep}, position={motion.Position}, pickup={station.PickupHeadPosition}, shooting={station.ShootingHeadPosition}, table={station.PickupTablePosition}, run={run.Status}");
                    if (run.IsCompleted)
                        await run;
                    Assert.True(nextPointReached.Task.IsCompleted);
                    if (arrivalDelaySeconds > 0)
                    {
                        Assert.True(Stopwatch.GetElapsedTime(secondPassageAt).TotalSeconds < arrivalDelaySeconds);
                        Assert.False(io.GetInput(InputIo.ShootingEscapeBackward));
                    }
                    Assert.True(io.GetOutput(OutputIo.ShootBolt));
                    Assert.Equal(1, bus.StartWrites); // Travel is complete; supply still owns the next START.
                    Assert.Equal(2, shots);
                    if (arrivalDelaySeconds == 0)
                    {
                        io.SetInput(InputIo.ShootingTubeBoltDetected, true);
                        io.SetInput(InputIo.ShootingTubeBoltDetected, false);
                        io.SetInput(InputIo.ShootingHeadVacuumDetected, true);
                    }
                    await run.WaitAsync(TimeSpan.FromSeconds(2));
                    Assert.Equal(3, bus.StartWrites);
                    Assert.Equal(1, pickupBus.StartWrites);
                    Assert.Equal(new[] { bolts[0].Id, bolts[2].Id }.Order(), results.Keys.Order());
                    Assert.True(work.GetAssembly(HeatSinkSlot.HeatSink1).PickupBoltResults[bolts[3].Id].Success);
                    break;
                case ShootingPreparationFailure.Stop:
                    stop.Cancel();
                    await run.WaitAsync(TimeSpan.FromSeconds(1));
                    break;
                case ShootingPreparationFailure.Motion:
                    failMotion = true;
                    Assert.Same(motionFailure, await Assert.ThrowsAsync<IOException>(
                        () => run.WaitAsync(TimeSpan.FromSeconds(1))));
                    break;
                case ShootingPreparationFailure.MotionAndCleanup or ShootingPreparationFailure.StopAndCleanup:
                    if (failure == ShootingPreparationFailure.StopAndCleanup)
                        stop.Cancel();
                    else
                        failMotion = true;
                    var failures = (await Assert.ThrowsAsync<AggregateException>(
                        () => run.WaitAsync(TimeSpan.FromSeconds(1)))).Flatten().InnerExceptions;
                    if (failure == ShootingPreparationFailure.MotionAndCleanup)
                        Assert.Contains(motionFailure, failures);
                    Assert.Contains(supplyCleanupFailure, failures);
                    Assert.Contains(finalCleanupFailure, failures);
                    Assert.Equal(2, cleanupWrites);
                    Assert.False(nextTravelStarted);
                    break;
                case ShootingPreparationFailure.Supply:
                    io.SetInputs((InputIo.ShootingHeadUp, true), (InputIo.ShootingHeadDown, false));
                    var error = await Assert.ThrowsAsync<IoTimeoutException>(() => run.WaitAsync(TimeSpan.FromSeconds(2)));
                    Assert.Equal(InputIo.ShootingTubeBoltDetected, error.Input);
                    break;
                case ShootingPreparationFailure.SupplyBeforeTravel:
                    await secondSupplyStopped.Task.WaitAsync(TimeSpan.FromSeconds(1));
                    io.SetInputs((InputIo.ShootingHeadUp, true), (InputIo.ShootingHeadDown, false));
                    await Assert.ThrowsAsync<IoTimeoutException>(() => run.WaitAsync(TimeSpan.FromSeconds(3)));
                    Assert.False(nextTravelStarted);
                    break;
                case ShootingPreparationFailure.ClearanceLost:
                    io.SetInput(InputIo.ShootingTubeBoltDetected, true);
                    io.SetInput(InputIo.ShootingTubeBoltDetected, false);
                    await secondSupplyStopped.Task.WaitAsync(TimeSpan.FromSeconds(1));
                    io.SetInputs((InputIo.ShootingHeadUp, true), (InputIo.ShootingHeadDown, false));
                    await Assert.ThrowsAsync<MotionInterlockException>(() => run.WaitAsync(TimeSpan.FromSeconds(3)));
                    Assert.False(nextTravelStarted);
                    Assert.True(io.GetOutput(OutputIo.ShootingHeadVacuumPump));
                    break;
                case ShootingPreparationFailure.CarrierLost:
                    io.SetInput(InputIo.BoltFasteningBackupPlateUp, false);
                    await Assert.ThrowsAsync<MotionInterlockException>(() => run.WaitAsync(TimeSpan.FromSeconds(1)));
                    Assert.False(nextTravelStarted);
                    break;
            }
            Assert.Equal(2, shots); // Neither the unselected bolt nor the following pickup causes another shot.
            Assert.False(motion.IsMoving);
            Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
            Assert.False(io.GetOutput(OutputIo.ShootBolt));
            Assert.False(io.GetOutput(OutputIo.ShootingEscapeForward));
            Assert.False(station.IsRunning);
            Assert.Null(station.Step);
            Assert.False(work.Completed);
            Assert.True(results[bolts[0].Id].Success); // A next-supply failure cannot erase the completed bolt.
            if (failure != ShootingPreparationFailure.None)
            {
                Assert.Equal(1, bus.StartWrites);
                Assert.Single(results);
            }
            if (failure is ShootingPreparationFailure.Stop or ShootingPreparationFailure.StopAndCleanup)
            {
                io.SetInputs((InputIo.ShootingHeadUp, true), (InputIo.ShootingHeadDown, false));
                settings.Motion.ZSpeed = 20_000;
                using var restart = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await station.RunAsync(restart.Token, selectedBolts: [bolts[0].Id]);
                Assert.Equal(3, shots); // Restart supplies the first selected bolt afresh.
                Assert.Equal(2, bus.StartWrites);
            }
        }
        finally
        {
            stop.Cancel();
            await run.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task AdcResultOrDryRunRaisesHeadAndContinuesToNextBolt(bool rejectedResponse, bool dryRun)
    {
        var settings = new BoltFasteningSettings
        {
            ShootingPickupSpinMilliseconds = 0,
            ShootingIpmPreset = 3,
            ShootingPcbPreset = 2,
            SafeZ = 5,
            DryRunMilliseconds = 30,
            ShootingArrivalDelaySeconds = 0,
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
        settings.ShootingHead.FasteningZ = 12;
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()), new());
        io.Initialize();
        using var motion = new VirtualMotionService(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var bus = new AdcControllerStub();
        if (rejectedResponse)
        {
            // Exact exception reply in the equipment log, including CRC.
            bus.NextResultReadFailure = new AdcResponseException(3, "HComm error 0x03; RX=0184030301.");
        }
        else
        {
            bus.ResultStatus = AdcEventStatus.Error;
            bus.ResultError = 42;
        }
        var head = CreateAdcHead(bus, io, FasteningHead.Shooting, new(), 1, "Virtual", 115200);
        var pickup = CreateAdcHead(new VirtualAdcBus(), io, FasteningHead.Pickup, new(), 2, "Virtual", 115200);
        var units = new UnitSettings { ShootingBoltFeeder = !dryRun };
        var work = ConveyorStation.CreateBoltFastening(io);
        var layout = new PcbLayout
        {
            BoltPoints = [Bolt(1, FasteningHead.Shooting, 20, 30), Bolt(2, FasteningHead.Shooting, 30, 40)],
        };
        var station = new BoltFasteningStation(head, pickup, io, motion, new MotionStatus(motion), settings,
            work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = layout } }, units,
            new BoltFeederUnit(io, new(), units));
        SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.ShootingFeederBoltDetected, true);
        await work.SeatAsync(CancellationToken.None);
        var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        var raisedAfterResult = false;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.ShootBolt && on)
            {
                io.SetInput(InputIo.ShootingTubeBoltDetected, true);
                io.SetInput(InputIo.ShootingTubeBoltDetected, false);
                io.SetInput(InputIo.ShootingHeadVacuumDetected, true);
            }
        };
        assembly.ResultsChanged += updated =>
        {
            if (updated.ShootingBoltResults.ContainsKey(VirtualTestSupport.BoltId(1)))
            {
                Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
                Assert.False(bus.Running);
                Assert.Equal(StationCylinderState.Up, station.ShootingHeadPosition);
                Assert.Equal(settings.SafeZ, motion.Position.Z);
                raisedAfterResult = true;
            }
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = station.RunAsync(stop.Token);
        try
        {
            Assert.True(await WaitUntilAsync(() => work.Completed || run.IsCompleted, TimeSpan.FromSeconds(4)));
            Assert.True(work.Completed, run.Exception?.ToString());
            Assert.True(raisedAfterResult);
            Assert.Equal(2, bus.StartWrites);
            Assert.Equal(rejectedResponse || dryRun ? 0 : 1, bus.ResetWrites);
            Assert.Equal(2, bus.StopWrites);
            Assert.Equal(2, assembly.ShootingBoltResults.Count);
            Assert.True(io.GetOutput(OutputIo.ShootingBoltPreset2));
            Assert.False(io.GetOutput(OutputIo.ShootingBoltPreset1));
            Assert.False(io.GetOutput(OutputIo.ShootingBoltPreset3));
            if (dryRun)
            {
                Assert.Equal(0, bus.ResultReads);
                Assert.Equal(AssemblyResult.Ng, assembly.FasteningResult);
                Assert.All(assembly.ShootingBoltResults.Values, result =>
                {
                    Assert.Equal(BoltResultSource.DryRun, result.Source);
                    Assert.Null(result.Torque);
                    Assert.Null(result.Error);
                });
                Assert.Equal(StationCylinderState.Up, station.ShootingHeadPosition);
                return;
            }
            if (rejectedResponse)
            {
                Assert.Equal(2, bus.ResultReads); // One result read per bolt, without retrying the rejected read.
                Assert.False(assembly.ShootingBoltResults[VirtualTestSupport.BoltId(1)].Success);
                Assert.Null(assembly.ShootingBoltResults[VirtualTestSupport.BoltId(1)].Torque);
                Assert.Contains("0x03", assembly.ShootingBoltResults[VirtualTestSupport.BoltId(1)].Error);
                Assert.True(assembly.ShootingBoltResults[VirtualTestSupport.BoltId(2)].Success);
                Assert.NotNull(assembly.ShootingBoltResults[VirtualTestSupport.BoltId(2)].Controller);
                Assert.Equal(AssemblyResult.Ng, assembly.FasteningResult);
            }
            else
            {
                Assert.False(assembly.ShootingBoltResults[VirtualTestSupport.BoltId(1)].Success);
                Assert.Contains("42", assembly.ShootingBoltResults[VirtualTestSupport.BoltId(1)].Error);
                Assert.False(assembly.ShootingBoltResults[VirtualTestSupport.BoltId(2)].Success); // A second START must occur, not reuse the old Error event.
                Assert.Equal(AssemblyResult.Ng, assembly.FasteningResult);
            }
            Assert.Equal(StationCylinderState.Up, station.ShootingHeadPosition);
            Assert.All(assembly.ShootingBoltResults.Values.Where(result => result.Controller is not null), result =>
            {
                Assert.Equal((ushort)2, result.Controller!.Preset);
                Assert.Equal((ushort)80, result.Controller.TorqueCompensationPercent);
            });
        }
        finally
        {
            stop.Cancel();
            await run;
        }
    }

    [Fact]
    public async Task ShootingRequiresCurrentlyEmptyTubeBeforeAdvancingEscape()
    {
        var settings = new BoltFasteningSettings { ShootingDetectionTimeoutMilliseconds = 100 };
        var io = new VirtualIoService(Outputs(new BoltFasteningHardwareSettings()), new() { TimeoutMilliseconds = 100 })
        { AutoResponseEnabled = false };
        using var motion = new VirtualMotionService(settings.Motion, new());
        using var bus = new VirtualAdcBus();
        var head = CreateAdcHead(bus, io, FasteningHead.Shooting, new(), 1, "Virtual", 115200);
        var station = CreateFastening(head, head, io, motion, settings);
        io.SetInputs((InputIo.ShootingEscapeBackward, true), (InputIo.ShootingFeederBoltDetected, true),
            (InputIo.ShootingTubeBoltDetected, true));
        var advanced = false;
        io.OutputChanged += (output, on) => advanced |= output == OutputIo.ShootingEscapeForward && on;
        var scheduler = new ConcurrentExclusiveSchedulerPair();
        try
        {
            await Task.Factory.StartNew(async () =>
            {
                var shooting = station.ShootBoltAsync();
                io.SetInput(InputIo.ShootingTubeBoltDetected, false);
                io.SetInput(InputIo.ShootingTubeBoltDetected, true);
                var failure = await Assert.ThrowsAsync<IoTimeoutException>(() => shooting);

                Assert.Equal(InputIo.ShootingTubeBoltDetected, failure.Input);
                Assert.False(advanced);
                Assert.False(io.GetOutput(OutputIo.ShootBolt));
            }, CancellationToken.None, TaskCreationOptions.None, scheduler.ExclusiveScheduler).Unwrap();
        }
        finally
        {
            scheduler.Complete();
            await scheduler.Completion;
        }
    }

    [Theory]
    [InlineData(InputIo.ShootingTubeBoltDetected, true)]
    [InlineData(InputIo.ShootingTubeBoltDetected, false)]
    public async Task ShootingDetectionUsesItsOwnTimeoutAndStopsTheShot(InputIo input, bool value)
    {
        var settings = new BoltFasteningSettings { ShootingDetectionTimeoutMilliseconds = 50 };
        var io = new VirtualIoService(
            new BoltFasteningHardwareSettings().Outputs,
            new MachineOptions { TimeoutMilliseconds = 5 })
        { AutoResponseEnabled = false };
        using var motion = new VirtualMotionService(settings.Motion, new());
        using var bus = new VirtualAdcBus();
        using var shootingBus = new VirtualAdcBus();
        var gantry = VirtualTestSupport.CreateFastening(
            CreateAdcHead(shootingBus, io, FasteningHead.Shooting, new(), 2, "Virtual", 115200),
            CreateAdcHead(bus, io, FasteningHead.Pickup, new(), 1, "Virtual", 115200),
            io, motion, settings);
        io.Initialize();
        io.SetInput(InputIo.ShootingFeederBoltDetected, true);
        io.SetInput(InputIo.ShootingEscapeBackward, true);
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.ShootingEscapeForward)
                io.SetInputs((InputIo.ShootingEscapeForward, on), (InputIo.ShootingEscapeBackward, !on));
            else if (output == OutputIo.ShootBolt && on && !value)
                io.SetInput(InputIo.ShootingTubeBoltDetected, true);
        };

        var error = await Assert.ThrowsAsync<IoTimeoutException>(() => gantry.ShootBoltAsync());

        Assert.Contains(input.GetDescription(), error.Message);
        Assert.Contains("timeout (50 ms)", error.Message);
        Assert.False(io.GetOutput(OutputIo.ShootBolt));
        Assert.False(io.GetOutput(OutputIo.ShootingEscapeForward));
        Assert.True(io.GetInput(InputIo.ShootingEscapeBackward));
    }

    [Fact]
    public async Task CancellingEscapeAdvanceReturnsItBackward()
    {
        var settings = new BoltFasteningSettings();
        var io = new VirtualIoService(new BoltFasteningHardwareSettings().Outputs, new())
        { AutoResponseEnabled = false };
        using var motion = new VirtualMotionService(settings.Motion, new());
        using var bus = new VirtualAdcBus();
        using var shootingBus = new VirtualAdcBus();
        var station = VirtualTestSupport.CreateFastening(
            CreateAdcHead(shootingBus, io, FasteningHead.Shooting, new(), 2, "Virtual", 115200),
            CreateAdcHead(bus, io, FasteningHead.Pickup, new(), 1, "Virtual", 115200), io, motion, settings);
        io.SetInput(InputIo.ShootingFeederBoltDetected, true);
        io.SetInput(InputIo.ShootingEscapeBackward, true);
        using var stop = new CancellationTokenSource();
        io.OutputChanged += (output, on) =>
        {
            if (output != OutputIo.ShootingEscapeForward)
                return;
            io.SetInput(InputIo.ShootingEscapeBackward, !on);
            if (on)
                stop.Cancel(); // Cancel before Forward completion feedback arrives.
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => station.ShootBoltAsync(stop.Token));

        Assert.False(io.GetOutput(OutputIo.ShootingEscapeForward));
        Assert.True(io.GetInput(InputIo.ShootingEscapeBackward));
        Assert.False(io.GetOutput(OutputIo.ShootBolt));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShootingArrivalUsesSecondsWithoutVacuumAndStopCancelsDelay(bool stopDuringDelay)
    {
        var settings = new BoltFasteningSettings
        {
            ShootingPickupSpinMilliseconds = 0,
            ShootingArrivalDelaySeconds = 0.2,
            ShootingDetectionTimeoutMilliseconds = 500,
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.ShootingArrivalDelaySeconds = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.ShootingArrivalDelaySeconds = double.NaN);
        var io = new VirtualIoService(new BoltFasteningHardwareSettings().Outputs, new())
        { AutoResponseEnabled = false };
        using var motion = new VirtualMotionService(settings.Motion, new());
        using var bus = new VirtualAdcBus();
        using var shootingBus = new VirtualAdcBus();
        var gantry = VirtualTestSupport.CreateFastening(
            CreateAdcHead(shootingBus, io, FasteningHead.Shooting, new(), 2, "Virtual", 115200), CreateAdcHead(bus, io, FasteningHead.Pickup, new(), 1, "Virtual", 115200),
            io, motion, settings);
        io.Initialize();
        io.SetInput(InputIo.ShootingFeederBoltDetected, true);
        io.SetInput(InputIo.ShootingEscapeBackward, true);
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.ShootingEscapeForward)
                // Backward feedback deliberately never arrives after the first advance.
                io.SetInputs((InputIo.ShootingEscapeForward, on), (InputIo.ShootingEscapeBackward, false));
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var shot = gantry.ShootBoltAsync(stop.Token);
        Assert.True(io.GetOutput(OutputIo.ShootBolt));
        Assert.True(io.GetOutput(OutputIo.ShootingHeadVacuumPump));
        Assert.False(shot.IsCompleted);
        var arrival = Stopwatch.StartNew();
        io.SetInput(InputIo.ShootingTubeBoltDetected, true);
        await Task.Delay(20);
        Assert.True(io.GetInput(InputIo.ShootingEscapeForward));
        io.SetInput(InputIo.ShootingTubeBoltDetected, false);
        try
        {
            Assert.True(await WaitUntilAsync(() => !io.GetOutput(OutputIo.ShootingEscapeForward),
                TimeSpan.FromMilliseconds(100)));
            Assert.False(io.GetInput(InputIo.ShootingEscapeBackward));
            Assert.False(shot.IsCompleted); // Backward is commanded on passage; only arrival time remains.
            Assert.True(io.GetOutput(OutputIo.ShootBolt));
            if (stopDuringDelay)
            {
                // Passage was observed, but the arrival delay has not completed.
                await Task.Delay(30);
                Assert.False(shot.IsCompleted);
                stop.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => shot);
            }
            else
            {
                await shot.WaitAsync(TimeSpan.FromSeconds(1));
                Assert.True(arrival.Elapsed >= TimeSpan.FromSeconds(0.18));
            }
            Assert.False(io.GetInput(InputIo.ShootingHeadVacuumDetected));
            Assert.False(io.GetOutput(OutputIo.ShootBolt));
        }
        finally
        {
            stop.Cancel();
            await shot.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Fact]
    public async Task ShootingFeederRunsThroughEscapeTravelAndStopsAfterLatestDetection()
    {
        var settings = new BoltFeederSettings();
        Assert.Equal(3_000, settings.ShootingRunOnMilliseconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.ShootingRunOnMilliseconds = -1);
        settings.ShootingRunOnMilliseconds = 200;
        var io = new VirtualIoService(new BoltFeederHardwareSettings().Outputs, new())
        { AutoResponseEnabled = false };
        io.SetInput(InputIo.ShootingEscapeForward, true);
        io.SetInput(InputIo.ShootingFeederBoltDetected, false);
        io.SetInput(InputIo.PickupFeederBoltDetected, true);
        io.SetOutput(OutputIo.ShootingEscapeForward, true);
        io.SetOutput(OutputIo.ShootingFeederOff, true);
        var physicalEvents = new WriteNotifyingIo(io);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var feederWrites = 0;
        var shootingControlWrites = 0;
        physicalEvents.OutputChanged += (output, value) =>
        {
            if (output is OutputIo.ShootingEscapeForward or OutputIo.ShootBolt)
                Interlocked.Increment(ref shootingControlWrites);
            if (output == OutputIo.ShootingFeederOff && Interlocked.Increment(ref feederWrites) >= 50)
                stop.Cancel(); // Bound a self-waking regression instead of hanging the test.
        };
        var feeder = new BoltFeederUnit(physicalEvents, settings, new());
        var run = feeder.RunAsync(stop.Token);
        try
        {
            Assert.False(run.IsCompleted);
            Assert.False(io.GetOutput(OutputIo.ShootingFeederOff));
            var beforeReturn = Volatile.Read(ref feederWrites);
            await Task.Delay(50);
            Assert.Equal(beforeReturn, Volatile.Read(ref feederWrites));
            // Keep feeding while the escape is between its end sensors, too.
            io.SetInput(InputIo.ShootingEscapeForward, false);
            await Task.Delay(30);
            Assert.False(io.GetOutput(OutputIo.ShootingFeederOff));

            // Returning to BACKWARD does not start the run-on timer; detection does.
            io.SetInputs((InputIo.ShootingEscapeForward, false), (InputIo.ShootingEscapeBackward, true));
            await Task.Delay(250);
            Assert.False(io.GetOutput(OutputIo.ShootingFeederOff));
            io.SetInput(InputIo.ShootingFeederBoltDetected, true);
            await Task.Delay(100);
            Assert.False(io.GetOutput(OutputIo.ShootingFeederOff));
            // A detection loss restarts the full run-on period from the next ON edge.
            io.SetInput(InputIo.ShootingFeederBoltDetected, false);
            var detected = Stopwatch.StartNew();
            io.SetInput(InputIo.ShootingFeederBoltDetected, true);
            await Task.Delay(120);
            Assert.False(io.GetOutput(OutputIo.ShootingFeederOff));
            Assert.True(await WaitUntilAsync(() => io.GetOutput(OutputIo.ShootingFeederOff),
                TimeSpan.FromSeconds(1)));
            Assert.True(detected.ElapsedMilliseconds >= 190);
            Assert.True(io.GetInput(InputIo.ShootingFeederBoltDetected));
            // Other feeder and escape edges must not restart this bolt's run-on.
            io.SetInput(InputIo.PickupFeederBoltDetected, false);
            io.SetInput(InputIo.PickupFeederBoltDetected, true);
            io.SetInputs((InputIo.ShootingEscapeBackward, false), (InputIo.ShootingEscapeForward, true));
            await Task.Delay(50);
            Assert.True(io.GetOutput(OutputIo.ShootingFeederOff));
            var stoppedWrites = Volatile.Read(ref feederWrites);
            await Task.Delay(50);
            Assert.Equal(stoppedWrites, Volatile.Read(ref feederWrites));
            Assert.False(run.IsCompleted);
        }
        finally
        {
            stop.Cancel();
            await run;
        }
        Assert.Equal(0, shootingControlWrites);
    }

    [Fact]
    public async Task NewlyConsumedFeederBoltStartsANewEmptyTimeout()
    {
        var io = new VirtualIoService(new BoltFeederHardwareSettings().Outputs, new())
        { AutoResponseEnabled = false };
        io.SetInput(InputIo.PickupFeederBoltDetected, true);
        var monitoredIo = new WriteNotifyingIo(io);
        var feeder = new BoltFeederUnit(monitoredIo,
            new() { PickupTimeoutMilliseconds = 250 }, new() { ShootingBoltFeeder = false });
        using var stop = new CancellationTokenSource();
        monitoredIo.BeforeInputRead = input =>
        {
            if (input != InputIo.PickupFeederBoltDetected)
                return;
            monitoredIo.BeforeInputRead = null;
            // The bolt is consumed while the first sensor read is in progress.
            Thread.Sleep(300);
            io.SetInput(input, false);
        };

        var run = feeder.RunAsync(stop.Token);
        try
        {
            Assert.False(run.IsCompleted);
        }
        finally
        {
            stop.Cancel();
            await run.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
        await run;
    }

    [Fact]
    public async Task StopDuringFeederSensorReadDoesNotRaiseAnEmptyAlarm()
    {
        var io = new VirtualIoService(new BoltFeederHardwareSettings().Outputs, new())
        { AutoResponseEnabled = false };
        io.SetInput(InputIo.ShootingEscapeBackward, true);
        var monitoredIo = new WriteNotifyingIo(io);
        var feeder = new BoltFeederUnit(monitoredIo,
            new() { ShootingTimeoutMilliseconds = 50 }, new() { PickupBoltFeeder = false });
        using var stop = new CancellationTokenSource();
        monitoredIo.BeforeInputRead = input =>
        {
            if (input != InputIo.ShootingFeederBoltDetected)
                return;
            monitoredIo.BeforeInputRead = null;
            Thread.Sleep(80);
            stop.Cancel();
        };

        await feeder.RunAsync(stop.Token);

        Assert.False(feeder.IsRunning);
        Assert.True(io.GetOutput(OutputIo.ShootingFeederOff));
    }

    [Fact]
    public async Task ShootingFeederMissingBoltTimesOutAfterEscapeReturnsAndStopsFeeding()
    {
        var io = new VirtualIoService(new BoltFeederHardwareSettings().Outputs, new())
        { AutoResponseEnabled = false };
        var feeder = new BoltFeederUnit(io,
            new() { ShootingTimeoutMilliseconds = 50 }, new() { PickupBoltFeeder = false });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var run = feeder.RunAsync(stop.Token);
        try
        {
            await Task.Delay(100);
            Assert.False(run.IsCompleted);
            Assert.False(io.GetOutput(OutputIo.ShootingFeederOff));
            io.SetInput(InputIo.ShootingEscapeBackward, true);
            await Assert.ThrowsAsync<IoTimeoutException>(() => run);
            Assert.True(io.GetOutput(OutputIo.ShootingFeederOff));
        }
        finally
        {
            stop.Cancel();
            await run.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Fact]
    public async Task PickupEmptyAlarmKeepsShootingRunOnAndTimeoutActive()
    {
        var io = new VirtualIoService(new BoltFeederHardwareSettings().Outputs, new())
        { AutoResponseEnabled = false };
        io.SetInput(InputIo.ShootingEscapeBackward, true);
        io.SetInput(InputIo.ShootingFeederBoltDetected, true);
        var feeder = new BoltFeederUnit(io, new()
        {
            PickupTimeoutMilliseconds = 0,
            ShootingTimeoutMilliseconds = 200,
            ShootingRunOnMilliseconds = 50,
        }, new());
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var run = feeder.RunAsync(stop.Token);
        try
        {
            Assert.NotNull(feeder.PickupEmptyAlarm);
            Assert.Null(feeder.ShootingEmptyAlarm);
            Assert.False(run.IsCompleted);
            Assert.False(io.GetOutput(OutputIo.ShootingFeederOff));
            Assert.True(await WaitUntilAsync(() => io.GetOutput(OutputIo.ShootingFeederOff),
                TimeSpan.FromSeconds(1)));
            io.SetInput(InputIo.ShootingFeederBoltDetected, false);
            Assert.True(await WaitUntilAsync(() => !io.GetOutput(OutputIo.ShootingFeederOff),
                TimeSpan.FromSeconds(1)));
            await Assert.ThrowsAsync<IoTimeoutException>(() => run);
            Assert.Equal(InputIo.ShootingFeederBoltDetected, feeder.ShootingEmptyAlarm?.Input);
            Assert.True(io.GetOutput(OutputIo.ShootingFeederOff));
        }
        finally
        {
            stop.Cancel();
            await run.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Fact]
    public async Task StopDuringShootingFeederRunOnStopsImmediately()
    {
        var io = new VirtualIoService(new BoltFeederHardwareSettings().Outputs, new())
        { AutoResponseEnabled = false };
        io.SetInput(InputIo.ShootingEscapeBackward, true);
        io.SetInput(InputIo.ShootingFeederBoltDetected, false);
        var feeder = new BoltFeederUnit(io, new(), new() { PickupBoltFeeder = false });
        using var stop = new CancellationTokenSource();
        var run = feeder.RunAsync(stop.Token);
        io.SetInput(InputIo.ShootingFeederBoltDetected, true);
        Assert.False(io.GetOutput(OutputIo.ShootingFeederOff));

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(io.GetOutput(OutputIo.ShootingFeederOff));
    }

    [Fact]
    public async Task ShootingEscapeDoesNotWaitForFeederRunOn()
    {
        var io = new VirtualIoService(
            Outputs(new BoltFeederHardwareSettings(), new BoltFasteningHardwareSettings()), new())
        { AutoResponseEnabled = false };
        io.SetInput(InputIo.ShootingEscapeBackward, true);
        io.SetInput(InputIo.ShootingFeederBoltDetected, false);
        var feeder = new BoltFeederUnit(io, new(), new() { PickupBoltFeeder = false });
        var settings = new BoltFasteningSettings { ShootingArrivalDelaySeconds = 0 };
        using var motion = new VirtualMotionService(settings.Motion, new());
        using var bus = new VirtualAdcBus();
        using var shootingBus = new VirtualAdcBus();
        var station = CreateFastening(
            CreateAdcHead(shootingBus, io, FasteningHead.Shooting, new(), 2, "Virtual", 115200), CreateAdcHead(bus, io, FasteningHead.Pickup, new(), 1, "Virtual", 115200),
            io, motion, settings);
        var forwarded = false;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.ShootingEscapeForward)
            {
                forwarded |= on;
                io.SetInputs((InputIo.ShootingEscapeForward, on), (InputIo.ShootingEscapeBackward, !on));
            }
            if (output == OutputIo.ShootBolt && on)
            {
                io.SetInput(InputIo.ShootingTubeBoltDetected, true);
                io.SetInput(InputIo.ShootingTubeBoltDetected, false);
            }
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var run = feeder.RunAsync(stop.Token);
        try
        {
            io.SetInput(InputIo.ShootingFeederBoltDetected, true);
            Assert.False(io.GetOutput(OutputIo.ShootingFeederOff));
            await station.ShootBoltAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(1));

            Assert.True(forwarded);
            Assert.True(io.GetInput(InputIo.ShootingEscapeBackward));
            Assert.False(io.GetOutput(OutputIo.ShootBolt));
        }
        finally
        {
            stop.Cancel();
            await run;
        }
    }

    [Fact]
    public async Task ShootingFeederKeepsTheNextBoltReady()
    {
        var io = new VirtualIoService(
            Outputs(new BoltFeederHardwareSettings(), new BoltFasteningHardwareSettings()), new MachineOptions());
        _ = new VirtualMachine(io, []);
        var feeder = new BoltFeederUnit(
            io,
            new BoltFeederSettings { ShootingTimeoutMilliseconds = 500, ShootingRunOnMilliseconds = 30 },
            new() { PickupBoltFeeder = false });
        var settings = new BoltFasteningSettings();
        using var motion = new VirtualMotionService(settings.Motion, new());
        using var bus = new VirtualAdcBus();
        var head = CreateAdcHead(bus, io, FasteningHead.Shooting, new(), 1, "Virtual", 115200);
        var station = CreateFastening(head, head, io, motion, settings);
        var refillCount = 0;
        var runCount = 0;
        io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.ShootingFeederOff && !value)
            {
                Interlocked.Increment(ref runCount);
            }
        };
        io.InputChanged += (input, value) =>
        {
            if (input == InputIo.ShootingFeederBoltDetected && value)
            {
                Interlocked.Increment(ref refillCount);
            }
        };

        io.Initialize();
        // Only the bolt station returns an escape left forward; feeder start/stop must not move it.
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.ShootingEscapeForward, true);
        feeder.Stop();
        Assert.True(io.GetOutput(OutputIo.ShootingFeederOff));
        Assert.True(io.GetOutput(OutputIo.ShootingEscapeForward));
        using var cancellation = new CancellationTokenSource();
        var run = feeder.RunAsync(cancellation.Token);
        Assert.True(io.GetOutput(OutputIo.ShootingEscapeForward));
        var stationRun = station.RunAsync(cancellation.Token);
        try
        {
            Assert.True(await WaitUntilAsync(
                () => runCount == 1
                    && io.GetInput(InputIo.ShootingFeederBoltDetected)
                    && io.GetOutput(OutputIo.ShootingFeederOff),
                TimeSpan.FromSeconds(1)));
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.ShootingEscapeForward, true);
            // Keep feeding while escape is forward, without starting the refill timeout yet.
            await Task.Delay(600);
            Assert.False(run.IsCompleted);
            Assert.False(io.GetInput(InputIo.ShootingFeederBoltDetected));
            Assert.False(io.GetOutput(OutputIo.ShootingFeederOff));
            Assert.Equal(2, runCount);
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.ShootingEscapeForward, false);
            Assert.True(await WaitUntilAsync(
                () => runCount == 2
                    && refillCount == 2
                    && io.GetInput(InputIo.ShootingFeederBoltDetected)
                    && io.GetOutput(OutputIo.ShootingFeederOff),
                TimeSpan.FromSeconds(1)));
            Assert.True(io.GetInput(InputIo.ShootingEscapeBackward));
            Assert.False(io.GetOutput(OutputIo.ShootingEscapeForward));
        }
        finally
        {
            cancellation.Cancel();
            await Task.WhenAll(run, stationRun);
        }
        Assert.True(io.GetOutput(OutputIo.ShootingFeederOff));
    }

    [Theory]
    [InlineData(FasteningHead.Pickup, false, false)]
    [InlineData(FasteningHead.Shooting, false, false)]
    [InlineData(FasteningHead.Pickup, false, true)]
    [InlineData(FasteningHead.Shooting, false, true)]
    [InlineData(FasteningHead.Pickup, true, false)]
    [InlineData(FasteningHead.Pickup, true, true)]
    public async Task HeadDownMustCompleteBeforeRun(FasteningHead boltHead, bool final, bool cancel)
    {
        var settings = new BoltFasteningSettings
        {
            ShootingPickupSpinMilliseconds = 0,
            ShootingPreliminaryZ = 12,
            FirstFasteningHead = FasteningHead.Pickup,
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
        };
        var io = new VirtualIoService(Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()),
            new() { TimeoutMilliseconds = 1_000 });
        io.Initialize();
        using var motion = new VirtualMotionService(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var pickupBus = new AdcControllerStub();
        using var shootingBus = new AdcControllerStub();
        var pickup = CreateAdcHead(pickupBus, io, FasteningHead.Pickup, new(), 1, "Pickup", 115200);
        var shooting = CreateAdcHead(shootingBus, io, FasteningHead.Shooting, new(), 1, "Shooting", 115200);
        var bolt = new BoltPoint
        {
            Head = boltHead, FasteningX = 10, FasteningY = 10,
            PreliminaryFasteningPosition = new() { X = 30, Y = 20 },
        };
        var units = new UnitSettings { PickupBoltFeeder = false, ShootingBoltFeeder = false };
        var work = ConveyorStation.CreateBoltFastening(io);
        var station = new BoltFasteningStation(shooting, pickup, io, motion, new(motion), settings, work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = new() { BoltPoints = [bolt] } } },
            units, new BoltFeederUnit(io, new(), units));
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        var preliminary = new BoltResult(true, 1) { Stage = BoltFasteningStage.Preliminary };
        if (final)
            assembly.RecordBolt(FasteningHead.Pickup, bolt.Id, preliminary);
        var tool = final ? FasteningHead.Pickup : FasteningHead.Shooting;
        var output = tool == FasteningHead.Pickup ? OutputIo.PickupHeadDown : OutputIo.ShootingHeadDown;
        var up = tool == FasteningHead.Pickup ? InputIo.PickupHeadUp : InputIo.ShootingHeadUp;
        var down = tool == FasteningHead.Pickup ? InputIo.PickupHeadDown : InputIo.ShootingHeadDown;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        io.OutputChanged += (signal, on) =>
        {
            if (signal != output || !on)
                return;
            Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
            Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
            io.AutoResponseEnabled = false;
            io.SetInputs((up, false), (down, false));
            if (cancel)
                stop.Cancel();
        };
        var run = station.RunAsync(stop.Token, selectedBolts: [bolt.Id]);
        if (cancel)
            await run;
        else
            Assert.Equal(down, (await Assert.ThrowsAsync<IoTimeoutException>(() => run)).Input);
        Assert.Equal(0, pickupBus.StartWrites);
        Assert.Equal(0, shootingBus.StartWrites);
        Assert.False(motion.IsMoving);
        Assert.False(work.Completed);
        if (final)
            Assert.Same(preliminary, assembly.PickupBoltResults[bolt.Id]);
        else
        {
            Assert.Empty(assembly.PickupBoltResults);
            Assert.Empty(assembly.ShootingBoltResults);
        }
    }

    [Fact]
    public async Task RestartAtFirstPickupBoltUsesCurrentVacuumWithoutAnotherPickup()
    {
        var settings = new BoltFasteningSettings
        {
            ShootingPickupSpinMilliseconds = 0,
            ShootingPreliminaryZ = 12,
            SafeZ = 5,
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
            DryRunMilliseconds = 30,
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
        settings.PickupHead.FasteningZ = 10;
        var controllerSettings = new IoBoltHardwareSettings();
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings(), controllerSettings),
            new())
        { AutoResponseEnabled = false };
        using var motion = new VirtualMotionService(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var shootingBus = new AdcControllerStub();
        var shooting = CreateAdcHead(shootingBus, io, FasteningHead.Shooting, new(), 1, "Virtual", 115200);
        using var bus = new AdcControllerStub();
        IBoltHead pickup = CreateAdcHead(bus, io, FasteningHead.Pickup, new HantasSettings(), 1, "Virtual", 115200);

        var work = ConveyorStation.CreateBoltFastening(io);
        var layout = new PcbLayout { BoltPoints = [Bolt(1, FasteningHead.Pickup, 10, 10)] };
        var units = new UnitSettings();
        var station = new BoltFasteningStation(shooting,
            pickup,
            io,
            motion, new MotionStatus(motion),
            settings,
            work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = layout } },
            units,
            new BoltFeederUnit(io, new(), units));
        io.SetInputs(
            (InputIo.BoltFasteningHeatSink1Present, true),
            (InputIo.BoltFasteningBackupPlateUp, true),
            (InputIo.BoltFasteningStopperDown, true),
            (InputIo.PickupHeadUp, true),
            (InputIo.ShootingHeadUp, true),
            (InputIo.ShootingHeadDown, false),
            (InputIo.ShootingHeadVacuumDetected, true));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var interruptDescent = true;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.ShootingHeadDown)
            {
                if (on && interruptDescent)
                {
                    io.SetInput(InputIo.ShootingHeadUp, false);
                    stop.Cancel();
                    return;
                }
                io.SetInputs((InputIo.ShootingHeadUp, !on), (InputIo.ShootingHeadDown, on));
            }
            if (output == OutputIo.ShootingHeadVacuumPump && !on)
                io.SetInput(InputIo.ShootingHeadVacuumDetected, false);
        };
        io.SetInputs((InputIo.PickupTableUp, true), (InputIo.PickupTableDown, false));
        io.SetOutput(OutputIo.ShootingHeadVacuumPump, true);
        var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        Assert.Equal(BoltFasteningState.PreparingCarrier, station.NextStep);
        await station.RunAsync(stop.Token);
        interruptDescent = false;
        Assert.Empty(assembly.PickupBoltResults);

        // This recipe has one bolt. Current vacuum confirms a bolt is still on the head.
        units.PickupBoltFeeder = false;
        Assert.Empty(assembly.PickupBoltResults);
        Assert.True(io.GetOutput(OutputIo.ShootingHeadDown));
        Assert.Equal(0, shootingBus.StartWrites);
        var job = work.CurrentJob;
        var clearedBeforeRestart = false;
        var restarted = false;
        motion.PositionChanged += (x, y, z) =>
        {
            if (!restarted)
            {
                if (motion.IsMovingHorizontal)
                {
                    Assert.True(clearedBeforeRestart);
                    Assert.Equal(settings.SafeZ, z);
                    Assert.Equal(StationCylinderState.Down, station.ShootingHeadPosition);
                }
                clearedBeforeRestart |= z == settings.SafeZ;
            }
        };
        using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.ShootingBoltStart && on)
            {
                restarted = true;
            }
        };
        assembly.ResultsChanged += updated =>
        {
            if (updated.PickupBoltResults.ContainsKey(VirtualTestSupport.BoltId(1)))
                finish.Cancel();
        };
        await station.RunAsync(finish.Token);
        Assert.True(clearedBeforeRestart);
        Assert.Same(job, work.CurrentJob);
        Assert.Same(assembly, Assert.Single(work.Assemblies));
        Assert.True(work.CarrierPresent);
        Assert.Equal(
            BoltResultSource.DryRun,
            assembly.PickupBoltResults[VirtualTestSupport.BoltId(1)].Source);
        Assert.True(assembly.PickupBoltResults[VirtualTestSupport.BoltId(1)].Success);
        Assert.Equal(1, shootingBus.StartWrites);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartBeginsAtFirstConfiguredBoltAndChecksVacuumAtThePickupTurn(bool pickupAlreadyLoaded)
    {
        var settings = new BoltFasteningSettings
        {
            ShootingPickupSpinMilliseconds = 0,
            ShootingPreliminaryZ = 12,
            SafeZ = 5,
            DryRunMilliseconds = 30,
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
            PickupPosition = new() { X = 100, Y = 100, Z = 10 },
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
        settings.PickupHead.FasteningZ = 12;
        settings.ShootingHead.FasteningZ = 12;
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()), new());
        io.Initialize();
        using var motion = new VirtualMotionService(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var firstStop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        BoltFasteningStation? station = null;
        var starts = new List<Guid>();
        using var bus = new AdcControllerStub
        {
            Started = () =>
            {
                starts.Add(station!.ActiveBolt!.Id);
                if (starts.Count == 2)
                    firstStop.Cancel();

            },
        };
        var head = CreateAdcHead(bus, io, FasteningHead.Shooting, new(), 1, "Virtual", 115200);
        var units = new UnitSettings { PickupBoltFeeder = false, ShootingBoltFeeder = false };
        var work = ConveyorStation.CreateBoltFastening(io);
        var layout = new PcbLayout
        {
            BoltPoints = [Bolt(1, FasteningHead.Shooting, 20, 30), Bolt(2, FasteningHead.Shooting, 40, 30),
                Bolt(3, FasteningHead.Pickup, 60, 30),
                new() { Id = VirtualTestSupport.BoltId(4, HeatSinkSlot.HeatSink2), HeatSink = HeatSinkSlot.HeatSink2 }],
            FasteningOrder = [VirtualTestSupport.BoltId(2), VirtualTestSupport.BoltId(4, HeatSinkSlot.HeatSink2),
                VirtualTestSupport.BoltId(1), VirtualTestSupport.BoltId(3)],
        };
        station = new(head, head, io, motion, new(motion), settings,
            work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = layout } }, units, new(io, new(), units));
        SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        await station.RunAsync(firstStop.Token);
        Assert.Equal(new[] { VirtualTestSupport.BoltId(2), VirtualTestSupport.BoltId(1) }, starts);
        var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        Assert.Equal(2, assembly.ShootingBoltResults.Count);
        Assert.Equal(BoltResultSource.Interrupted, assembly.ShootingBoltResults[VirtualTestSupport.BoltId(1)].Source);
        assembly.ChangeBoltResult(FasteningHead.Shooting, VirtualTestSupport.BoltId(1), null);
        Assert.Single(assembly.ShootingBoltResults);
        var firstResult = assembly.ShootingBoltResults[VirtualTestSupport.BoltId(2)];

        assembly.ResultsChanged += updated =>
        {
            if (pickupAlreadyLoaded && starts.Count == 4
                && updated.ShootingBoltResults.GetValueOrDefault(VirtualTestSupport.BoltId(1)) is { IsComplete: true })
            {
                io.SetOutput(OutputIo.ShootingHeadVacuumPump, true);
                io.SetInput(InputIo.ShootingHeadVacuumDetected, true);
            }
        };

        // The next run must ignore the recorded first bolt when choosing where to start.
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.ShootingHeadVacuumPump && !on)
                io.SetInput(InputIo.ShootingHeadVacuumDetected, false);
        };
        var visitedPickup = false;
        motion.PositionChanged += (x, y, z) =>
        {
            if (x == settings.PickupPosition.X && y == settings.PickupPosition.Y)
            {
                visitedPickup = true;
                Assert.Equal(VirtualTestSupport.BoltId(3), station.ActiveBolt!.Id);
            }
        };
        using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        work.Changed += () =>
        {
            if (work.Completed)
                finish.Cancel();
        };
        await station.RunAsync(finish.Token);
        Assert.True(work.Completed,
            $"State={station.NextStep}, starts={string.Join(',', starts)}, pickup={station.PickupHeadPosition}, "
            + $"vacuum={io.GetInput(InputIo.ShootingHeadVacuumDetected)}, XY={motion.Position}, visitedPickup={visitedPickup}");
        Assert.Equal(new[] { VirtualTestSupport.BoltId(2), VirtualTestSupport.BoltId(1), VirtualTestSupport.BoltId(2), VirtualTestSupport.BoltId(1), VirtualTestSupport.BoltId(3) }, starts);
        Assert.NotSame(firstResult, assembly.ShootingBoltResults[VirtualTestSupport.BoltId(2)]);
        Assert.Equal(!pickupAlreadyLoaded, visitedPickup);
        Assert.Single(assembly.PickupBoltResults);
        Assert.Equal(StationCylinderState.Up, station.PickupHeadPosition);
        using var completedStop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        station.StepChanged += () =>
        {
            if (station.Step is BoltFasteningState.Waiting or BoltFasteningState.PreparingCarrier)
                completedStop.Cancel();
        };
        await station.RunAsync(completedStop.Token);
        Assert.True(work.Completed);
        Assert.Equal(5, starts.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartDoesNotCollectThePreviousBoltResult(bool replaceCarrier)
    {
        var settings = new BoltFasteningSettings
        {
            ShootingPickupSpinMilliseconds = 0,
            ShootingPreliminaryZ = 12,
            SafeZ = 0,
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()),
            new MachineOptions())
        { AutoResponseEnabled = false };
        using var motion = new VirtualMotionService(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var bus = new VirtualAdcBus();
        using var shootingBus = new VirtualAdcBus();
        var pickupHead = CreateAdcHead(bus, io, FasteningHead.Pickup, new HantasSettings(), 1, "Virtual", 115200);

        var work = ConveyorStation.CreateBoltFastening(io);
        var layout = new PcbLayout
        {
            BoltPoints = [Bolt(1, FasteningHead.Pickup, 0, 0)],
        };
        var station = new BoltFasteningStation(CreateAdcHead(shootingBus, io, FasteningHead.Shooting, new HantasSettings(), 2, "Virtual", 115200),
            pickupHead,
            io,
            motion, new MotionStatus(motion),
            settings,
            work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = layout } },
            new(),
            new BoltFeederUnit(io, new(), new()));
        VirtualTestSupport.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningBackupPlateUp, true);
        io.SetInput(InputIo.BoltFasteningStopperDown, true);
        io.SetInput(InputIo.BoltFasteningStopperUp, false);
        io.SetInput(InputIo.PickupHeadUp, true);
        io.SetInput(InputIo.ShootingHeadUp, true);
        io.SetInput(InputIo.ShootingHeadDown, false);
        io.SetInput(InputIo.ShootingHeadVacuumDetected, true);
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.ShootingHeadDown)
                io.SetInputs((InputIo.ShootingHeadUp, !on), (InputIo.ShootingHeadDown, on));
            if (output == OutputIo.ShootingHeadVacuumPump && !on)
                io.SetInput(InputIo.ShootingHeadVacuumDetected, false);
        };
        io.SetInputs((InputIo.PickupTableUp, true), (InputIo.PickupTableDown, false));
        io.SetOutput(OutputIo.ShootingHeadVacuumPump, true);
        var originalAssembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        Assert.Equal(BoltFasteningState.PreparingCarrier, station.NextStep);

        var responseError = new IOException("Completed fastening response lost.");
        var loseResult = true;
        var starts = 0;
        using var resumedStop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        shootingBus.FrameTransferred += (direction, frame) =>
        {
            if (direction == AdcFrameDirection.Receive
                && frame[1] == (byte)AdcFunctionCode.ReadInputRegisters
                && frame[2] == AdcFasteningResult.RegisterCount * 2
                && BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(3)) != 0
                && loseResult)
            {
                loseResult = false;
                throw responseError;
            }

        };
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.ShootingBoltStart && on)
                starts++;
        };
        shootingBus.SetNextFasteningResult(2, AdcEventStatus.FasteningNg);
        using var firstStop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Same(responseError, await Assert.ThrowsAsync<IOException>(
            () => station.RunAsync(firstStop.Token)));
        var interrupted = Assert.Single(originalAssembly.PickupBoltResults).Value;
        Assert.Equal(BoltResultSource.Interrupted, interrupted.Source);
        Assert.False((await ((IAdcBus)shootingBus).ReadControllerStatusAsync(2)).Status!.Running);

        if (replaceCarrier)
        {
            VirtualTestSupport.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
            station.Station.ClearJob();
            VirtualTestSupport.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        }
        else
        {
            originalAssembly.ChangeBoltResult(FasteningHead.Pickup, VirtualTestSupport.BoltId(1), null);
            io.SetInput(InputIo.ShootingHeadDown, false);
            io.SetInput(InputIo.ShootingHeadUp, true);
            Assert.Equal(BoltFasteningState.PreparingCarrier, station.NextStep);
            Assert.Null(station.ActiveBolt);
        }

        var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        assembly.ResultsChanged += updated =>
        {
            if (updated.PickupBoltResults.ContainsKey(VirtualTestSupport.BoltId(1)))
                resumedStop.Cancel();
        };
        io.SetOutput(OutputIo.ShootingHeadVacuumPump, true);
        io.SetInput(InputIo.ShootingHeadVacuumDetected, true);
        await station.RunAsync(resumedStop.Token);
        Assert.True(assembly.PickupBoltResults[VirtualTestSupport.BoltId(1)].Success);
        Assert.Equal(2, starts);
        if (replaceCarrier)
        {
            Assert.NotSame(originalAssembly, assembly);
            Assert.Same(interrupted, Assert.Single(originalAssembly.PickupBoltResults).Value);
        }
        else
        {
            Assert.Single(assembly.PickupBoltResults);
        }
    }

    [Theory]
    [InlineData(false, false, FasteningHead.Shooting)]
    [InlineData(true, false, FasteningHead.Shooting)]
    [InlineData(true, true, FasteningHead.Shooting)]
    [InlineData(false, false, FasteningHead.Pickup)]
    public async Task StartupStandbyStopsDuringZRetractionAndRestartsThroughZeroBeforeXy(
        bool carrierSeated, bool selectedTest, FasteningHead firstHead)
    {
        var settings = new BoltFasteningSettings
        {
            ShootingPickupSpinMilliseconds = 0,
            FirstFasteningHead = firstHead,
            PickupPosition = new() { X = 30, Y = 40, Z = 10 },
            SafeZ = 5,
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 100 },
        };
        var io = new VirtualIoService(Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()), new());
        using var motion = new VirtualMotionService(settings.Motion, new());
        using var bus = new VirtualAdcBus();
        var head = CreateAdcHead(bus, io, FasteningHead.Shooting, new(), 1, "Virtual", 115200);
        var recipes = new RecipeManager(OpenMachineStore(), new())
        {
            Current = { Pcb = new() { BoltPoints = [Bolt(1, FasteningHead.Shooting, 10, 20)] } },
        };
        var station = new BoltFasteningStation(head, head, io, motion, new(motion), settings,
            ConveyorStation.CreateBoltFastening(io), recipes, new(),
            new BoltFeederUnit(io, new(), new()));
        io.Initialize();
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        if (carrierSeated)
        {
            SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
            await station.Station.SeatAsync(CancellationToken.None);
        }
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PickupTableDown, true);
        await motion.MoveAxisAsync(MotionAxis.Z, 20, 20_000);
        Guid[]? selectedBolts = selectedTest ? [VirtualTestSupport.BoltId(1)] : null;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        void StopDuringRetraction(double x, double y, double z)
        {
            if (z < 20)
                stop.Cancel();
        }
        motion.PositionChanged += StopDuringRetraction;
        try
        {
            await station.RunAsync(stop.Token, selectedBolts: selectedBolts);
            Assert.True(stop.IsCancellationRequested);
            Assert.Equal((0d, 0d), (motion.Position.X, motion.Position.Y));
            Assert.False(motion.IsMoving);
        }
        finally
        {
            motion.PositionChanged -= StopDuringRetraction;
        }

        var xyAtZero = false;
        var loweredAfterXy = false;
        using var restartedStop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        station.StepChanged += () =>
        {
            if (firstHead == FasteningHead.Pickup && station.Step is BoltFasteningState.Waiting)
                restartedStop.Cancel();
        };
        motion.PositionChanged += (x, y, z) =>
        {
            if (motion.IsMovingHorizontal)
            {
                Assert.Equal(0, z);
                xyAtZero = true;
            }
            else if (motion.IsMoving && xyAtZero)
            {
                Assert.Equal((10d, 20d), (x, y));
                loweredAfterXy = true;
                if (z == settings.SafeZ)
                    restartedStop.Cancel();
            }
        };
        await station.RunAsync(restartedStop.Token, selectedBolts: selectedBolts);
        Assert.True(xyAtZero);
        Assert.Equal(firstHead == FasteningHead.Shooting, loweredAfterXy);
        Assert.Equal(firstHead == FasteningHead.Shooting ? (10d, 20d, 5d) : (30d, 40d, 0d), motion.Position);
        Assert.False(motion.IsMoving);
        Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.All(station.Station.Assemblies, assembly => Assert.Empty(assembly.ShootingBoltResults));
    }

    [Theory]
    [InlineData(FasteningHead.Shooting, false)]
    [InlineData(FasteningHead.Pickup, true)]
    public async Task FasteningKeepsPcbOrderAndPickupTableClearance(FasteningHead firstHead, bool retighten)
    {
        var settings = new BoltFasteningSettings
        {
            ShootingPickupSpinMilliseconds = 0,
            ShootingPreliminaryZ = 12,
            FirstFasteningHead = firstHead,
            IpmRetighteningEnabled = retighten,
            ShootingArrivalDelaySeconds = 0.05,
            DryRunMilliseconds = 30,
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
            SafeZ = 5,
            PickupPosition = new() { X = 100, Y = 50, Z = 10 },
            ShootingHead = HeadSettings(),
            PickupHead = HeadSettings(),
        };
        settings.ShootingHead.FasteningZ = 12;
        settings.PickupHead.FasteningZ = 16;
        settings.ShootingHead.UpperLeftLocatingPin = new() { X = 300, Y = 400 };
        settings.ShootingHead.LowerRightLocatingPin = new() { X = 300, Y = 440 };
        settings.PickupHead.UpperLeftLocatingPin = new() { X = -50, Y = 250 };
        settings.PickupHead.LowerRightLocatingPin = new() { X = -50, Y = 210 };
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()), new());
        using var motion = new VirtualMotionService(settings.Motion, operationCancellation: new());
        using var bus = new VirtualAdcBus();
        using var shootingBus = new VirtualAdcBus();

        var layout = new PcbLayout
        {
            BoltPoints = [
                new() { Id = VirtualTestSupport.BoltId(1), Head = FasteningHead.Shooting, X = 110, Y = 220 },
                new() { Id = VirtualTestSupport.BoltId(2), Head = FasteningHead.Pickup, X = 115, Y = 225, PreliminaryFasteningPosition = new() { X = 275, Y = 415 } },
                new() { Id = VirtualTestSupport.BoltId(1, HeatSinkSlot.HeatSink2), HeatSink = HeatSinkSlot.HeatSink2, Head = FasteningHead.Shooting, X = 120, Y = 230 },
                new() { Id = VirtualTestSupport.BoltId(2, HeatSinkSlot.HeatSink2), HeatSink = HeatSinkSlot.HeatSink2, Head = FasteningHead.Pickup, X = 125, Y = 235, PreliminaryFasteningPosition = new() { X = 265, Y = 425 } },
            ],
            FasteningOrder = [VirtualTestSupport.BoltId(1, HeatSinkSlot.HeatSink2), VirtualTestSupport.BoltId(1),
                VirtualTestSupport.BoltId(2, HeatSinkSlot.HeatSink2), VirtualTestSupport.BoltId(2)],
        };
        var reference = new CarrierReferenceSettings
        {
            UpperLeftLocatingPin = new() { X = 100, Y = 200 },
            LowerRightLocatingPin = new() { X = 140, Y = 200 },
        };
        foreach (var bolt in layout.BoltPoints)
            settings.InitializeBoltPosition(bolt, reference);
        var work = ConveyorStation.CreateBoltFastening(io);
        var station = new BoltFasteningStation(CreateAdcHead(shootingBus, io, FasteningHead.Shooting, new HantasSettings(), 2, "Virtual", 115200),
            CreateAdcHead(bus, io, FasteningHead.Pickup, new HantasSettings(), 1, "Virtual", 115200),
            io,
            motion, new MotionStatus(motion),
            settings,
            work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = layout } },
            new() { PickupBoltFeeder = false, ShootingBoltFeeder = false },
            new BoltFeederUnit(io, new(), new() { PickupBoltFeeder = false, ShootingBoltFeeder = false }));
        io.Initialize();
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PickupTableDown, true, CancellationToken.None);
        await motion.MoveAxisAsync(MotionAxis.Z, 20, settings.Motion.ZSpeed);
        await shootingBus.WriteRegisterAsync(2, (ushort)AdcRemoteRegister.Preset, 4);
        await bus.WriteRegisterAsync(1, (ushort)AdcRemoteRegister.Preset, 5);
        var presets = new List<(byte Head, ushort Preset)>();
        var starts = new List<(byte Head, double X, double Y, double Z)>();
        var pickups = 0;
        var tableDescents = 0;
        io.OutputChanged += (output, on) =>
        {
            if (!on)
                return;
            if (output is OutputIo.PickupBoltPreset1 or OutputIo.ShootingBoltPreset1)
                presets.Add(((byte)(output == OutputIo.PickupBoltPreset1 ? 1 : 2), 1));
            if (output == OutputIo.ShootingBoltPreset2)
                presets.Add((2, 2));
            if (output is not (OutputIo.PickupBoltStart or OutputIo.ShootingBoltStart))
                return;
            var head = (byte)(output == OutputIo.PickupBoltStart ? 1 : 2);
            var position = motion.Position;
            starts.Add((head, position.X, position.Y, position.Z));
            Assert.Equal(head == 2 ? StationCylinderState.Up : StationCylinderState.Down, station.PickupTablePosition);
        };
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PickupTableDown && on)
                tableDescents++;
            if (output == OutputIo.PickupHeadVacuumPump && on)
            {
                pickups++;
            }

        };
        var standbyPosition = firstHead == FasteningHead.Shooting ? (280d, 410d, 5d) : (100d, 50d, 0d);
        var startupXyMoved = false;
        motion.PositionChanged += (_, _, z) =>
        {
            if (motion.IsMovingHorizontal)
            {
                var startup = starts.Count == 0 && station.ActiveBolt is null;
                startupXyMoved |= startup;
                var expectedZ = startup ? 0
                    : station.ActiveBolt is null ? standbyPosition.Item3
                    : settings.SafeZ;
                Assert.Equal(expectedZ, z);
                Assert.True(station.IsHorizontalMoveAllowed);
                Assert.Equal(station.ActiveStage is BoltFasteningStage.Final or BoltFasteningStage.FinalBeforeRetightening
                    ? StationCylinderState.Down : StationCylinderState.Up, station.PickupTablePosition);
            }
            if (firstHead == FasteningHead.Pickup && startupXyMoved && !work.CarrierSeated)
                Assert.Equal(0, z);
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = station.RunAsync(stop.Token);
        try
        {
            Assert.True(await WaitUntilAsync(
                () => station.Step is BoltFasteningState.Waiting, TimeSpan.FromSeconds(2)));
            Assert.Equal(standbyPosition, motion.Position);
            Assert.True(startupXyMoved);
            Assert.Empty(starts);
            Assert.Equal(StationCylinderState.Up, station.PickupTablePosition);
            io.SetInputs(
                (InputIo.BoltFasteningHeatSink1Present, true),
                (InputIo.BoltFasteningHeatSink2Present, true));
            Assert.True(await WaitUntilAsync(
                () => station.Step is BoltFasteningState.Waiting, TimeSpan.FromSeconds(1)));
            Assert.Empty(starts); // No descent while the carrier is still on the belt.
            Assert.Equal(standbyPosition, motion.Position);
            await work.SeatAsync(CancellationToken.None);
            Assert.True(await WaitUntilAsync(() => work.Completed || run.IsCompleted, TimeSpan.FromSeconds(8)));
            Assert.True(work.Completed, run.Exception?.ToString() ?? station.NextStep.ToString());
            Assert.Equal(standbyPosition, motion.Position);
            Assert.Equal(StationCylinderState.Up, station.PickupTablePosition);
            var expected = firstHead == FasteningHead.Shooting
                ? new (byte Head, double X, double Y, double Z)[] {
                    (2, 280, 410, 12), (2, 270, 420, 12), (2, 275, 415, 12), (2, 265, 425, 12),
                    (1, -15, 225, 16), (1, -25, 235, 16),
                }
                : [(2, 275, 415, 12), (2, 265, 425, 12),
                    (1, -15, 225, 16), (1, -15, 225, 16), (1, -25, 235, 16), (1, -25, 235, 16),
                    (2, 280, 410, 12), (2, 270, 420, 12)];
            Assert.Equal(expected, starts);
            // Matching preset outputs are retained; each operation still checks current preset/READY feedback.
            Assert.Equal(firstHead == FasteningHead.Shooting
                ? new (byte, ushort)[] { (2, 1), (2, 2), (1, 1) } : [(2, 2), (1, 1), (2, 1)], presets);
            Assert.Equal(0, pickups); // Feeder OFF preserves pickup travel without vacuum ON.
            Assert.Equal(1, tableDescents);
            Assert.All(work.Assemblies, assembly =>
            {
                var final = Assert.Single(assembly.PickupBoltResults).Value;
                Assert.Equal(retighten ? BoltFasteningStage.Retightening : BoltFasteningStage.Final, final.Stage);
                Assert.Equal(BoltResultSource.DryRun, final.Source);
                Assert.True(final.IsComplete);
                var preliminary = (final.PreviousFinalResult ?? final).PreliminaryResult!;
                Assert.Equal(BoltResultSource.DryRun, preliminary.Source);
                Assert.False(preliminary.IsComplete);
            });
            Assert.True(await WaitUntilAsync(
                () => station.Step is BoltFasteningState.Waiting, TimeSpan.FromSeconds(2)));
            Assert.Equal(standbyPosition, motion.Position);
            Assert.Equal(StationCylinderState.Up, station.PickupTablePosition);
        }
        finally
        {
            stop.Cancel();
            await run;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StoppingFasteningDistinguishesApproachFromStartedBolt(bool duringFastening, bool duringFeed)
    {
        var settings = new BoltFasteningSettings
        {
            ShootingPickupSpinMilliseconds = 0,
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = duringFastening ? 20_000 : 20 },
            SafeZ = 5,
            ShootingHead = new() { FasteningZ = 12 },
        };
        var units = new UnitSettings { PickupBoltFeeder = false, ShootingBoltFeeder = false };
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()), new());
        io.Initialize();
        using var motion = new VirtualMotionService(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var bus = new AdcControllerStub { SuppressCompletion = duringFeed };
        var head = CreateAdcHead(bus, io, FasteningHead.Shooting, new(), 1, "Virtual", 115200);
        var work = ConveyorStation.CreateBoltFastening(io);
        var bolt = Bolt(1, FasteningHead.Shooting, 20, 30);
        bolt.FeedDistance = duringFeed ? 2 : 0;
        bolt.FeedSeconds = 2;
        var station = new BoltFasteningStation(head, head, io, motion, new(motion), settings, work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = new() { BoltPoints = [bolt] } } },
            units, new BoltFeederUnit(io, new(), units));
        SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stoppedDuringApproach = false;
        motion.PositionChanged += (x, y, z) =>
        {
            if (duringFeed && io.GetOutput(OutputIo.ShootingBoltStart) && z > settings.ShootingHead.FasteningZ + 0.05)
                stop.Cancel();
            if (!duringFastening && x == bolt.FasteningX && y == bolt.FasteningY && z > settings.SafeZ + 0.05)
            {
                stoppedDuringApproach = true;
                stop.Cancel();
            }
        };
        io.OutputChanged += (output, on) =>
        {
            if (duringFastening && !duringFeed && output == OutputIo.ShootingBoltStart && on)
                stop.Cancel();
        };

        await station.RunAsync(stop.Token);

        Assert.Equal(!duringFastening, stoppedDuringApproach);
        if (!duringFastening)
            Assert.InRange(motion.Position.Z, settings.SafeZ + 0.05, settings.ShootingHead.FasteningZ - 0.05);
        Assert.Equal(duringFastening ? 1 : 0, bus.StartWrites);
        Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
        Assert.Equal(!duringFastening, station.IsHorizontalMoveAllowed);
        Assert.False(motion.IsMoving);
        Assert.False(work.Completed);
        if (duringFastening)
        {
            var assembly = work.GetAssembly(bolt.HeatSink);
            var result = Assert.Single(assembly.ShootingBoltResults).Value;
            Assert.Equal(BoltResultSource.Interrupted, result.Source);
            Assert.Equal(motion.Position.Z - settings.ShootingHead.FasteningZ, result.ZFeedDistance!.Value, 3);
            if (duringFeed)
                Assert.InRange(result.ZFeedDistance.Value, 0.05, bolt.FeedDistance - 0.05);
            Assert.False(result.IsComplete);
            Assert.Null(result.Torque);
            Assert.Equal(AssemblyResult.Pending, assembly.FasteningResult);
            Assert.False(station.IsFasteningRecorded);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => station.RunAsync(selectedBolts: [bolt.Id]));
            Assert.Contains("Remove interrupted bolts", error.Message);
            Assert.Equal(1, bus.StartWrites);
        }
        else
            Assert.All(work.Assemblies, assembly => Assert.Empty(assembly.ShootingBoltResults));
    }

    [Theory]
    [InlineData(0, 1, false, false)]
    [InlineData(0, 0, false, false)]
    [InlineData(2, 3, false, false)]
    [InlineData(2, 0, false, false)]
    [InlineData(3, 0, true, false)]
    [InlineData(3, 0, false, true)]
    public async Task PickupChecksVacuumAfterReturningToSafeZ(
        int retryCount, int successfulAttempt, bool cancelDuringRetry, bool cancelDuringVacuumDelay)
    {
        var settings = new BoltFasteningSettings
        {
            ShootingPickupSpinMilliseconds = 0,
            ShootingPreliminaryZ = 12,
            PickupRetryCount = retryCount,
            PickupVacuumDelayMilliseconds = cancelDuringVacuumDelay ? 5_000 : 40,
            SafeZ = 5,
            PickupPosition = new() { X = 10, Y = 10, Z = 10 },
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
        settings.PickupHead.FasteningZ = 16;
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()),
            new() { TimeoutMilliseconds = 10_000 });
        io.Initialize();
        using var motion = new VirtualMotionService(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var bus = new AdcControllerStub();
        var pickup = CreateAdcHead(bus, io, FasteningHead.Shooting, new(), 1, "Virtual", 115200);
        var units = new UnitSettings();
        var work = ConveyorStation.CreateBoltFastening(io);
        var station = new BoltFasteningStation(pickup, pickup, io, motion, new MotionStatus(motion), settings,
            work,
            new RecipeManager(OpenMachineStore(), new())
            {
                Current = { Pcb = new() { BoltPoints = [Bolt(1, FasteningHead.Pickup, 20, 30)] } },
            }, units,
            new BoltFeederUnit(io, new(), units));
        SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        io.SetInput(InputIo.PickupFeederBoltDetected, true);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var vacuumRequested = false;
        var vacuumStarted = 0L;
        var vacuumRequests = 0;
        var failedPickups = 0;
        var pickupAttempts = 0;
        var confirmedAfterLift = false;
        var previousZ = motion.Position.Z;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.ShootingHeadVacuumPump)
            {
                if (on)
                {
                    Assert.Equal(settings.PickupPosition.Z, motion.Position.Z);
                    Assert.True(io.GetOutput(OutputIo.ShootingHeadDown));
                    Assert.Equal(pickupAttempts, ++vacuumRequests);
                    vacuumStarted = Stopwatch.GetTimestamp();
                    // A signal at the feeder is not proof that the bolt stayed on the lifted head.
                    io.SetInput(InputIo.ShootingHeadVacuumDetected, true);
                    if (cancelDuringVacuumDelay)
                        stop.CancelAfter(20);
                }
                else if (vacuumRequested && !confirmedAfterLift)
                {
                    Assert.Equal(settings.SafeZ, motion.Position.Z);
                    Assert.False(io.GetInput(InputIo.ShootingHeadVacuumDetected));
                    failedPickups++;
                }
                vacuumRequested = on;
            }
            if (output == OutputIo.ShootingBoltStart && on)
                stop.Cancel();
        };
        motion.PositionChanged += (x, y, z) =>
        {
            if (x == settings.PickupPosition.X && y == settings.PickupPosition.Y
                && z == settings.PickupPosition.Z && previousZ != z)
            {
                Assert.False(io.GetOutput(OutputIo.ShootingHeadVacuumPump));
                pickupAttempts++;
                if (cancelDuringRetry && pickupAttempts == 2)
                    stop.Cancel();
            }
            if (vacuumRequested && previousZ == settings.PickupPosition.Z && z < previousZ)
                Assert.True(Stopwatch.GetElapsedTime(vacuumStarted).TotalMilliseconds
                    >= settings.PickupVacuumDelayMilliseconds - 5);
            previousZ = z;
            if (!confirmedAfterLift && vacuumRequested && z < settings.PickupPosition.Z)
            {
                confirmedAfterLift = z == settings.SafeZ && pickupAttempts == successfulAttempt;
                io.SetInput(InputIo.ShootingHeadVacuumDetected, confirmedAfterLift);
            }
        };
        var run = station.RunAsync(stop.Token);
        try
        {
            // Each missed pickup must retry immediately, without the 10-second I/O wait.
            if (successfulAttempt > 0)
            {
                await run.WaitAsync(TimeSpan.FromSeconds(4));
                Assert.Equal(successfulAttempt, pickupAttempts);
                Assert.Equal(successfulAttempt, vacuumRequests);
                Assert.Equal(successfulAttempt - 1, failedPickups);
                Assert.Equal(1, bus.StartWrites);
                Assert.Equal(12, motion.Position.Z);
            }
            else if (cancelDuringRetry)
            {
                await run.WaitAsync(TimeSpan.FromSeconds(4));
                Assert.Equal(2, pickupAttempts);
                Assert.Equal(1, vacuumRequests);
                Assert.Equal(1, failedPickups);
                Assert.Equal(0, bus.StartWrites);
                Assert.True(io.GetOutput(OutputIo.ShootingHeadDown));
            }
            else if (cancelDuringVacuumDelay)
            {
                await run.WaitAsync(TimeSpan.FromSeconds(4));
                Assert.Equal(1, pickupAttempts);
                Assert.Equal(1, vacuumRequests);
                Assert.Equal(0, failedPickups);
                Assert.Equal(settings.PickupPosition.Z, motion.Position.Z);
                Assert.Equal(0, bus.StartWrites);
            }
            else
            {
                var error = await Assert.ThrowsAsync<MaintenanceStopException>(
                    () => run.WaitAsync(TimeSpan.FromSeconds(4)));
                Assert.Contains("vacuum", error.Message);
                Assert.Equal(retryCount + 1, pickupAttempts);
                Assert.Equal(pickupAttempts, vacuumRequests);
                Assert.Equal(pickupAttempts, failedPickups);
                Assert.False(io.GetOutput(OutputIo.ShootingHeadVacuumPump));
                Assert.Equal((10, 10, settings.SafeZ), motion.Position);
                Assert.Equal(0, bus.StartWrites);
            }
        }
        finally
        {
            stop.Cancel();
            await run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(true, true)]
    public async Task MaintenanceRetractionPreservesSupplyAndCleanupFailures(
        bool failCleanup, bool failBothHeads = false)
    {
        var settings = new BoltFasteningSettings
        {
            ShootingPickupSpinMilliseconds = 0,
            ShootingPreliminaryZ = 12,
            SafeZ = 5,
            PickupPosition = new() { X = 10, Y = 10, Z = 10 },
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
        };
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()), new());
        io.Initialize();
        using var motion = new VirtualMotionService(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var bus = new AdcControllerStub();
        var head = CreateAdcHead(bus, io, FasteningHead.Pickup, new(), 1, "Virtual", 115200);
        var units = new UnitSettings { ShootingBoltFeeder = false };
        var feeder = new BoltFeederUnit(io, new() { PickupTimeoutMilliseconds = 0 }, units);
        var supplyFailure = await Assert.ThrowsAsync<IoTimeoutException>(() => feeder.RunAsync());
        var stationIo = new WriteNotifyingIo(io);
        var work = ConveyorStation.CreateBoltFastening(io);
        var station = new BoltFasteningStation(head, head, stationIo, motion, new(motion), settings, work,
            new RecipeManager(OpenMachineStore(), new())
            {
                Current = { Pcb = new() { BoltPoints = [Bolt(1, FasteningHead.Pickup, 20, 30)] } },
            }, units, feeder);
        SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        var maintenanceStarted = false;
        var retractionFailure = new IOException("Maintenance head retraction failed.");
        var shootingRetractionFailure = new IOException("Maintenance shooting head retraction failed.");
        var cleanupFailure = new IOException("Shooting output OFF failed after maintenance retraction.");
        var escapeOff = false;
        station.StepChanged += () => maintenanceStarted |= station.Step is BoltFasteningState.Fastening;
        stationIo.OutputChanged += (output, on) =>
        {
            if (!maintenanceStarted || on)
                return;
            if (output == OutputIo.PickupHeadDown)
                throw retractionFailure;
            if (output == OutputIo.ShootingHeadDown && failBothHeads)
                throw shootingRetractionFailure;
            if (output == OutputIo.ShootBolt && failCleanup)
                throw cleanupFailure;
            if (output == OutputIo.ShootingEscapeForward)
                escapeOff = true;
        };

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var error = await Assert.ThrowsAsync<AggregateException>(() => station.RunAsync(stop.Token));
        var failures = error.Flatten().InnerExceptions;
        var maintenance = Assert.Single(failures.OfType<MaintenanceStopException>());
        Assert.Same(supplyFailure, maintenance.InnerException);
        Assert.Contains(retractionFailure, failures);
        Assert.Equal(2 + (failCleanup ? 1 : 0) + (failBothHeads ? 1 : 0), failures.Count);
        if (failBothHeads)
            Assert.Contains(shootingRetractionFailure, failures);
        if (failCleanup)
            Assert.Contains(cleanupFailure, failures);
        Assert.True(escapeOff);
        Assert.False(station.IsRunning);
        Assert.False(work.Completed);
        Assert.Equal(0, bus.StartWrites);
    }

    [Fact]
    public async Task PickupAlarmDuringApproachCannotBeBypassedByRecoveredDetection()
    {
        var settings = new BoltFasteningSettings
        {
            ShootingPickupSpinMilliseconds = 0,
            ShootingPreliminaryZ = 12,
            SafeZ = 5,
            PickupPosition = new() { X = 10, Y = 10, Z = 10 },
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
        };
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()), new());
        io.Initialize();
        io.SetInput(InputIo.PickupFeederBoltDetected, true);
        using var motion = new VirtualMotionService(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var bus = new AdcControllerStub();
        var head = CreateAdcHead(bus, io, FasteningHead.Pickup, new(), 1, "Virtual", 115200);
        var units = new UnitSettings { ShootingBoltFeeder = false };
        var feeder = new BoltFeederUnit(io, new() { PickupTimeoutMilliseconds = 0 }, units);
        var work = ConveyorStation.CreateBoltFastening(io);
        var bolt = Bolt(1, FasteningHead.Pickup, 20, 30);
        var station = new BoltFasteningStation(head, head, io, motion, new(motion), settings, work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = new() { BoltPoints = [bolt] } } },
            units, feeder);
        SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Task? feederRun = null;
        motion.PositionChanged += (x, y, z) =>
        {
            if (feederRun is not null || x != settings.PickupPosition.X || y != settings.PickupPosition.Y
                || z != settings.PickupPosition.Z)
                return;
            io.SetInput(InputIo.PickupFeederBoltDetected, false);
            feederRun = feeder.RunAsync(stop.Token);
            Assert.True(feederRun.IsFaulted);
            // Supply returns after approach, but its latched alarm still prevents pickup.
            io.SetInput(InputIo.PickupFeederBoltDetected, true);
        };
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.ShootingHeadVacuumPump)
                io.SetInput(InputIo.ShootingHeadVacuumDetected, on);
        };

        var failure = await Assert.ThrowsAsync<MaintenanceStopException>(
            () => station.RunAsync(stop.Token, selectedBolts: [bolt.Id]));

        Assert.NotNull(feederRun);
        Assert.Same(feederRun.Exception!.InnerException, failure.InnerException);
        Assert.Equal((10, 10, settings.SafeZ), motion.Position);
        Assert.True(station.IsHorizontalMoveAllowed);
        Assert.False(work.Completed);
        Assert.Empty(Assert.Single(work.Assemblies).PickupBoltResults);
        Assert.False(io.GetOutput(OutputIo.ShootingHeadVacuumPump));
        Assert.Equal(0, bus.StartWrites);
    }

    [Theory]
    [InlineData(FasteningHead.Pickup)]
    [InlineData(FasteningHead.Shooting)]
    public async Task FeederWaitRechecksSupplyFeedbackBeforeNextOperation(FasteningHead head)
    {
        var settings = new BoltFasteningSettings();
        var io = new VirtualIoService(Outputs(new BoltFasteningHardwareSettings(), new BoltFeederHardwareSettings()), new());
        using var motion = new VirtualMotionService(settings.Motion, new());
        using var bus = new VirtualAdcBus();
        var boltHead = CreateAdcHead(bus, io, head, new(), 1, "Virtual", 115200);
        var station = CreateFastening(boltHead, boltHead, io, motion, settings);
        var input = head == FasteningHead.Pickup
            ? InputIo.PickupFeederBoltDetected : InputIo.ShootingFeederBoltDetected;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var scheduler = new ConcurrentExclusiveSchedulerPair();
        try
        {
            await Task.Factory.StartNew(async () =>
            {
                var waiting = station.WaitForBoltSupplyAsync(head, stop.Token);
                io.SetInput(input, true);
                io.SetInput(input, false);
                await Task.Yield();
                Assert.False(waiting.IsCompleted);

                io.SetInput(input, true);
                await waiting;
                io.SetInput(input, false);
                waiting = station.WaitForBoltSupplyAsync(head, stop.Token);
                Assert.False(waiting.IsCompleted);
                stop.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            }, CancellationToken.None, TaskCreationOptions.None, scheduler.ExclusiveScheduler).Unwrap();
        }
        finally
        {
            stop.Cancel();
            scheduler.Complete();
            await scheduler.Completion;
        }
    }

    [Theory]
    [InlineData(FasteningHead.Pickup)]
    [InlineData(FasteningHead.Shooting)]
    [InlineData(FasteningHead.Pickup, true)]
    [InlineData(FasteningHead.Shooting, false, true)]
    [InlineData(FasteningHead.Pickup, false, false, true)]
    [InlineData(FasteningHead.Shooting, false, false, false, true)]
    [InlineData(FasteningHead.Shooting, false, false, false, false, true)]
    public async Task TeachingBoltMoveUsesCommonSafeZBeforeXyAndFasteningZ(
        FasteningHead head,
        bool stopAtTable = false,
        bool conflictingTableFeedback = false,
        bool loseTableDuringXy = false,
        bool loseTableDuringFasteningZ = false,
        bool loseHeadDuringFasteningZ = false)
    {
        var settings = new BoltFasteningSettings
        {
            ShootingPickupSpinMilliseconds = 0,
            SafeZ = 5,
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = loseTableDuringFasteningZ || loseHeadDuringFasteningZ ? 50 : 20_000 },
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
        settings.PickupHead.FasteningZ = 16;
        settings.ShootingHead.FasteningZ = 12;
        var bolt = Bolt(1, head, 20, 30);
        bolt.FasteningZOffset = 0.75;
        var point = new TeachingPosition(TeachingTarget.BoltPosition, MotionGroup.BoltFastening,
            TeachMode.XYOnly, bolt.IsFasteningPositionDefined) { Bolt = bolt };
        var destination = settings.GetBoltPosition(bolt);
        Assert.Equal(settings.GetHead(head).FasteningZ + 0.75, destination.Z);
        var tableDown = head == FasteningHead.Pickup;
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()),
            new() { TimeoutMilliseconds = conflictingTableFeedback ? 250 : 2_000 })
        { AutoResponseEnabled = false };
        io.SetOutput(OutputIo.PickupTableDown, !tableDown);
        io.SetInputs(
            (InputIo.PickupTableUp, tableDown), (InputIo.PickupTableDown, !tableDown),
            (InputIo.PickupHeadUp, true), (InputIo.PickupHeadDown, false),
            (InputIo.ShootingHeadUp, true), (InputIo.ShootingHeadDown, false));
        using var motion = new VirtualMotionService(settings.Motion, new());
        using var bus = new VirtualAdcBus();
        using var shootingBus = new VirtualAdcBus();
        var station = CreateFastening(
            CreateAdcHead(shootingBus, io, FasteningHead.Shooting, new(), 2, "Virtual", 115200), CreateAdcHead(bus, io, FasteningHead.Pickup, new(), 1, "Virtual", 115200), io, motion, settings);
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        await motion.MoveAxisAsync(MotionAxis.Z, 9, 20_000);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var tableCommand = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var steps = new List<string>();
        motion.MovingChanged += moving =>
        {
            if (!moving)
                return;
            steps.Add(motion.IsMovingHorizontal ? "XY" : "Z");
            if (motion.IsMovingHorizontal)
            {
                Assert.Equal(settings.SafeZ, motion.Position.Z);
                Assert.Equal(tableDown ? StationCylinderState.Down : StationCylinderState.Up, station.PickupTablePosition);
            }
        };
        motion.PositionChanged += (x, y, z) =>
        {
            if (loseTableDuringXy && motion.IsMovingHorizontal)
                io.SetInputs((InputIo.PickupTableUp, false), (InputIo.PickupTableDown, false));
            if (!motion.IsMovingHorizontal
                && x == destination.X && y == destination.Y && z > settings.SafeZ + 0.05)
            {
                if (loseTableDuringFasteningZ)
                    io.SetInputs((InputIo.PickupTableUp, false), (InputIo.PickupTableDown, false));
                if (loseHeadDuringFasteningZ)
                    io.SetInput(InputIo.ShootingHeadUp, false);
            }
        };
        io.OutputChanged += (output, on) =>
        {
            Assert.Equal(OutputIo.PickupTableDown, output);
            Assert.Equal(tableDown, on);
            Assert.Equal((0, 0, settings.SafeZ), motion.Position);
            steps.Add("Table");
            tableCommand.TrySetResult();
        };

        var move = station.MoveToTeachingPositionAsync(point, destination, stop.Token);
        await tableCommand.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(move.IsCompleted);
        Assert.Equal(new[] { "Z", "Table" }, steps);
        Assert.Equal((0, 0, settings.SafeZ), motion.Position);
        if (stopAtTable)
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
            Assert.Equal((0, 0, settings.SafeZ), motion.Position);
        }
        else if (conflictingTableFeedback)
        {
            io.SetInputs((InputIo.PickupTableUp, true), (InputIo.PickupTableDown, true));
            await Assert.ThrowsAsync<IoTimeoutException>(() => move);
            Assert.Equal((0, 0, settings.SafeZ), motion.Position);
        }
        else
        {
            io.SetInputs((InputIo.PickupTableUp, !tableDown), (InputIo.PickupTableDown, tableDown));
            if (loseTableDuringXy || loseTableDuringFasteningZ || loseHeadDuringFasteningZ)
            {
                await Assert.ThrowsAsync<MotionInterlockException>(() => move);
                if (loseTableDuringFasteningZ || loseHeadDuringFasteningZ)
                {
                    Assert.Equal(destination.X, motion.Position.X);
                    Assert.Equal(destination.Y, motion.Position.Y);
                    Assert.InRange(motion.Position.Z, settings.SafeZ, destination.Z - 0.05);
                }
                else
                {
                    Assert.InRange(motion.Position.X, 0, destination.X - 0.05);
                    Assert.InRange(motion.Position.Y, 0, destination.Y - 0.05);
                    Assert.Equal(settings.SafeZ, motion.Position.Z);
                }
            }
            else
            {
                await move;
                Assert.Equal(new[] { "Z", "Table", "XY", "Z" }, steps);
                Assert.Equal((destination.X, destination.Y, destination.Z), motion.Position);
            }
        }
        Assert.Equal((destination.X, destination.Y, destination.Z), (settings.GetBoltPosition(bolt).X, settings.GetBoltPosition(bolt).Y, settings.GetBoltPosition(bolt).Z));
        Assert.Equal(!loseHeadDuringFasteningZ, station.IsHorizontalMoveAllowed);
        Assert.False(motion.IsMoving);
    }

    // PhysicalIoService notifies every successful write, including an unchanged output.
    private sealed class WriteNotifyingIo : IIoService
    {
        private readonly VirtualIoService _inner;

        public Action<InputIo>? BeforeInputRead { get; set; }

        public WriteNotifyingIo(VirtualIoService inner)
        {
            _inner = inner;
        }

        public event Action<InputIo, bool>? InputChanged
        {
            add => _inner.InputChanged += value;
            remove => _inner.InputChanged -= value;
        }

        public event Action<Exception>? Faulted
        {
            add => _inner.Faulted += value;
            remove => _inner.Faulted -= value;
        }

        public event Action<OutputIo, bool>? OutputChanged;
        public bool IsReady => _inner.IsReady;
        public int TimeoutMilliseconds => _inner.TimeoutMilliseconds;

        public void Initialize()
        {
            _inner.Initialize();
        }

        public void CheckReady()
        {
            _inner.CheckReady();
        }

        public void RefreshInputs()
        {
            _inner.RefreshInputs();
        }

        public bool GetInput(InputIo input)
        {
            BeforeInputRead?.Invoke(input);
            return _inner.GetInput(input);
        }

        public bool GetOutput(OutputIo output)
        {
            return _inner.GetOutput(output);
        }

        public OutputFeedback? GetOutputFeedback(OutputIo output)
        {
            return _inner.GetOutputFeedback(output);
        }

        public void SetOutput(OutputIo output, bool value)
        {
            _inner.SetOutput(output, value);
            OutputChanged?.Invoke(output, value);
        }
    }

    private static BoltPoint Bolt(int number, FasteningHead head, double x, double y)
    {
        return new()
        {
            Id = VirtualTestSupport.BoltId(number),
            Head = head,
            X = x,
            Y = y,
            FasteningX = x,
            FasteningY = y,
            PreliminaryFasteningPosition = new() { X = x + 100, Y = y },
        };
    }

    private static BoltHeadSettings HeadSettings()
    {
        return new()
        {
            UpperLeftLocatingPin = new AxisPosition { X = 0, Y = 0 },
            LowerRightLocatingPin = new AxisPosition { X = 100, Y = 100 },
        };
    }

public enum ShootingPreparationFailure
    {
        None,
        Motion,
        MotionAndCleanup,
        Supply,
        Stop,
        StopAndCleanup,
        SupplyBeforeTravel,
        ClearanceLost,
        CarrierLost,
    }

    [Fact]
    public async Task FasteningResumeReviewUsesCurrentNamesAndMatchesResultsByGuid()
    {
        await using var services = MachineTestSupport.CreateDiagnosticServices();
        var machine = services.GetRequiredService<MachineController>();
        await machine.InitializeAsync();
        try
        {
            var recipes = services.GetRequiredService<RecipeManager>();
            var first = new BoltPoint { Name = "Same name" };
            var second = new BoltPoint { Name = "Same name", Head = FasteningHead.Pickup };
            var io = services.GetRequiredService<VirtualIoService>();
            VirtualTestSupport.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
            var station = services.GetRequiredService<BoltFasteningStation>().Station;
            var result = new BoltResult(true, 8);
            station.GetAssembly(HeatSinkSlot.HeatSink1).RecordBolt(second.Head, second.Id, result);
            recipes.Current.Pcb.BoltPoints = [first, new BoltPoint(second.Id) { Name = "Renamed", Head = second.Head }];
            var review = services.GetRequiredService<OperationViewModel>();
            await review.CheckStartCommand.ExecuteAsync(null);
            Assert.Null(review.FasteningResumeBolts[0].Result);
            Assert.Same(result, review.FasteningResumeBolts[1].Result);
            Assert.Equal("Renamed", review.FasteningResumeBolts[1].Label);
            Assert.Equal("OK", review.FasteningResumeBolts[1].Status);
            Assert.False(review.IsFasteningResumeConfirmed);
            services.GetRequiredService<BoltFasteningSettings>().FirstFasteningHead = FasteningHead.Pickup;
            await review.CheckStartCommand.ExecuteAsync(null);
            Assert.Same(result, review.FasteningResumeBolts[0].Result);
            Assert.Equal("Renamed", review.FasteningResumeBolts[0].Label);
            Assert.Null(review.FasteningResumeBolts[1].Result);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }
}
