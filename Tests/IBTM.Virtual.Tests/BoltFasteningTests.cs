using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.BoltFeeder;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Hantas;
using IBTM.Virtual;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTest;
using IBTM.Storage;

namespace IBTM.Virtual.Tests;

public sealed class BoltFasteningTests
{
    [Theory]
    [InlineData(BoltDriver.Io)]
    [InlineData(BoltDriver.HantasAdc)]
    public async Task ResultTimeoutRecordsNgRaisesHeadAndContinuesToNextBolt(BoltDriver driver)
    {
        var settings = new BoltFasteningSettings
        {
            SafeZ = 5,
            ShootingArrivalDelaySeconds = 0,
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
        settings.ShootingHead.FasteningZ = 12;
        var controllerSettings = new IoBoltHardwareSettings { FasteningTimeoutMilliseconds = 100 };
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings(), controllerSettings), new());
        io.Initialize();
        using var motion = new VirtualMotionService(settings.Motion, new(), horizontalZ: () => settings.SafeZ);
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var ioHead = new IoBoltHead(io, FasteningHead.Shooting, controllerSettings);
        using var pickup = new IoBoltHead(io, FasteningHead.Pickup, controllerSettings);
        var bus = new AdcControllerStub { SuppressAutomaticResults = true };
        IBoltHead head = driver == BoltDriver.Io ? ioHead
            : CreateAdcHead(bus, io, FasteningHead.Shooting, new HantasSettings { FasteningTimeoutMilliseconds = 100 }, 1, "Virtual", 115200);
        var units = new UnitSettings();
        var work = new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), units);
        var layout = new PcbLayout
        {
            BoltPoints = [Bolt(1, FasteningHead.Shooting, 20, 30), Bolt(2, FasteningHead.Shooting, 30, 40)],
        };
        var station = new BoltFasteningStation(head, pickup, io, motion, settings,
            new CarrierReferenceSettings { UpperLeftLocatingPin = new(), LowerRightLocatingPin = new() { X = 100, Y = 100 } },
            work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = layout } }, units);
        SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.ShootingFeederBoltDetected, true);
        await work.Station.SeatAsync(CancellationToken.None);
        var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        var raisedAfterTimeout = false;
        var starts = 0;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.ShootBolt && on)
            {
                io.SetInput(InputIo.ShootingTubeBoltDetected, true);
                io.SetInput(InputIo.ShootingTubeBoltDetected, false);
            }
            if (output == OutputIo.ShootingBoltStart && on)
                starts++;
            if (output == OutputIo.ShootingHeadDown && on && starts == 2)
            {
                io.SetInput(InputIo.ShootingBoltFasten, true);
                io.SetInput(InputIo.ShootingBoltFasten, false);
            }
            if (output == OutputIo.ShootingHeadDown && !on && assembly.PcbBoltResults.ContainsKey(1))
            {
                Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
                Assert.False(bus.Running);
                raisedAfterTimeout = true;
                bus.SuppressAutomaticResults = false;
            }
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = station.RunAsync(stop.Token);
        try
        {
            Assert.True(await WaitUntilAsync(() => work.Completed || run.IsCompleted, TimeSpan.FromSeconds(4)));
            Assert.True(work.Completed, run.Exception?.ToString());
            Assert.True(raisedAfterTimeout);
            Assert.Equal(2, driver == BoltDriver.Io ? starts : bus.StartWrites);
            var failed = assembly.PcbBoltResults[1];
            Assert.False(failed.Success);
            Assert.Null(failed.Torque);
            Assert.Null(failed.Controller);
            Assert.Contains("timed out", failed.Error);
            Assert.NotNull(failed.RecordedAt);
            Assert.True(assembly.PcbBoltResults[2].Success);
            Assert.Equal(AssemblyResult.Ng, assembly.FasteningResult);
            Assert.Equal(BoltCylinderState.Up, station.ShootingHeadPosition);
        }
        finally
        {
            stop.Cancel();
            await run;
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
        using var motion = new VirtualMotionService(settings.Motion, new(), horizontalZ: () => settings.SafeZ);
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        var bus = new AdcControllerStub();
        if (rejectedResponse)
        {
            // Exact exception reply in the equipment log, including CRC.
            using var response = new MemoryStream([0x01, 0x84, 0x03, 0x03, 0x01]);
            bus.ResultReceiveFailure = await Assert.ThrowsAsync<AdcResponseException>(
                () => AdcBus.ReadResponseAsync(response, () => { }, bytes => { },
                    1, AdcFunctionCode.ReadInputRegisters, CancellationToken.None));
        }
        else
        {
            bus.ResultStatus = AdcEventStatus.Error;
            bus.ResultError = 42;
        }
        var head = CreateAdcHead(bus, io, FasteningHead.Shooting, new(), 1, "Virtual", 115200);
        var pickup = CreateAdcHead(new VirtualAdcBus(), io, FasteningHead.Pickup, new(), 2, "Virtual", 115200);
        var units = new UnitSettings { ShootingBoltFeeder = !dryRun };
        var work = new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), units);
        var layout = new PcbLayout
        {
            BoltPoints = [Bolt(1, FasteningHead.Shooting, 20, 30), Bolt(2, FasteningHead.Shooting, 30, 40)],
        };
        var station = new BoltFasteningStation(head, pickup, io, motion, settings,
            new CarrierReferenceSettings { UpperLeftLocatingPin = new(), LowerRightLocatingPin = new() { X = 100, Y = 100 } },
            work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = layout } }, units);
        SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.ShootingFeederBoltDetected, true);
        await work.Station.SeatAsync(CancellationToken.None);
        var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        var raisedAfterError = false;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.ShootBolt && on)
            {
                io.SetInput(InputIo.ShootingTubeBoltDetected, true);
                io.SetInput(InputIo.ShootingTubeBoltDetected, false);
            }
            if (output == OutputIo.ShootingHeadDown && !on && assembly.PcbBoltResults.ContainsKey(1))
            {
                Assert.False(bus.Running);
                raisedAfterError = true;
            }
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = station.RunAsync(stop.Token);
        try
        {
            Assert.True(await WaitUntilAsync(() => work.Completed || run.IsCompleted, TimeSpan.FromSeconds(4)));
            Assert.True(work.Completed, run.Exception?.ToString());
            Assert.True(raisedAfterError);
            Assert.Equal(2, bus.StartWrites);
            Assert.Equal(rejectedResponse || dryRun ? 0 : 1, bus.ResetWrites);
            Assert.Equal(2, bus.StopWrites);
            Assert.Equal(2, assembly.PcbBoltResults.Count);
            if (dryRun)
            {
                Assert.Equal(0, bus.ResultReceives);
                Assert.All(assembly.PcbBoltResults.Values, result =>
                {
                    Assert.Equal(BoltResultSource.DryRun, result.Source);
                    Assert.Null(result.Torque);
                    Assert.Null(result.Error);
                });
                Assert.Equal(BoltCylinderState.Up, station.ShootingHeadPosition);
                return;
            }
            Assert.False(assembly.PcbBoltResults[1].Success);
            Assert.Contains(rejectedResponse ? "0x03" : "42", assembly.PcbBoltResults[1].Error);
            if (rejectedResponse)
            {
                Assert.Null(assembly.PcbBoltResults[1].Torque);
                Assert.True(assembly.PcbBoltResults[2].Success);
            }
            else
                Assert.False(assembly.PcbBoltResults[2].Success); // A second START must occur, not reuse the old Error event.
            Assert.Equal(AssemblyResult.Ng, assembly.FasteningResult);
            Assert.Equal(BoltCylinderState.Up, station.ShootingHeadPosition);
        }
        finally
        {
            stop.Cancel();
            await run;
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
        var bus = new VirtualAdcBus();
        var gantry = VirtualTest.CreateFastening(
            CreateAdcHead(bus, io, FasteningHead.Shooting, new(), 2, "Virtual", 115200),
            CreateAdcHead(bus, io, FasteningHead.Pickup, new(), 1, "Virtual", 115200),
            io, motion, settings, new());
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
        var bus = new VirtualAdcBus();
        var station = VirtualTest.CreateFastening(
            CreateAdcHead(bus, io, FasteningHead.Shooting, new(), 2, "Virtual", 115200),
            CreateAdcHead(bus, io, FasteningHead.Pickup, new(), 1, "Virtual", 115200), io, motion, settings, new());
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
            ShootingArrivalDelaySeconds = 0.2,
            ShootingDetectionTimeoutMilliseconds = 500,
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.ShootingArrivalDelaySeconds = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.ShootingArrivalDelaySeconds = double.NaN);
        var io = new VirtualIoService(new BoltFasteningHardwareSettings().Outputs, new())
        { AutoResponseEnabled = false };
        using var motion = new VirtualMotionService(settings.Motion, new());
        var bus = new VirtualAdcBus();
        var gantry = VirtualTest.CreateFastening(
            CreateAdcHead(bus, io, FasteningHead.Shooting, new(), 2, "Virtual", 115200), CreateAdcHead(bus, io, FasteningHead.Pickup, new(), 1, "Virtual", 115200),
            io, motion, settings, new());
        io.Initialize();
        io.SetInput(InputIo.ShootingFeederBoltDetected, true);
        io.SetInput(InputIo.ShootingEscapeBackward, true);
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.ShootingEscapeForward)
                io.SetInputs((InputIo.ShootingEscapeForward, on), (InputIo.ShootingEscapeBackward, !on));
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
            Assert.True(await WaitUntilAsync(() => io.GetInput(InputIo.ShootingEscapeBackward),
                TimeSpan.FromMilliseconds(100)));
            Assert.False(shot.IsCompleted); // Escape returns on passage, before the head-arrival delay ends.
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
        var physicalEvents = new FeederWriteNotifyingIo(io);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var feederWrites = 0;
        physicalEvents.OutputChanged += (output, value) =>
        {
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
        var bus = new VirtualAdcBus();
        var station = CreateFastening(
            CreateAdcHead(bus, io, FasteningHead.Shooting, new(), 2, "Virtual", 115200), CreateAdcHead(bus, io, FasteningHead.Pickup, new(), 1, "Virtual", 115200),
            io, motion, settings, new());
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
        // Starting the independent feeder must also return an escape left forward.
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.ShootingEscapeForward, true);
        feeder.Stop();
        Assert.True(io.GetOutput(OutputIo.ShootingFeederOff));
        using var cancellation = new CancellationTokenSource();
        var run = feeder.RunAsync(cancellation.Token);
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
            await run;
        }
        Assert.True(io.GetOutput(OutputIo.ShootingFeederOff));
    }

    [Theory]
    [InlineData(FasteningHead.Shooting, false, true)]
    [InlineData(FasteningHead.Pickup, false, false)]
    [InlineData(FasteningHead.Pickup, true, false)]
    [InlineData(FasteningHead.Shooting, true, false)]
    [InlineData(FasteningHead.Pickup, false, true)]
    [InlineData(FasteningHead.Shooting, false, false, true)]
    [InlineData(FasteningHead.Shooting, false, false, false, true)]
    [InlineData(FasteningHead.Shooting, false, false, false, false, ShootingPreparationFailure.Motion)]
    [InlineData(FasteningHead.Shooting, false, false, false, false, ShootingPreparationFailure.Supply)]
    [InlineData(FasteningHead.Shooting, false, false, false, false, ShootingPreparationFailure.Stop)]
    public async Task IoFasteningStartsBeforeDescentAndStopsAfterCompletionOrFeedFailure(
        FasteningHead selectedHead,
        bool stopDuringDescent,
        bool missingDownFeedback,
        bool loseTableUp = false,
        bool shootWithoutVacuum = false,
        ShootingPreparationFailure preparationFailure = ShootingPreparationFailure.None)
    {
        var settings = new BoltFasteningSettings
        {
            SafeZ = 5,
            ShootingArrivalDelaySeconds = preparationFailure == ShootingPreparationFailure.Motion
                ? 1 : shootWithoutVacuum ? 0.3 : 0.05,
            ShootingDetectionTimeoutMilliseconds = preparationFailure == ShootingPreparationFailure.Supply ? 50 : 2_000,
            Motion = new() { HorizontalSpeed = 50, ZSpeed = 20_000 },
            PickupPosition = new() { X = 100, Y = 100, Z = 10 },
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
        settings.ShootingHead.FasteningZ = 12;
        settings.PickupHead.FasteningZ = 16;
        var controllerSettings = new IoBoltHardwareSettings();
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(), new BoltFeederHardwareSettings(), new ConveyorHardwareSettings(), controllerSettings),
            new() { TimeoutMilliseconds = missingDownFeedback ? 100 : 2_000 })
        { AutoResponseEnabled = false };
        using var motion = new VirtualMotionService(settings.Motion, new(), horizontalZ: () => settings.SafeZ);
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var pickup = new IoBoltHead(io, FasteningHead.Pickup, controllerSettings);
        using var shooting = new IoBoltHead(io, FasteningHead.Shooting, controllerSettings);

        var work = new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), new());
        var bolt = selectedHead == FasteningHead.Shooting
            ? Bolt(1, selectedHead, 20, 30)
            : Bolt(1, selectedHead, 0, 0);
        var layout = new PcbLayout { BoltPoints = [bolt] };
        var station = new BoltFasteningStation(shooting,
            pickup,
            io,
            motion,
            settings,
            new CarrierReferenceSettings { UpperLeftLocatingPin = new(), LowerRightLocatingPin = new() { X = 100, Y = 100 } },
            work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = layout } },
            new());
        var gantry = station;
        await gantry.MoveZAsync(selectedHead == FasteningHead.Shooting
            ? settings.SafeZ : settings.PickupHead.FasteningZ);
        io.SetOutput(OutputIo.PickupHeadVacuumPump, true);
        io.SetOutput(OutputIo.ShootingHeadVacuumPump, true);
        io.SetInputs(
            (InputIo.BoltFasteningHeatSink1Present, true),
            (InputIo.BoltFasteningBackupPlateUp, true),
            (InputIo.BoltFasteningStopperDown, true),
            (InputIo.PickupTableUp, selectedHead != FasteningHead.Pickup),
            (InputIo.PickupTableDown, selectedHead == FasteningHead.Pickup),
            (InputIo.PickupHeadVacuumDetected, true),
            (InputIo.ShootingHeadVacuumDetected, !shootWithoutVacuum),
            (InputIo.ShootingFeederBoltDetected, selectedHead == FasteningHead.Shooting),
            (InputIo.ShootingEscapeBackward, true));
        var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        var results = selectedHead == FasteningHead.Shooting ? assembly.PcbBoltResults : assembly.PickupBoltResults;
        var selected = selectedHead == FasteningHead.Pickup ? pickup : shooting;
        var (start, fasten, cylinder, up, down) = selectedHead == FasteningHead.Pickup
            ? (OutputIo.PickupBoltStart, InputIo.PickupBoltFasten, OutputIo.PickupHeadDown,
                InputIo.PickupHeadUp, InputIo.PickupHeadDown)
            : (OutputIo.ShootingBoltStart, InputIo.ShootingBoltFasten, OutputIo.ShootingHeadDown,
                InputIo.ShootingHeadUp, InputIo.ShootingHeadDown);
        var commands = new List<string>();
        var supplyCommands = new List<string>();
        var shotElapsed = new Stopwatch();
        var shootingOverlappedMove = false;
        var escapeReturnedDuringMove = false;
        motion.PositionChanged += (x, y, z) =>
        {
            if (motion.IsMovingHorizontal && io.GetOutput(OutputIo.ShootBolt))
            {
                shootingOverlappedMove = true;
                if (preparationFailure == ShootingPreparationFailure.Motion && x > 0)
                    motion.Stop();
            }
        };
        var descending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        io.OutputChanged += (output, on) =>
        {
            Assert.NotEqual(OutputIo.ShootingFeederOff, output); // Only the feeder loop owns this output.
            if (output == OutputIo.ShootingEscapeForward)
            {
                if (on)
                {
                    Assert.True(io.GetInput(InputIo.ShootingFeederBoltDetected));
                }
                else
                {
                    Assert.False(io.GetInput(InputIo.ShootingTubeBoltDetected));
                    escapeReturnedDuringMove = motion.IsMovingHorizontal;
                }
                supplyCommands.Add(on ? "ESCAPE FORWARD" : "ESCAPE BACKWARD");
                io.SetInputs((InputIo.ShootingEscapeForward, on), (InputIo.ShootingEscapeBackward, !on));
            }
            else if (output == OutputIo.ShootBolt)
            {
                supplyCommands.Add(on ? "SHOOT ON" : "SHOOT OFF");
                if (on)
                {
                    Assert.True(io.GetInput(InputIo.ShootingEscapeForward));
                    Assert.True(gantry.IsHorizontalMoveAllowed);
                    Assert.Equal(BoltCylinderState.Up, gantry.PickupTablePosition);
                    Assert.NotEqual((bolt.X!.Value, bolt.Y!.Value, settings.ShootingHead.FasteningZ), motion.GetPosition());
                    shotElapsed.Restart();
                    if (preparationFailure == ShootingPreparationFailure.Stop)
                        stop.Cancel();
                    else if (preparationFailure != ShootingPreparationFailure.Supply)
                    {
                        io.SetInput(InputIo.ShootingTubeBoltDetected, true);
                        io.SetInput(InputIo.ShootingTubeBoltDetected, false);
                    }
                }
            }
            else if (!on && output == OutputIo.PickupHeadVacuumPump)
                io.SetInput(InputIo.PickupHeadVacuumDetected, false);
            else if (output == start)
            {
                commands.Add(on ? "START ON" : "START OFF");
                if (on)
                {
                    Assert.True(gantry.IsHorizontalMoveAllowed);
                    Assert.Equal(settings.GetHead(selectedHead).FasteningZ, motion.GetPosition().Z);
                    if (selectedHead == FasteningHead.Shooting)
                    {
                        Assert.True(shotElapsed.IsRunning);
                        Assert.True(shotElapsed.Elapsed >= TimeSpan.FromSeconds(settings.ShootingArrivalDelaySeconds - 0.005));
                        Assert.Equal((bolt.X!.Value, bolt.Y!.Value, settings.ShootingHead.FasteningZ), motion.GetPosition());
                        Assert.False(motion.IsMoving);
                        Assert.Equal(!shootWithoutVacuum, io.GetInput(InputIo.ShootingHeadVacuumDetected));
                        Assert.False(io.GetOutput(OutputIo.ShootBolt));
                        Assert.Equal(new[] { "ESCAPE FORWARD", "SHOOT ON", "ESCAPE BACKWARD", "SHOOT OFF" }, supplyCommands);
                    }
                }
                io.SetInput(fasten, on); // STOP-induced OFF must not become a successful result.
            }
            else if (output == cylinder)
            {
                if (on)
                {
                    Assert.True(io.GetOutput(start));
                    commands.Add("DOWN");
                    io.SetInputs((up, false), (down, false));
                    descending.TrySetResult();
                }
                else
                {
                    io.SetInputs((up, true), (down, false));
                    if (results.ContainsKey(1))
                        stop.Cancel();
                }
            }
        };
        var run = station.RunAsync(stop.Token);
        try
        {
            if (preparationFailure != ShootingPreparationFailure.None)
            {
                if (preparationFailure == ShootingPreparationFailure.Motion)
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(1)));
                else if (preparationFailure == ShootingPreparationFailure.Supply)
                    await Assert.ThrowsAsync<IoTimeoutException>(() => run.WaitAsync(TimeSpan.FromSeconds(1)));
                else
                    await run.WaitAsync(TimeSpan.FromSeconds(1));
                Assert.True(shotElapsed.IsRunning);
                if (preparationFailure != ShootingPreparationFailure.Stop)
                    Assert.True(shootingOverlappedMove);
                Assert.NotEqual((bolt.X!.Value, bolt.Y!.Value, settings.ShootingHead.FasteningZ), motion.GetPosition());
                Assert.False(io.GetOutput(OutputIo.ShootBolt));
                Assert.False(motion.IsMoving);
                Assert.Empty(commands);
                Assert.Empty(results);
                return;
            }
            await descending.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (selectedHead == FasteningHead.Shooting)
                Assert.True(escapeReturnedDuringMove);
            Assert.Equal(new[] { "START ON", "DOWN" }, commands);
            Assert.True(io.GetOutput(start));
            Assert.False(run.IsCompleted);
            Assert.Empty(results);
            if (loseTableUp)
                io.SetInput(InputIo.PickupTableUp, false);
            else if (stopDuringDescent)
                stop.Cancel();
            else if (missingDownFeedback)
                io.SetInput(fasten, false); // Screw contact can prevent the DOWN input from turning ON.
            else
            {
                io.SetInput(down, true);
                io.SetInput(fasten, false);
            }

            if (loseTableUp)
                await Assert.ThrowsAsync<MotionInterlockException>(() => run);
            else
                await run.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(new[] { "START ON", "DOWN", "START OFF" }, commands);
            Assert.False(io.GetOutput(start));
            if (stopDuringDescent || loseTableUp)
            {
                Assert.Empty(results);
                Assert.True(io.GetOutput(cylinder));
            }
            else
            {
                Assert.True(results[1].Success);
                Assert.Equal(BoltResultSource.IoAssumedOk, results[1].Source);
                Assert.Null(results[1].Torque);
                if (selectedHead == FasteningHead.Shooting)
                {
                    Assert.True(shootingOverlappedMove);
                    Assert.False(io.GetOutput(OutputIo.ShootingHeadVacuumPump));
                    Assert.Equal(!shootWithoutVacuum, io.GetInput(InputIo.ShootingHeadVacuumDetected));
                }
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartAtFirstPickupBoltUsesCurrentVacuumWithoutAnotherPickup(bool useIo)
    {
        var settings = new BoltFasteningSettings
        {
            SafeZ = 5,
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
        using var motion = Motion(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var pickupIo = new IoBoltHead(io, FasteningHead.Pickup, controllerSettings);
        using var shooting = new IoBoltHead(io, FasteningHead.Shooting, controllerSettings);
        var bus = new AdcControllerStub();
        IBoltHead pickup = useIo ? pickupIo : CreateAdcHead(bus, io, FasteningHead.Pickup, new HantasSettings(), 1, "Virtual", 115200);

        var work = new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), new());
        var layout = new PcbLayout { BoltPoints = [Bolt(1, FasteningHead.Pickup, 10, 10)] };
        var units = new UnitSettings();
        var station = new BoltFasteningStation(shooting,
            pickup,
            io,
            motion,
            settings,
            new CarrierReferenceSettings
            {
                UpperLeftLocatingPin = new(),
                LowerRightLocatingPin = new() { X = 100, Y = 100 },
            },
            work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = layout } },
            units);
        io.SetInputs(
            (InputIo.BoltFasteningHeatSink1Present, true),
            (InputIo.BoltFasteningBackupPlateUp, true),
            (InputIo.BoltFasteningStopperDown, true),
            (InputIo.PickupHeadUp, true),
            (InputIo.PickupHeadDown, false),
            (InputIo.PickupHeadVacuumDetected, true));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var interruptDescent = true;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PickupHeadDown)
            {
                if (on && interruptDescent)
                {
                    io.SetInput(InputIo.PickupHeadUp, false);
                    stop.Cancel();
                    return;
                }
                io.SetInputs((InputIo.PickupHeadUp, !on), (InputIo.PickupHeadDown, on));
            }
        };
        io.SetInputs((InputIo.PickupTableUp, false), (InputIo.PickupTableDown, true));
        var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        Assert.Equal(BoltFasteningState.FasteningPickup, station.GetState());
        await station.RunAsync(stop.Token);
        interruptDescent = false;
        Assert.Empty(assembly.PickupBoltResults);

        // This recipe has one bolt. Current vacuum confirms a bolt is still on the head.
        units.PickupBoltFeeder = false;
        Assert.Empty(assembly.PickupBoltResults);
        Assert.True(io.GetOutput(OutputIo.PickupHeadDown));
        if (!useIo)
            Assert.Equal(1, bus.StartWrites);
        var job = work.CurrentJob;
        var movedBeforeRestart = false;
        var restarted = false;
        motion.PositionChanged += (x, y, z) =>
        {
            if (!restarted)
                movedBeforeRestart = true;
        };
        using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PickupBoltStart && on)
            {
                restarted = true;
                io.SetInput(InputIo.PickupBoltFasten, true);
                io.SetInput(InputIo.PickupBoltFasten, false);
            }
            if (output == OutputIo.PickupHeadDown && !on && assembly.PickupBoltResults.ContainsKey(1))
                finish.Cancel();
        };
        await station.RunAsync(finish.Token);
        Assert.False(movedBeforeRestart);
        Assert.Same(job, work.CurrentJob);
        Assert.Same(assembly, Assert.Single(work.Assemblies));
        Assert.True(work.Station.CarrierPresent);
        Assert.Equal(
            BoltResultSource.DryRun,
            assembly.PickupBoltResults[1].Source);
        Assert.True(assembly.PickupBoltResults[1].Success);
        if (useIo)
            Assert.Null(assembly.PickupBoltResults[1].Torque);
        else
            Assert.Equal(2, bus.StartWrites);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartBeginsAtBoltOneAndChecksVacuumAtThePickupTurn(bool pickupAlreadyLoaded)
    {
        var settings = new BoltFasteningSettings
        {
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
        using var motion = new VirtualMotionService(settings.Motion, new(), horizontalZ: () => settings.SafeZ);
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var firstStop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        BoltFasteningStation? station = null;
        var starts = new List<int>();
        var bus = new AdcControllerStub
        {
            Started = () =>
            {
                starts.Add(station!.GetActiveBolt()!.Number);
                if (starts.Count == 2)
                    firstStop.Cancel();
            },
        };
        var head = CreateAdcHead(bus, io, FasteningHead.Shooting, new(), 1, "Virtual", 115200);
        var units = new UnitSettings { PickupBoltFeeder = false, ShootingBoltFeeder = false };
        var work = new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), units);
        var layout = new PcbLayout
        {
            BoltPoints = [Bolt(1, FasteningHead.Shooting, 20, 30), Bolt(2, FasteningHead.Shooting, 40, 30),
                Bolt(3, FasteningHead.Pickup, 60, 30)],
        };
        station = new(head, head, io, motion, settings,
            new CarrierReferenceSettings { UpperLeftLocatingPin = new(), LowerRightLocatingPin = new() { X = 100, Y = 100 } },
            work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = layout } }, units);
        SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        await work.Station.SeatAsync(CancellationToken.None);
        await station.RunAsync(firstStop.Token);
        Assert.Equal(new[] { 1, 2 }, starts);
        var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        Assert.Single(assembly.PcbBoltResults);
        var firstResult = assembly.PcbBoltResults[1];

        // The next run must ignore the recorded first bolt when choosing where to start.
        io.SetOutput(OutputIo.PickupHeadVacuumPump, pickupAlreadyLoaded);
        io.SetInput(InputIo.PickupHeadVacuumDetected, pickupAlreadyLoaded);
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PickupHeadVacuumPump && !on)
                io.SetInput(InputIo.PickupHeadVacuumDetected, false);
        };
        var visitedPickup = false;
        motion.PositionChanged += (x, y, z) =>
        {
            if (x == settings.PickupPosition.X && y == settings.PickupPosition.Y)
            {
                visitedPickup = true;
                Assert.Equal(3, station.GetActiveBolt()!.Number);
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
            $"State={station.GetState()}, starts={string.Join(',', starts)}, pickup={station.PickupHeadPosition}, "
            + $"vacuum={station.PickupBoltLoaded}, XY={motion.GetPosition()}, visitedPickup={visitedPickup}");
        Assert.Equal(new[] { 1, 2, 1, 2, 3 }, starts);
        Assert.NotSame(firstResult, assembly.PcbBoltResults[1]);
        Assert.Equal(!pickupAlreadyLoaded, visitedPickup);
        Assert.Single(assembly.PickupBoltResults);
        Assert.Equal(BoltCylinderState.Up, station.PickupHeadPosition);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartDoesNotCollectThePreviousBoltResult(bool replaceCarrier)
    {
        var settings = new BoltFasteningSettings
        {
            SafeZ = 0,
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()),
            new MachineOptions())
        { AutoResponseEnabled = false };
        using var motion = Motion(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        var bus = new VirtualAdcBus();
        var pickupHead = CreateAdcHead(bus, io, FasteningHead.Pickup, new HantasSettings(), 1, "Virtual", 115200);

        var work = new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), new());
        var layout = new PcbLayout
        {
            BoltPoints = [Bolt(1, FasteningHead.Pickup, 0, 0)],
        };
        var station = new BoltFasteningStation(CreateAdcHead(bus, io, FasteningHead.Shooting, new HantasSettings(), 2, "Virtual", 115200),
            pickupHead,
            io,
            motion,
            settings,
            new CarrierReferenceSettings
            {
                UpperLeftLocatingPin = new(),
                LowerRightLocatingPin = new() { X = 100, Y = 100 },
            },
            work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = layout } },
            new());
        var gantry = station;
        VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningBackupPlateUp, true);
        io.SetInput(InputIo.BoltFasteningStopperDown, true);
        io.SetInput(InputIo.BoltFasteningStopperUp, false);
        io.SetInput(InputIo.ShootingHeadUp, true);
        io.SetInput(InputIo.PickupHeadUp, true);
        io.SetInput(InputIo.PickupHeadDown, false);
        io.SetInput(InputIo.PickupHeadVacuumDetected, true);
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PickupHeadDown)
                io.SetInputs((InputIo.PickupHeadUp, !on), (InputIo.PickupHeadDown, on));
        };
        io.SetInputs((InputIo.PickupTableUp, false), (InputIo.PickupTableDown, true));
        var originalAssembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        Assert.Equal(BoltFasteningState.FasteningPickup, station.GetState());

        var responseError = new IOException("Completed fastening response lost.");
        var loseResult = true;
        var starts = 0;
        using var resumedStop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bus.FrameTransferred += (direction, frame) =>
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
            if (output == OutputIo.PickupBoltStart && on)
                starts++;
        };
        bus.SetNextFasteningResult(1, AdcEventStatus.FasteningNg);
        using var firstStop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Same(responseError, await Assert.ThrowsAsync<IOException>(
            () => station.RunAsync(firstStop.Token)));
        Assert.Empty(originalAssembly.PickupBoltResults);
        Assert.False((await ((IAdcBus)bus).ReadControllerStatusAsync(1)).Running);

        if (replaceCarrier)
        {
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        }
        else
        {
            io.SetInput(InputIo.PickupHeadDown, false);
            io.SetInput(InputIo.PickupHeadUp, true);
            Assert.Equal(BoltFasteningState.FasteningPickup, station.GetState());
            Assert.Equal(1, station.GetActiveBolt()!.Number);
        }

        var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        assembly.ResultsChanged += updated =>
        {
            if (updated.PickupBoltResults.ContainsKey(1))
                resumedStop.Cancel();
        };
        await station.RunAsync(resumedStop.Token);
        Assert.True(assembly.PickupBoltResults[1].Success);
        Assert.Equal(2, starts);
        if (replaceCarrier)
        {
            Assert.NotSame(originalAssembly, assembly);
            Assert.Empty(originalAssembly.PickupBoltResults);
        }
        else
        {
            Assert.Single(assembly.PickupBoltResults);
        }
    }

    [Fact]
    public async Task ShootingStandbyAndPickupTableFollowSingleFasteningCycle()
    {
        var settings = new BoltFasteningSettings
        {
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
        using var motion = new VirtualMotionService(settings.Motion, operationCancellation: new(), horizontalZ: () => settings.SafeZ);
        var bus = new VirtualAdcBus();

        var layout = new PcbLayout
        {
            BoltPoints = [
                Bolt(1, FasteningHead.Shooting, 110, 220),
                Bolt(2, FasteningHead.Pickup, 115, 225),
                new() { Number = 1, HeatSink = HeatSinkSlot.HeatSink2, Head = FasteningHead.Shooting, X = 120, Y = 230 },
                new() { Number = 2, HeatSink = HeatSinkSlot.HeatSink2, Head = FasteningHead.Pickup, X = 125, Y = 235 },
            ],
        };
        var work = new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), new());
        var station = new BoltFasteningStation(CreateAdcHead(bus, io, FasteningHead.Shooting, new HantasSettings(), 2, "Virtual", 115200),
            CreateAdcHead(bus, io, FasteningHead.Pickup, new HantasSettings(), 1, "Virtual", 115200),
            io,
            motion,
            settings,
            new() { UpperLeftLocatingPin = new() { X = 100, Y = 200 }, LowerRightLocatingPin = new() { X = 140, Y = 200 } },
            work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = layout } },
            new() { PickupBoltFeeder = false, ShootingBoltFeeder = false });
        var gantry = station;
        io.Initialize();
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        await station.SetPickupTableDownAsync(true, CancellationToken.None);
        await ((IAdcBus)bus).SelectPresetAsync(2, 4);
        await ((IAdcBus)bus).SelectPresetAsync(1, 5);
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
            if (output is not (OutputIo.PickupBoltStart or OutputIo.ShootingBoltStart))
                return;
            var head = (byte)(output == OutputIo.PickupBoltStart ? 1 : 2);
            var position = motion.GetPosition();
            starts.Add((head, position.X, position.Y, position.Z));
            Assert.Equal(head == 2 ? BoltCylinderState.Up : BoltCylinderState.Down, gantry.PickupTablePosition);
        };
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PickupTableDown && on)
            {
                tableDescents++;
                Assert.True(gantry.IsAtSafeZ());
                Assert.True(gantry.IsHorizontalMoveAllowed);
                Assert.Equal(2, starts.Count);
                Assert.All(work.Assemblies, assembly => Assert.Single(assembly.PcbBoltResults));
            }
            if (output == OutputIo.PickupHeadVacuumPump && on)
            {
                pickups++;
                Assert.Equal(BoltCylinderState.Down, gantry.PickupTablePosition);
                Assert.True(gantry.IsAtPickupPosition());
                Assert.True(gantry.IsHorizontalMoveAllowed);
            }
            if (on && output is OutputIo.PickupHeadDown or OutputIo.ShootingHeadDown)
                Assert.False(gantry.IsAtPickupXY());
        };
        motion.PositionChanged += (_, _, z) =>
        {
            if (motion.IsMovingHorizontal)
            {
                Assert.Equal(settings.SafeZ, z);
                Assert.True(gantry.IsHorizontalMoveAllowed);
                Assert.Equal(tableDescents > 0 && !work.Completed ? BoltCylinderState.Down : BoltCylinderState.Up,
                    gantry.PickupTablePosition);
            }
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = station.RunAsync(stop.Token);
        try
        {
            Assert.True(await WaitUntilAsync(
                () => station.GetState() == BoltFasteningState.Waiting, TimeSpan.FromSeconds(2)));
            Assert.Equal((280d, 410d, 5d), (motion.GetPosition().X, motion.GetPosition().Y, motion.GetPosition().Z));
            Assert.Empty(starts);
            Assert.Equal(BoltCylinderState.Up, gantry.PickupTablePosition);
            io.SetInputs(
                (InputIo.BoltFasteningHeatSink1Present, true),
                (InputIo.BoltFasteningHeatSink2Present, true));
            Assert.True(await WaitUntilAsync(
                () => station.GetState() == BoltFasteningState.Waiting, TimeSpan.FromSeconds(1)));
            Assert.Empty(starts); // No descent while the carrier is still on the belt.
            await work.Station.SeatAsync(CancellationToken.None);
            Assert.True(await WaitUntilAsync(() => work.Completed || run.IsCompleted, TimeSpan.FromSeconds(8)));
            Assert.True(work.Completed, run.Exception?.ToString() ?? station.GetState().ToString());
            Assert.Equal(new (byte, double, double, double)[] {
                (2, 280, 410, 12), (2, 270, 420, 12), (1, -25, 235, 16), (1, -15, 225, 16),
            }, starts);
            Assert.Equal(new (byte, ushort)[] { (2, 1), (2, 1), (1, 1), (1, 1) }, presets);
            Assert.Equal(2, pickups);
            Assert.Equal(1, tableDescents);
            Assert.All(work.Assemblies, assembly => Assert.Single(assembly.PickupBoltResults));
            Assert.True(await WaitUntilAsync(
                () => station.GetState() == BoltFasteningState.Waiting, TimeSpan.FromSeconds(2)));
            Assert.Equal((280d, 410d, 5d), (motion.GetPosition().X, motion.GetPosition().Y, motion.GetPosition().Z));
            Assert.Equal(BoltCylinderState.Up, gantry.PickupTablePosition);
        }
        finally
        {
            stop.Cancel();
            await run;
        }
    }

    [Trait("Category", "MachineFlow")]
    [Fact]
    public async Task FasteningPreservesHeadOrderAndCarrierResults()
    {
        var settings = new BoltFasteningSettings
        {
            ShootingArrivalDelaySeconds = 0.05,
            Motion = new MotionSettings { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
            SafeZ = 5,
            PickupPosition = new AxisPosition { X = 10, Y = 10, Z = 10 },
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
        settings.ShootingHead.FasteningZ = 12;
        settings.PickupHead.FasteningZ = 16;
        var io = new VirtualIoService(
            Outputs(
                new BoltFasteningHardwareSettings(),
                new BoltFeederHardwareSettings(),
                new ConveyorHardwareSettings()),
            new MachineOptions());
        _ = new VirtualMachine(io, []);
        var bus = new VirtualAdcBus();
        await ((IAdcBus)bus).SelectPresetAsync(2, 4);
        await ((IAdcBus)bus).SelectPresetAsync(1, 5);
        var presets = new Dictionary<byte, ushort>();
        var tightenings = new List<(byte Head, ushort Preset)>();
        Action? afterStop = null;
        io.OutputChanged += (output, on) =>
        {
            if (on && output is OutputIo.PickupBoltPreset1 or OutputIo.ShootingBoltPreset1)
                presets[(byte)(output == OutputIo.PickupBoltPreset1 ? 1 : 2)] = 1;
            if (output is not (OutputIo.PickupBoltStart or OutputIo.ShootingBoltStart))
                return;
            var head = (byte)(output == OutputIo.PickupBoltStart ? 1 : 2);
            if (on)
                tightenings.Add((head, presets[head]));
            else
                afterStop?.Invoke();
        };
        var connection = new HantasSettings { PickupPortName = "Virtual" };
        var pickupHead = CreateAdcHead(bus, io, FasteningHead.Pickup, connection, 1, "Virtual", 115200);
        var shootingHead = CreateAdcHead(bus, io, FasteningHead.Shooting, connection, 2, "Virtual", 115200);
        using var motion = new VirtualMotionService(
            settings.Motion,
            horizontalZ: () => settings.SafeZ,
            operationCancellation: new());

        var work = new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), new());
        var feeder = new BoltFeederUnit(io, new(), new());
        var layout = new PcbLayout

        {

            BoltPoints = [
                Bolt(1, FasteningHead.Pickup, 20, 30),
                Bolt(2, FasteningHead.Shooting, 20, 30),
                new() { Number = 1, HeatSink = HeatSinkSlot.HeatSink2, Head = FasteningHead.Pickup, X = 30, Y = 40 },
                new() { Number = 2, HeatSink = HeatSinkSlot.HeatSink2, Head = FasteningHead.Shooting, X = 30, Y = 40 },
            ],
        };
        var station = new BoltFasteningStation(shootingHead,
            pickupHead,
            io,
            motion,
            settings,
            new CarrierReferenceSettings
            {
                UpperLeftLocatingPin = new AxisPosition { X = 0, Y = 0 },
                LowerRightLocatingPin = new AxisPosition { X = 100, Y = 100 },
            },
            work,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = layout } },
            new());
        var gantry = station;
        var movedWithLoweredCylinder = false;
        var movedBelowTravelZ = false;
        var fasteningHeights = new List<(byte Head, double Z)>();
        var runningHeads = new HashSet<byte>();
        var feedingHeads = new List<byte>();
        motion.PositionChanged += (_, _, _) =>
            movedWithLoweredCylinder |= motion.IsMovingHorizontal
                && !gantry.IsHorizontalMoveAllowed;
        io.OutputChanged += (output, on) =>
        {
            if (output is not (OutputIo.PickupBoltStart or OutputIo.ShootingBoltStart))
                return;
            var head = (byte)(output == OutputIo.PickupBoltStart ? 1 : 2);
            if (on)
            {
                Assert.True(gantry.IsHorizontalMoveAllowed);
                runningHeads.Add(head);
                fasteningHeights.Add((head, motion.GetPosition().Z));
            }
            else
                runningHeads.Remove(head);
        };
        io.OutputChanged += (output, on) =>
        {
            switch (true)
            {
                case true when !on || output is not (OutputIo.ShootingHeadDown or OutputIo.PickupHeadDown):
                    return;
            }
            var address = (byte)(output == OutputIo.PickupHeadDown ? 1 : 2);
            Assert.Contains(address, runningHeads);
            feedingHeads.Add(address);
        };

        io.Initialize();
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        await gantry.MoveToSafeZAsync();
        motion.PositionChanged += (_, _, z) =>
            movedBelowTravelZ |= motion.IsMovingHorizontal
                && Math.Abs(z - settings.SafeZ) > MotionService.PositionToleranceMillimeters;
        await gantry.CheckReadyAsync();
        bus.SetNextFasteningResult(2, AdcEventStatus.FasteningNg);
        io.SetInput(InputIo.ShootingFeederBoltDetected, true);
        VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        await work.Station.SeatAsync(CancellationToken.None);
        Assert.True(work.Station.CarrierSeated);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningHeatSink2Present, true);

        using var feederCancellation = new CancellationTokenSource();
        var feederRun = feeder.RunAsync(feederCancellation.Token);
        try
        {
            using (var stopDuringApproach = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                settings.Motion.ZSpeed = 20;
                void StopDuringFasteningApproach(double x, double y, double z)
                {
                    if (x == 20 && y == 30 && z > settings.SafeZ + 0.05)
                        stopDuringApproach.Cancel();
                }
                motion.PositionChanged += StopDuringFasteningApproach;
                try
                {
                    await station.RunAsync(stopDuringApproach.Token);
                }
                finally
                {
                    motion.PositionChanged -= StopDuringFasteningApproach;
                    settings.Motion.ZSpeed = 20_000;
                }
                Assert.InRange(
                    motion.GetPosition().Z,
                    settings.SafeZ + 0.05,
                    settings.ShootingHead.FasteningZ - 0.05);
                Assert.Empty(tightenings);
                Assert.True(gantry.IsHorizontalMoveAllowed);
                Assert.False(work.Completed);
            }

            using var cancellation = new CancellationTokenSource();
            var resumedRun = station.RunAsync(cancellation.Token);
            try
            {
                Assert.True(
                    await WaitUntilAsync(() => work.Completed, TimeSpan.FromSeconds(10)),
                    $"State={station.GetState()}, Error={resumedRun.Exception?.GetBaseException().Message}");
            }
            finally
            {
                cancellation.Cancel();
                await resumedRun;
            }

            var heatSink1 = work.GetAssembly(HeatSinkSlot.HeatSink1);
            var heatSink2 = work.GetAssembly(HeatSinkSlot.HeatSink2);
            Assert.Equal(AssemblyResult.Ng, heatSink1.FasteningResult);
            Assert.Equal(AssemblyResult.Ok, heatSink2.FasteningResult);
            Assert.False(heatSink1.PcbBoltResults[2].Success);
            Assert.True(heatSink1.PickupBoltResults[1].Success);
            Assert.True(heatSink2.PcbBoltResults[2].Success);
            Assert.True(heatSink2.PickupBoltResults[1].Success);
            Assert.False(movedWithLoweredCylinder);
            Assert.False(movedBelowTravelZ);
            Assert.Equal(4, fasteningHeights.Count);
            Assert.Equal(new byte[] { 2, 2, 1, 1 }, feedingHeads);
            Assert.All(fasteningHeights, item => Assert.Equal(
                item.Head == 1 ? settings.PickupHead.FasteningZ : settings.ShootingHead.FasteningZ,
                item.Z));
            Assert.True(gantry.IsHorizontalMoveAllowed);
            Assert.True(motion.IsAtHorizontalZ);
            Assert.Equal(
                new (byte Head, ushort Preset)[] { (2, 1), (2, 1), (1, 1), (1, 1) },
                tightenings);
            // A replaced carrier must never inherit the previous carrier's in-flight result.
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
            io.SetInput(InputIo.BoltFasteningHeatSink2Present, false);
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
            var previousAssembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
            var carrierReplaced = false;
            using var carrierChange = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            afterStop = () =>
            {
                VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
                VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
                carrierReplaced = true;
                carrierChange.Cancel();
            };
            await station.RunAsync(carrierChange.Token);
            Assert.True(carrierReplaced);
            Assert.Empty(previousAssembly.PcbBoltResults);
            Assert.Empty(work.Assemblies);
            Assert.False(work.Completed);
        }
        finally
        {
            feederCancellation.Cancel();
            await feederRun;
        }
    }

    [Theory]
    [InlineData(FasteningHead.Pickup)]
    [InlineData(FasteningHead.Shooting)]
    public async Task FeederWaitRechecksSupplyFeedbackBeforeNextOperation(FasteningHead head)
    {
        var settings = new BoltFasteningSettings
        {
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
            SafeZ = 5,
            PickupPosition = new() { X = 10, Y = 10, Z = 10 },
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
        settings.ShootingHead.FasteningZ = 12;
        settings.PickupHead.FasteningZ = 16;
        var io = new VirtualIoService(
            Outputs(
                new BoltFasteningHardwareSettings(),
                new BoltFeederHardwareSettings(),
                new ConveyorHardwareSettings()),
            new MachineOptions())
        { AutoResponseEnabled = false };
        using var motion = new VirtualMotionService(
            settings.Motion,
            horizontalZ: () => settings.SafeZ,
            operationCancellation: new OperationCancellation());
        var bus = new VirtualAdcBus();

        var layout = new PcbLayout
        {
            BoltPoints = [Bolt(1, head, 10, 10)],
        };
        var station = new BoltFasteningStation(CreateAdcHead(bus, io, FasteningHead.Shooting, new HantasSettings(), 2, "Virtual", 115200),
            CreateAdcHead(bus, io, FasteningHead.Pickup, new HantasSettings(), 1, "Virtual", 115200),
            io,
            motion,
            settings,
            new CarrierReferenceSettings { UpperLeftLocatingPin = new(), LowerRightLocatingPin = new() { X = 100, Y = 100 }, },
            new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), new()),
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = layout } },
            new());
        var gantry = station;
        io.SetInput(InputIo.PickupHeadUp, true);
        io.SetInput(InputIo.ShootingHeadUp, true);
        io.SetInput(InputIo.ShootingEscapeBackward, true);
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        await gantry.MoveToXYAsync(10, 10);
        if (head == FasteningHead.Pickup)
        {
            io.SetInputs((InputIo.PickupTableUp, false), (InputIo.PickupTableDown, true));
        }
        else
        {
            await gantry.MoveZAsync(settings.ShootingHead.FasteningZ);
        }

        VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningBackupPlateUp, true);
        io.SetInput(InputIo.BoltFasteningStopperDown, true);
        io.SetInput(InputIo.BoltFasteningStopperUp, false);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        Assert.Equal(
            head == FasteningHead.Pickup
                ? BoltFasteningState.FasteningPickup
                : BoltFasteningState.FasteningPcb,
            station.GetState());

        using (var cancelled = new CancellationTokenSource())
        {
            var waiting = station.RunAsync(cancelled.Token);
            Assert.False(waiting.IsCompleted);
            if (head == FasteningHead.Pickup)
            {
                Assert.Equal(BoltCylinderState.Up, station.PickupHeadPosition);
                Assert.Equal(settings.SafeZ, motion.GetPosition().Z);
                Assert.False(io.GetOutput(OutputIo.PickupHeadDown));
                Assert.False(io.GetOutput(OutputIo.PickupHeadVacuumPump));
            }
            cancelled.Cancel();
            await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        }

        if (head == FasteningHead.Pickup)
            await gantry.MoveToXYAsync(0, 0);
        var stationChanges = 0;
        var gantryChanges = 0;
        station.Changed += () => Interlocked.Increment(ref stationChanges);
        gantry.Changed += () => Interlocked.Increment(ref gantryChanges);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var continued = false;
        io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.ShootingEscapeForward)
            {
                if (value)
                    Assert.True(io.GetInput(InputIo.ShootingFeederBoltDetected));
                io.SetInputs((InputIo.ShootingEscapeForward, value), (InputIo.ShootingEscapeBackward, !value));
            }
            if (head == FasteningHead.Pickup
                ? output == OutputIo.PickupHeadVacuumPump && value
                : output == OutputIo.ShootBolt && value)
            {
                Assert.True(station.IsHorizontalMoveAllowed);
                Assert.True(io.GetInput(head == FasteningHead.Pickup
                    ? InputIo.PickupFeederBoltDetected : InputIo.ShootingFeederBoltDetected));
                continued = true;
                stop.Cancel();
            }
        };
        var run = station.RunAsync(stop.Token);
        try
        {
            if (head == FasteningHead.Pickup)
            {
                Assert.True(await WaitUntilAsync(
                    () => station.IsAtPickupXY() && station.IsAtSafeZ(),
                    TimeSpan.FromSeconds(1)));
                Assert.Equal(BoltCylinderState.Up, station.PickupHeadPosition);
                Assert.Equal(settings.SafeZ, motion.GetPosition().Z);
                Assert.False(io.GetOutput(OutputIo.PickupHeadDown));
                Assert.False(io.GetOutput(OutputIo.PickupHeadVacuumPump));
                io.SetInput(InputIo.PickupFeederBoltDetected, true);
            }
            else
            {
                io.SetInput(InputIo.ShootingHeadVacuumDetected, true);
                io.SetInput(InputIo.ShootingEscapeBackward, false);
                io.SetInput(InputIo.ShootingEscapeForward, true);
                io.SetInput(InputIo.ShootingTubeBoltDetected, true);
                await Task.Delay(50);
                Assert.False(continued);
                Assert.False(run.IsCompleted);
                Assert.False(io.GetOutput(OutputIo.ShootBolt));
                io.SetInput(InputIo.ShootingTubeBoltDetected, false);
                io.SetInput(InputIo.ShootingEscapeForward, false);
                io.SetInput(InputIo.ShootingEscapeBackward, true);
                io.SetInput(InputIo.ShootingFeederBoltDetected, true);
            }

            Assert.True(await WaitUntilAsync(() => continued, TimeSpan.FromSeconds(1)));
        }
        finally
        {
            stop.Cancel();
            await run;
        }

        Assert.Equal(head == FasteningHead.Pickup, io.GetInput(InputIo.PickupFeederBoltDetected));
        Assert.Equal(head == FasteningHead.Shooting, io.GetInput(InputIo.ShootingFeederBoltDetected));
        Assert.False(io.GetOutput(OutputIo.ShootBolt));
        Assert.True(stationChanges > 0);
        Assert.True(gantryChanges > 0); // Display listeners still receive head feedback.
        Assert.Equal(
            head == FasteningHead.Pickup ? settings.PickupPosition.Z : settings.ShootingHead.FasteningZ,
            motion.GetPosition().Z);
    }

    [Theory]
    [InlineData(FasteningHead.Pickup)]
    [InlineData(FasteningHead.Shooting)]
    [InlineData(FasteningHead.Pickup, true)]
    [InlineData(FasteningHead.Shooting, false, true)]
    [InlineData(FasteningHead.Pickup, false, false, true)]
    [InlineData(FasteningHead.Shooting, false, false, false, true)]
    public async Task TeachingBoltMoveWaitsForTableAtSafeZBeforeXyAndFasteningZ(
        FasteningHead head,
        bool stopAtTable = false,
        bool conflictingTableFeedback = false,
        bool loseTableDuringXy = false,
        bool loseTableDuringFasteningZ = false)
    {
        var settings = new BoltFasteningSettings
        {
            SafeZ = 5,
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = loseTableDuringFasteningZ ? 50 : 20_000 },
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
        settings.PickupHead.FasteningZ = 16;
        settings.ShootingHead.FasteningZ = 12;
        var reference = new CarrierReferenceSettings
        {
            UpperLeftLocatingPin = new(),
            LowerRightLocatingPin = new() { X = 100, Y = 100 },
        };
        var bolt = Bolt(1, head, 20, 30);
        var layout = new PcbLayout { BoltPoints = [bolt] };
        var point = settings.GetTeachingPositions(layout, bolt.HeatSink, reference)
            .Single(point => point.Bolt == bolt);
        var destination = point.Read();
        Assert.Equal(TeachMode.Full, point.Mode);
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
        using var motion = new VirtualMotionService(settings.Motion, new(), horizontalZ: () => settings.SafeZ);
        var bus = new VirtualAdcBus();
        var station = CreateFastening(
            CreateAdcHead(bus, io, FasteningHead.Shooting, new(), 2, "Virtual", 115200), CreateAdcHead(bus, io, FasteningHead.Pickup, new(), 1, "Virtual", 115200), io, motion, settings, reference);
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
                Assert.Equal(settings.SafeZ, motion.GetPosition().Z);
                Assert.Equal(tableDown ? BoltCylinderState.Down : BoltCylinderState.Up, station.PickupTablePosition);
            }
        };
        motion.PositionChanged += (x, y, z) =>
        {
            if (loseTableDuringXy && motion.IsMovingHorizontal)
                io.SetInputs((InputIo.PickupTableUp, false), (InputIo.PickupTableDown, false));
            if (loseTableDuringFasteningZ && !motion.IsMovingHorizontal
                && x == destination.X && y == destination.Y && z > settings.SafeZ + 0.05)
                io.SetInputs((InputIo.PickupTableUp, false), (InputIo.PickupTableDown, false));
        };
        io.OutputChanged += (output, on) =>
        {
            Assert.Equal(OutputIo.PickupTableDown, output);
            Assert.Equal(tableDown, on);
            Assert.Equal((0, 0, settings.SafeZ), motion.GetPosition());
            steps.Add("Table");
            tableCommand.TrySetResult();
        };

        var move = station.MoveToTeachingPositionAsync(point, destination, stop.Token);
        await tableCommand.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(move.IsCompleted);
        Assert.Equal(new[] { "Z", "Table" }, steps);
        Assert.Equal((0, 0, settings.SafeZ), motion.GetPosition());
        if (stopAtTable)
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
            Assert.Equal((0, 0, settings.SafeZ), motion.GetPosition());
        }
        else if (conflictingTableFeedback)
        {
            io.SetInputs((InputIo.PickupTableUp, true), (InputIo.PickupTableDown, true));
            await Assert.ThrowsAsync<IoTimeoutException>(() => move);
            Assert.Equal((0, 0, settings.SafeZ), motion.GetPosition());
        }
        else
        {
            io.SetInputs((InputIo.PickupTableUp, !tableDown), (InputIo.PickupTableDown, tableDown));
            if (loseTableDuringXy || loseTableDuringFasteningZ)
            {
                await Assert.ThrowsAsync<MotionInterlockException>(() => move);
                if (loseTableDuringFasteningZ)
                {
                    Assert.Equal(destination.X, motion.GetPosition().X);
                    Assert.Equal(destination.Y, motion.GetPosition().Y);
                    Assert.InRange(motion.GetPosition().Z, settings.SafeZ, destination.Z - 0.05);
                }
                else
                {
                    Assert.InRange(motion.GetPosition().X, 0, destination.X - 0.05);
                    Assert.InRange(motion.GetPosition().Y, 0, destination.Y - 0.05);
                    Assert.Equal(settings.SafeZ, motion.GetPosition().Z);
                }
            }
            else
            {
                await move;
                Assert.Equal(new[] { "Z", "Table", "XY", "Z" }, steps);
                Assert.Equal((destination.X, destination.Y, destination.Z), motion.GetPosition());
            }
        }
        Assert.Equal((destination.X, destination.Y, destination.Z), (point.Read().X, point.Read().Y, point.Read().Z));
        Assert.True(station.IsHorizontalMoveAllowed);
        Assert.False(motion.IsMoving);
    }

    // PhysicalIoService notifies every successful write, including an unchanged output.
    private sealed class FeederWriteNotifyingIo : IIoService
    {
        private readonly VirtualIoService _inner;

        public FeederWriteNotifyingIo(VirtualIoService inner)
        {
            _inner = inner;
        }

        public event Action<InputIo, bool>? InputChanged
        {
            add { _inner.InputChanged += value; }
            remove { _inner.InputChanged -= value; }
        }

        public event Action<Exception>? Faulted
        {
            add { _inner.Faulted += value; }
            remove { _inner.Faulted -= value; }
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
            Number = number,
            Head = head,
            X = x,
            Y = y,
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
        Supply,
        Stop,
    }

}
