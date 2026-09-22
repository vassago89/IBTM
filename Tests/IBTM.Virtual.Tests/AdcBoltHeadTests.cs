using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Hantas;
using IBTM.Virtual;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class AdcBoltHeadTests
{
    private static (VirtualIoService Io, AdcBoltHead Head) Create(
        IAdcBus bus, HantasSettings? settings = null, FasteningHead head = FasteningHead.Pickup)
    {
        var io = new VirtualIoService(VirtualTest.Outputs(), new());
        var controller = VirtualTest.CreateAdcHead(bus, io, head, settings ?? new(), 1, "Virtual", 115200);
        return (io, controller);
    }

    [Theory]
    [InlineData(FasteningHead.Pickup)]
    [InlineData(FasteningHead.Shooting)]
    public async Task UsesIoControlsAndReceivesAdcResultWithoutRunOrResultPolling(FasteningHead selected)
    {
        using var bus = new VirtualAdcBus();
        var (io, head) = Create(bus, head: selected);
        var start = selected == FasteningHead.Pickup ? OutputIo.PickupBoltStart : OutputIo.ShootingBoltStart;
        var otherStart = selected == FasteningHead.Pickup ? OutputIo.ShootingBoltStart : OutputIo.PickupBoltStart;
        var reads = 0;
        bus.FrameTransferred += (direction, frame) =>
        {
            if (direction != AdcFrameDirection.Transmit)
                return;
            Assert.Equal((byte)AdcFunctionCode.ReadInputRegisters, frame[1]);
            Assert.Equal((ushort)AdcResultRegister.EventCount, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2)));
            Assert.False(io.GetOutput(start));
            reads++;
        };
        await head.SelectPresetAsync(1);
        var fed = false;
        var result = await head.TightenAsync(feedAsync: async token =>
        {
            Assert.True(io.GetOutput(start));
            Assert.False(io.GetOutput(otherStart));
            fed = true;
            // The result may arrive while the head DOWN callback is still running.
            await Task.Delay(300, token);
        });
        Assert.True(result.Success);
        Assert.True(fed);
        Assert.NotNull(result.Controller);
        Assert.Equal(1, reads);
        Assert.False(io.GetOutput(start));
    }

    [Fact]
    public async Task FastenInputOffAloneDoesNotCompleteWithoutAdcResult()
    {
        var bus = new AdcControllerStub { SuppressAutomaticResults = true };
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        using var stop = new CancellationTokenSource();
        var running = head.TightenAsync(stop.Token);
        io.SetInput(InputIo.PickupBoltFasten, false);
        await Task.Delay(60);
        Assert.False(running.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Theory]
    [InlineData(AdcEventStatus.FasteningOk, 0, true)]
    [InlineData(AdcEventStatus.FasteningNg, 0, false)]
    [InlineData(AdcEventStatus.Error, 42, false)]
    public async Task RetainsEveryControllerResultRegister(AdcEventStatus status, ushort error, bool success)
    {
        ushort[] registers = [1, 1234, 1, 150, 147, 850, 3156, 19, 3175, 57, error, 0, (ushort)status, 123];
        var bus = new AdcControllerStub();
        bus.AutomaticResults.Enqueue(AdcFasteningResult.FromRegisters(registers));
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        var result = await head.TightenAsync();
        Assert.Equal(success, result.Success);
        Assert.Equal(1.47, result.Torque);
        Assert.NotNull(result.RecordedAt);
        var data = Assert.IsType<BoltControllerData>(result.Controller);
        Assert.Equal("Virtual", data.Port);
        Assert.Equal(1, data.SlaveAddress);
        Assert.Equal(1, data.EventCount);
        Assert.Equal(1234, data.FasteningTimeMilliseconds);
        Assert.Equal(1, data.Preset);
        Assert.Equal(1.5, data.TargetTorque);
        Assert.Equal(850, data.TargetSpeedRpm);
        Assert.Equal((3156.0, 19.0, 3175.0), (data.Angle1, data.Angle2, data.Angle3));
        Assert.Equal((ushort)57, data.ScrewCount);
        Assert.Equal(error, data.ErrorCode);
        Assert.Equal((ushort)0, data.DirectionCode);
        Assert.Equal((ushort)status, data.StatusCode);
        Assert.Equal((ushort)123, data.SnugAngle);
        Assert.Equal(registers, data.Registers);
        registers[4] = 999;
        Assert.Equal((ushort)147, data.Registers![4]);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Fact]
    public async Task IgnoresOldAndNonCompletionEvents()
    {
        var bus = new AdcControllerStub();
        var result = AdcFasteningResult.FromRegisters([0, 250, 1, 100, 80, 1000, 0, 0, 0, 1, 0, 0, 1, 0]);
        bus.AutomaticResults.Enqueue(result);
        bus.AutomaticResults.Enqueue(result with { EventCount = ushort.MaxValue });
        bus.AutomaticResults.Enqueue(result with { EventCount = 1, Status = AdcEventStatus.DirectionChanged });
        bus.AutomaticResults.Enqueue(result with { EventCount = 2, Status = AdcEventStatus.PresetChanged });
        bus.AutomaticResults.Enqueue(result with { EventCount = 3, Status = AdcEventStatus.FasteningNg });
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        var completed = await head.TightenAsync();
        Assert.False(completed.Success);
        Assert.Equal(0.8, completed.Torque);
        Assert.Equal(5, bus.ResultReceives);
        Assert.False(io.GetInput(InputIo.PickupBoltFasten));
    }

    [Theory]
    [InlineData(2, AdcDirection.Fastening)]
    [InlineData(1, AdcDirection.Loosening)]
    public async Task MismatchedResultCannotBeRecorded(ushort preset, AdcDirection direction)
    {
        var bus = new AdcControllerStub { ResultPreset = preset, ResultDirection = direction };
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync());
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResultAndDryRunTurnStartOffWithoutWaitingForFastenInput(bool dryRun)
    {
        var bus = new AdcControllerStub { StopPollsRemaining = -1 };
        var (io, head) = Create(bus, new() { ResponseTimeoutMilliseconds = 80 });
        await head.SelectPresetAsync(1);
        Assert.True((await head.TightenAsync(dryRunMilliseconds: dryRun ? 20 : 0)).Success);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.True(io.GetInput(InputIo.PickupBoltFasten));
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.SelectPresetAsync(1));
    }

    [Fact]
    public async Task MissingResultRecordsNgOnlyAfterIoStop()
    {
        var bus = new AdcControllerStub { SuppressAutomaticResults = true, StopPollsRemaining = -1 };
        var (io, head) = Create(bus, new() { FasteningTimeoutMilliseconds = 60 });
        await head.SelectPresetAsync(1);
        var result = await head.TightenAsync();
        Assert.False(result.Success);
        Assert.Null(result.Torque);
        Assert.Null(result.Controller);
        Assert.Contains("timed out", result.Error);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.True(io.GetInput(InputIo.PickupBoltFasten));
    }

    [Fact]
    public async Task TimeoutAndIoOutputFailureAreBothPreserved()
    {
        var bus = new AdcControllerStub { SuppressAutomaticResults = true, StopWriteFailure = new IOException("I/O write failed") };
        var (io, head) = Create(bus, new() { FasteningTimeoutMilliseconds = 50, ResponseTimeoutMilliseconds = 60 });
        await head.SelectPresetAsync(1);
        var error = await Assert.ThrowsAsync<AggregateException>(() => head.TightenAsync());
        Assert.IsType<TimeoutException>(error.InnerExceptions[0]);
        Assert.IsType<IOException>(error.InnerExceptions[1]);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Fact]
    public async Task SerialReceiveFailureStillTurnsStartOff()
    {
        var bus = new AdcControllerStub { ResultReceiveFailure = new IOException("Serial disconnected") };
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        await Assert.ThrowsAsync<IOException>(() => head.TightenAsync());
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.False(io.GetInput(InputIo.PickupBoltFasten));
    }

    [Fact]
    public async Task FailedHeadDownStillStopsAndDoesNotRecordAResult()
    {
        var bus = new AdcControllerStub();
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync(
            feedAsync: token => throw new InvalidOperationException("Head output failed")));
        Assert.Equal(0, bus.ResultReceives);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.True((await head.TightenAsync()).Success);
    }

    [Fact]
    public async Task DryRunNeedsNoAdcResultAndCancellationStopsMotor()
    {
        var bus = new AdcControllerStub { SuppressAutomaticResults = true };
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        var result = await head.TightenAsync(dryRunMilliseconds: 30);
        Assert.Equal(BoltResultSource.DryRun, result.Source);
        Assert.Equal(0, bus.ResultReceives);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        using var stop = new CancellationTokenSource();
        var cycle = head.TightenAsync(stop.Token, dryRunMilliseconds: 2000);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cycle);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Fact]
    public async Task ControllerErrorRecordsNgThenPulsesIoResetBeforeNextBolt()
    {
        var bus = new AdcControllerStub { ResultStatus = AdcEventStatus.Error, ResultError = 125 };
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        var ng = await head.TightenAsync();
        Assert.False(ng.Success);
        Assert.Equal((ushort)125, ng.Controller!.ErrorCode);
        Assert.Equal(0, bus.ResetWrites);
        var edges = new List<(bool On, long Time)>();
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PickupBoltReset)
                edges.Add((on, Environment.TickCount64));
        };
        await head.SelectPresetAsync(1);
        Assert.Equal(2, edges.Count);
        Assert.True(edges[0].On);
        Assert.False(edges[1].On);
        Assert.True(edges[1].Time - edges[0].Time >= 90);
        Assert.Equal(1, bus.ResetWrites);
        Assert.False(io.GetInput(InputIo.PickupBoltAlarm));
        Assert.True(io.GetInput(InputIo.PickupBoltReady));
        bus.ResultStatus = AdcEventStatus.FasteningOk;
        bus.ResultError = 0;
        Assert.True((await head.TightenAsync()).Success);
    }

    [Fact]
    public async Task AlarmWithoutAdcResultStopsImmediatelyAndRecordsNg()
    {
        var bus = new AdcControllerStub { SuppressAutomaticResults = true };
        var (io, head) = Create(bus, new() { ResponseTimeoutMilliseconds = 100 });
        await head.SelectPresetAsync(1);
        var cycle = head.TightenAsync();
        io.SetInput(InputIo.PickupBoltAlarm, true);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        var result = await cycle;
        Assert.False(result.Success);
        Assert.Contains("ALARM", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetWithoutClearedAlarmAndReadyBlocksNextStart(bool alarmClears)
    {
        var bus = new AdcControllerStub { CurrentAlarm = 42, ResetPollsRemaining = alarmClears ? 0 : -1, NotReady = alarmClears };
        var (io, head) = Create(bus, new() { ResponseTimeoutMilliseconds = 60 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.SelectPresetAsync(1));
        Assert.Equal(1, bus.ResetWrites);
        Assert.Equal(0, bus.StartWrites);
        Assert.False(io.GetOutput(OutputIo.PickupBoltReset));
    }

    [Fact]
    public async Task CancellationDuringResetAlwaysTurnsResetOff()
    {
        var bus = new AdcControllerStub();
        var (io, head) = Create(bus);
        using var stop = new CancellationTokenSource();
        var reset = head.ResetAsync(stop.Token);
        Assert.True(io.GetOutput(OutputIo.PickupBoltReset));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reset);
        Assert.False(io.GetOutput(OutputIo.PickupBoltReset));
        Assert.Equal(0, bus.StartWrites);
    }

    [Fact]
    public async Task PresetOutputChangeAndLiveNotReadyBlockStart()
    {
        var bus = new AdcControllerStub();
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        io.SetOutput(OutputIo.PickupBoltPreset2, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync());
        Assert.Equal(0, bus.StartWrites);
        await head.SelectPresetAsync(1);
        io.SetInput(InputIo.PickupBoltReady, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync());
        Assert.Equal(0, bus.StartWrites);
    }

    [Fact]
    public async Task ManualReverseUsesIoAndReleaseStopsIt()
    {
        using var bus = new VirtualAdcBus();
        var (io, head) = Create(bus);
        using var release = new CancellationTokenSource();
        var cycle = head.RunReverseAsync(release.Token);
        Assert.True(io.GetOutput(OutputIo.PickupBoltDirection));
        Assert.True(io.GetOutput(OutputIo.PickupBoltStart));
        release.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cycle);
        Assert.False(io.GetInput(InputIo.PickupBoltFasten));
        await head.SelectPresetAsync(1);
        Assert.True((await head.TightenAsync()).Success);
        Assert.False(io.GetOutput(OutputIo.PickupBoltDirection));
    }

    [Fact]
    public async Task SeparatePortsWithSameSlaveNeverMixHeads()
    {
        var io = new VirtualIoService(VirtualTest.Outputs(), new());
        using var pickupBus = new VirtualAdcBus(io, FasteningHead.Pickup, 1);
        using var shootingBus = new VirtualAdcBus(io, FasteningHead.Shooting, 1);
        var pickup = new AdcBoltHead(pickupBus, io, FasteningHead.Pickup, new(), 1, "Pickup", 115200);
        var shooting = new AdcBoltHead(shootingBus, io, FasteningHead.Shooting, new(), 1, "Shooting", 115200);
        pickupBus.SetNextFasteningResult(1, AdcEventStatus.FasteningNg);
        await pickup.SelectPresetAsync(1);
        await shooting.SelectPresetAsync(1);
        var results = await Task.WhenAll(pickup.TightenAsync(), shooting.TightenAsync());
        Assert.False(results[0].Success);
        Assert.Equal("Pickup", results[0].Controller!.Port);
        Assert.True(results[1].Success);
        Assert.Equal("Shooting", results[1].Controller!.Port);
    }
}
