using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
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
    public async Task UsesIoControlsAndReadsResultOnceAfterRunTurnsOff(FasteningHead selected)
    {
        using var bus = new VirtualAdcBus();
        var (io, head) = Create(bus, head: selected);
        var start = selected == FasteningHead.Pickup ? OutputIo.PickupBoltStart : OutputIo.ShootingBoltStart;
        var otherStart = selected == FasteningHead.Pickup ? OutputIo.ShootingBoltStart : OutputIo.PickupBoltStart;
        var resultReads = 0;
        var eventReads = 0;
        var statusReads = 0;
        bus.FrameTransferred += (direction, frame) =>
        {
            if (direction != AdcFrameDirection.Transmit)
                return;
            Assert.Equal((byte)AdcFunctionCode.ReadInputRegisters, frame[1]);
            var address = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2));
            if (address == (ushort)AdcResultRegister.EventCount)
            {
                var count = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4));
                if (count == 1)
                {
                    eventReads++;
                    Assert.False(io.GetOutput(start));
                }
                else
                {
                    Assert.Equal(AdcFasteningResult.RegisterCount, count);
                    resultReads++;
                    Assert.True(io.GetOutput(start));
                }
            }
            else
            {
                Assert.Equal((ushort)AdcStatusRegister.Preset, address);
                statusReads++;
            }
        };
        await head.SelectPresetAsync(1);
        var fed = false;
        var result = await head.TightenAsync(feedAsync: async token =>
        {
            Assert.True(io.GetOutput(start));
            Assert.False(io.GetOutput(otherStart));
            fed = true;
            await Task.Delay(10, token);
        });
        Assert.True(result.Success);
        Assert.True(fed);
        Assert.NotNull(result.Controller);
        Assert.Equal(1, eventReads); // Pre-START baseline only.
        Assert.Equal(1, resultReads);
        Assert.True(statusReads >= 4); // Preset, RUN ON, RUN OFF and STOP.
        Assert.False(io.GetOutput(start));
    }

    [Fact]
    public async Task SharedMonitorRunsWhileIdleAndDisconnectInvalidatesItsSample()
    {
        using var bus = new AdcControllerStub { StatusReadDelayMilliseconds = 15 };
        var (io, head) = Create(bus, new() { StatusPollMilliseconds = 20 });
        await head.CheckReadyAsync();
        Assert.Null(head.Monitor.Error);
        await Task.WhenAll(bus.Monitor.StartAsync(1, CancellationToken.None),
            bus.Monitor.StartAsync(1, CancellationToken.None));
        var reads = bus.StatusReads;
        Assert.True(await VirtualTest.WaitUntilAsync(() => bus.StatusReads >= reads + 3, TimeSpan.FromSeconds(2)));
        Assert.False(bus.ConcurrentStatusReadsDetected);
        Assert.Equal(0, bus.ResultReads);
        Assert.Equal(0, bus.StartWrites);
        Assert.True(head.Monitor.Status!.Ready);
        bus.StatusReadFailure = new IOException("Status disconnected");
        Assert.True(await VirtualTest.WaitUntilAsync(() => head.Monitor.Error is not null, TimeSpan.FromSeconds(2)));
        Assert.Null(head.Monitor.Status);
        bus.StatusReadFailure = null;
        Assert.True(await VirtualTest.WaitUntilAsync(() => head.Monitor.Status is not null, TimeSpan.FromSeconds(2)));
        using var disconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var pending = head.Monitor.WaitForSampleAsync(Stopwatch.GetTimestamp(), disconnectTimeout.Token);
        bus.Close();
        var disconnected = await Assert.ThrowsAsync<IOException>(() => pending);
        Assert.Contains("Controller test bus/1", disconnected.Message);
        Assert.Null(head.Monitor.Status);
        reads = bus.StatusReads;
        await Task.Delay(80);
        Assert.Equal(reads, bus.StatusReads);
        await head.CheckReadyAsync();
        Assert.True(head.Monitor.Status!.Ready);
        Assert.Null(head.Monitor.Error);
        Assert.False(bus.ConcurrentStatusReadsDetected);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Fact]
    public async Task MonitorSerializesQueuedReadsAndStatusUntilEachResponseCompletes()
    {
        var statusResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var bus = new AdcControllerStub { StatusReadBarrier = statusResponse.Task };
        bus.Open("Virtual", 115200);
        bus.Monitor.IntervalMilliseconds = 10;
        await bus.Monitor.StartAsync(1, CancellationToken.None);
        Assert.True(await VirtualTest.WaitUntilAsync(() => bus.StatusReads == 1, TimeSpan.FromSeconds(2)));
        var first = bus.Monitor.EnqueueAsync(async token =>
        {
            firstEntered.TrySetResult();
            await firstResponse.Task.WaitAsync(token);
            return await bus.ReadRegistersAsync(1, AdcFunctionCode.ReadInputRegisters,
                (ushort)AdcResultRegister.EventCount, 1, token);
        });
        var second = bus.Monitor.EnqueueAsync(token => bus.ReadFasteningResultAsync(1, token));
        try
        {
            Assert.False(firstEntered.Task.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Equal(0, bus.EventReads);
            Assert.Equal(0, bus.ResultReads);
            statusResponse.SetResult();
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var statusReads = bus.StatusReads;
            await Task.Delay(40); // Several status intervals elapse while the queued exchange owns the loop.
            Assert.Equal(statusReads, bus.StatusReads);
            Assert.False(second.IsCompleted);
            Assert.Equal(0, bus.ResultReads);
            firstResponse.SetResult();
            Assert.Equal(new ushort[] { 0 }, await first.WaitAsync(TimeSpan.FromSeconds(2)));
            await second.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, bus.EventReads);
            Assert.Equal(1, bus.ResultReads);
            Assert.True(await VirtualTest.WaitUntilAsync(() => bus.StatusReads > statusReads, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            bus.Close();
            await Task.WhenAll(first, second).ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Fact]
    public async Task CancelledQueuedReadIsNotSentAndTheFollowingRequestStillRuns()
    {
        var statusResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var bus = new AdcControllerStub { StatusReadBarrier = statusResponse.Task };
        bus.Open("Virtual", 115200);
        await bus.Monitor.StartAsync(1, CancellationToken.None);
        Assert.True(await VirtualTest.WaitUntilAsync(() => bus.StatusReads == 1, TimeSpan.FromSeconds(2)));
        using var cancellation = new CancellationTokenSource();
        var cancelled = bus.Monitor.EnqueueAsync(token => bus.ReadFasteningResultAsync(1, token), cancellation.Token);
        var next = bus.Monitor.EnqueueAsync(token => bus.ReadRegistersAsync(1, AdcFunctionCode.ReadInputRegisters,
            (ushort)AdcResultRegister.EventCount, 1, token));
        cancellation.Cancel();
        statusResponse.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(TimeSpan.FromSeconds(2)));
        await next.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, bus.ResultReads);
        Assert.Equal(1, bus.EventReads);
    }

    [Fact]
    public async Task DisconnectDrainsQueuedReadsBeforeAReopenedMonitorAcceptsNewWork()
    {
        using var bus = new AdcControllerStub { StatusReadBarrier = new TaskCompletionSource().Task };
        bus.Open("Virtual", 115200);
        await bus.Monitor.StartAsync(1, CancellationToken.None);
        Assert.True(await VirtualTest.WaitUntilAsync(() => bus.StatusReads == 1, TimeSpan.FromSeconds(2)));
        var first = bus.Monitor.EnqueueAsync(token => bus.ReadFasteningResultAsync(1, token));
        var second = bus.Monitor.EnqueueAsync(token => bus.ReadFasteningResultAsync(1, token));
        bus.Close();
        await Assert.ThrowsAsync<IOException>(() => first.WaitAsync(TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAsync<IOException>(() => second.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, bus.ResultReads);
        Assert.Null(bus.Monitor.Status);

        bus.StatusReadBarrier = null;
        bus.Open("Virtual", 115200);
        await bus.Monitor.StartAsync(1, CancellationToken.None);
        await bus.Monitor.EnqueueAsync(token => bus.ReadFasteningResultAsync(1, token));
        Assert.Equal(1, bus.ResultReads);
    }

    [Fact]
    public async Task MonitorReadFailureDuringFasteningStopsInsteadOfTreatingUnknownAsRunOff()
    {
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        var (io, head) = Create(bus, new() { StatusPollMilliseconds = 10 });
        await head.SelectPresetAsync(1);
        var cycle = head.TightenAsync();
        Assert.True(await VirtualTest.WaitUntilAsync(() => head.Monitor.Status?.Running == true, TimeSpan.FromSeconds(2)));
        bus.StatusReadFailure = new IOException("Status lost");
        await Assert.ThrowsAsync<AggregateException>(() => cycle);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.Equal(0, bus.ResultReads);
        Assert.Null(head.Monitor.Status);
    }

    [Fact]
    public async Task PollsOnlyStatusUntilRunOnThenOffUsingTheConfiguredInterval()
    {
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        foreach (var running in new[] { false, false, true, true, false })
            bus.RunReplies.Enqueue(running);
        var (io, head) = Create(bus, new() { StatusPollMilliseconds = 40 });
        await head.SelectPresetAsync(1);
        var startedAt = Environment.TickCount64;
        var cycle = head.TightenAsync();
        Assert.True(await VirtualTest.WaitUntilAsync(() => bus.StartWrites == 1, TimeSpan.FromSeconds(2)));
        Assert.False(cycle.IsCompleted);
        Assert.False(head.Monitor.Status!.Running); // Initial OFF cannot finish a new cycle.
        Assert.Equal(1, bus.EventReads);
        Assert.Equal(0, bus.ResultReads);
        Assert.True((await cycle).Success);
        Assert.True(Environment.TickCount64 - startedAt >= 140);
        Assert.Equal(1, bus.ResultReads);
        Assert.Equal(1, bus.EventReads);
        Assert.True(bus.StatusReads >= 7);
        Assert.False(bus.ResultReadWhileRunning);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOneShotResultReadIsRecordedWithoutRetryingOrRestarting(bool rejected)
    {
        using var bus = new AdcControllerStub { ResultReadFailure = ResultReplyFailure(rejected) };
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        var result = await head.TightenAsync();
        Assert.False(result.Success);
        Assert.Null(result.Controller);
        Assert.Null(result.Torque);
        Assert.NotNull(result.Error);
        Assert.Equal(1, bus.EventReads);
        Assert.Equal(1, bus.ResultReads);
        Assert.Equal(1, bus.StartWrites);
        Assert.Equal(1, bus.StopWrites);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Fact]
    public async Task CancellationWhileMonitoringRunStopsAndNextStartUsesAFreshBaseline()
    {
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        using var stop = new CancellationTokenSource();
        var running = head.TightenAsync(stop.Token);
        Assert.True(await VirtualTest.WaitUntilAsync(() => bus.StartWrites == 1, TimeSpan.FromSeconds(2)));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(0, bus.ResultReads);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        bus.SuppressCompletion = false;
        var next = head.TightenAsync();
        Assert.False(next.IsCompleted);
        Assert.Equal((ushort)2, (await next).Controller!.EventCount);
        Assert.Equal(1, bus.ResultReads);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedBaselineCannotStartFastening(bool rejected)
    {
        var failure = ResultReplyFailure(rejected);
        using var bus = new AdcControllerStub { BaselineReadFailure = failure };
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        Assert.Same(failure, await Assert.ThrowsAnyAsync<IOException>(() => head.TightenAsync()));
        Assert.Equal(0, bus.StartWrites);
        Assert.Equal(1, bus.EventReads);
        Assert.Equal(0, bus.ResultReads);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    private static IOException ResultReplyFailure(bool rejected)
    {
        if (rejected)
            return Assert.Throws<AdcResponseException>(() => AdcBus.ValidateResponse(
                [0x01, 0x84, 0x03, 0x03, 0x01], 1, AdcFunctionCode.ReadInputRegisters, 28));
        return Assert.Throws<AdcUnexpectedResponseException>(() => AdcBus.ValidateResponse(
            [0x01, 0x8C, 0x03, 0x04, 0xC1], 1, AdcFunctionCode.ReadInputRegisters, 28));
    }

    [Fact]
    public async Task OtherResultQueryRejectionsStillEndTheCycleAfterStop()
    {
        var frame = AdcRtuFrame.Build(1, (AdcFunctionCode)0x84, [0x02]);
        using var bus = new AdcControllerStub
        {
            ResultReadFailure = Assert.Throws<AdcResponseException>(() => AdcBus.ValidateResponse(
                frame, 1, AdcFunctionCode.ReadInputRegisters, 28)),
        };
        var (io, head) = Create(bus, new());
        await head.SelectPresetAsync(1);
        var result = await head.TightenAsync();
        Assert.False(result.Success);
        Assert.Null(result.Controller);
        Assert.Contains("0x02", result.Error);
        Assert.Equal(1, bus.ResultReads);
        Assert.Equal(1, bus.StopWrites);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Theory]
    [InlineData(AdcEventStatus.FasteningOk, 0, true)]
    [InlineData(AdcEventStatus.FasteningNg, 0, false)]
    [InlineData(AdcEventStatus.Error, 42, false)]
    public async Task RetainsEveryControllerResultRegister(AdcEventStatus status, ushort error, bool success)
    {
        ushort[] registers = [1, 1234, 1, 150, 147, 850, 3156, 19, 3175, 57, error, 0, (ushort)status, 123];
        using var bus = new AdcControllerStub();
        bus.ResultReplies.Enqueue(AdcFasteningResult.FromRegisters(registers));
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

    [Theory]
    [InlineData(0, AdcEventStatus.FasteningOk)]
    [InlineData(1, AdcEventStatus.DirectionChanged)]
    public async Task RunOffCannotTurnAnOldOrIncompleteResultIntoSuccess(ushort eventCount, AdcEventStatus status)
    {
        using var bus = new AdcControllerStub();
        var result = AdcFasteningResult.FromRegisters([eventCount, 250, 1, 100, 80, 1000, 0, 0, 0, 1, 0, 0, (ushort)status, 0]);
        bus.ResultReplies.Enqueue(result);
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        var completed = await head.TightenAsync();
        Assert.False(completed.Success);
        Assert.Null(completed.Controller);
        Assert.Contains("no new completed fastening result", completed.Error);
        Assert.Equal(1, bus.EventReads);
        Assert.Equal(1, bus.ResultReads);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Fact]
    public async Task ChangedEventIsAcceptedWithoutAssumingAnIncrementOrOrdering()
    {
        using var bus = new AdcControllerStub();
        bus.ResultReplies.Enqueue(AdcFasteningResult.FromRegisters(
            [ushort.MaxValue, 250, 1, 100, 80, 1000, 0, 0, 0, 1, 0, 0, 1, 0]));
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        var result = await head.TightenAsync();
        Assert.True(result.Success);
        Assert.Equal(ushort.MaxValue, result.Controller!.EventCount);
        Assert.Equal(1, bus.ResultReads);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Fact]
    public async Task EveryStartUsesANewBaselineAndCannotReuseThePreviousBoltResult()
    {
        using var bus = new AdcControllerStub();
        var (io, head) = Create(bus, new());
        await head.SelectPresetAsync(1);
        Assert.Equal((ushort)1, (await head.TightenAsync()).Controller!.EventCount);
        bus.ResultReplies.Enqueue(AdcFasteningResult.FromRegisters(
            [1, 250, 1, 100, 80, 1000, 0, 0, 0, 1, 0, 0, 1, 0]));
        await head.SelectPresetAsync(1);
        var next = await head.TightenAsync();
        Assert.False(next.Success);
        Assert.Null(next.Controller);
        Assert.Equal(2, bus.EventReads); // A fresh baseline for each START.
        Assert.Equal(2, bus.ResultReads);
        Assert.Equal((ushort)3, (await head.TightenAsync()).Controller!.EventCount);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Theory]
    [InlineData(2, AdcDirection.Fastening)]
    [InlineData(1, AdcDirection.Loosening)]
    public async Task MismatchedResultCannotBeRecorded(ushort preset, AdcDirection direction)
    {
        using var bus = new AdcControllerStub { ResultPreset = preset, ResultDirection = direction };
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync());
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResultAndDryRunSampleRunOnceAfterStartOff(bool dryRun)
    {
        using var bus = new AdcControllerStub { StopPollsRemaining = -1 };
        var (io, head) = Create(bus, new() { ResponseTimeoutMilliseconds = 80 });
        await head.SelectPresetAsync(1);
        Assert.True((await head.TightenAsync(dryRunMilliseconds: dryRun ? 20 : 0)).Success);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.Equal(dryRun, head.Monitor.Status!.Running);
        Assert.True(bus.StatusReads >= (dryRun ? 2 : 4));
        if (dryRun)
            await Assert.ThrowsAsync<InvalidOperationException>(() => head.SelectPresetAsync(1));
    }

    [Fact]
    public async Task MissingResultRecordsNgOnlyAfterIoStop()
    {
        using var bus = new AdcControllerStub { SuppressCompletion = true, StopPollsRemaining = -1 };
        var (io, head) = Create(bus, new() { FasteningTimeoutMilliseconds = 60 });
        await head.SelectPresetAsync(1);
        var result = await head.TightenAsync();
        Assert.False(result.Success);
        Assert.Null(result.Torque);
        Assert.Null(result.Controller);
        Assert.Contains("timed out", result.Error);
        Assert.Contains("RUN observed=", result.Error);
        Assert.Equal(1, bus.EventReads);
        Assert.Equal(0, bus.ResultReads);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.True(head.Monitor.Status!.Running);
        Assert.Equal(2, bus.StatusReads);
    }

    [Fact]
    public async Task TimeoutAndIoOutputFailureAreBothPreserved()
    {
        using var bus = new AdcControllerStub { SuppressCompletion = true, StopWriteFailure = new IOException("I/O write failed") };
        var (io, head) = Create(bus, new() { FasteningTimeoutMilliseconds = 50, ResponseTimeoutMilliseconds = 60 });
        await head.SelectPresetAsync(1);
        var error = await Assert.ThrowsAsync<AggregateException>(() => head.TightenAsync());
        Assert.IsType<TimeoutException>(error.InnerExceptions[0]);
        Assert.IsType<IOException>(error.InnerExceptions[1]);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Fact]
    public async Task SerialQueryFailureStillTurnsStartOff()
    {
        using var bus = new AdcControllerStub { ResultReadFailure = new IOException("Serial disconnected") };
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        await Assert.ThrowsAsync<IOException>(() => head.TightenAsync());
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Fact]
    public async Task IoFaultIsReportedAsEquipmentFailureAfterStartOff()
    {
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        var cycle = head.TightenAsync();
        io.IsReady = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => cycle);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Fact]
    public async Task HeadCommandTimeoutStillFailsAndTurnsStartOff()
    {
        using var bus = new AdcControllerStub();
        var (io, head) = Create(bus, new() { FasteningTimeoutMilliseconds = 60 });
        await head.SelectPresetAsync(1);
        await Assert.ThrowsAsync<TimeoutException>(() => head.TightenAsync(
            feedAsync: token => Task.Delay(Timeout.Infinite, token)));
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.Equal(0, bus.ResultReads);
    }

    [Fact]
    public async Task FailedHeadDownStillStopsAndDoesNotRecordAResult()
    {
        using var bus = new AdcControllerStub();
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync(
            feedAsync: token => throw new InvalidOperationException("Head output failed")));
        Assert.Equal(0, bus.ResultReads);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.True((await head.TightenAsync()).Success);
    }

    [Fact]
    public async Task DryRunNeedsNoAdcResultAndCancellationStopsMotor()
    {
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        var result = await head.TightenAsync(dryRunMilliseconds: 30);
        Assert.Equal(BoltResultSource.DryRun, result.Source);
        Assert.Equal(0, bus.ResultReads);
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
        using var bus = new AdcControllerStub { ResultStatus = AdcEventStatus.Error, ResultError = 125 };
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
        Assert.Equal((ushort)0, head.Monitor.Status!.Alarm);
        Assert.True(head.Monitor.Status.Ready);
        Assert.True(bus.StatusReads >= 6); // Shared monitoring includes RUN transitions.
        bus.ResultStatus = AdcEventStatus.FasteningOk;
        bus.ResultError = 0;
        Assert.True((await head.TightenAsync()).Success);
    }

    [Fact]
    public async Task AlarmWithoutCompletionResultIsSampledAtStop()
    {
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        var (io, head) = Create(bus, new() { FasteningTimeoutMilliseconds = 80 });
        await head.SelectPresetAsync(1);
        var cycle = head.TightenAsync();
        bus.CurrentAlarm = 125;
        Assert.True(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.Equal(1, bus.StatusReads);
        var result = await cycle;
        Assert.False(result.Success);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.Equal((ushort)125, head.Monitor.Status!.Alarm);
        Assert.Equal(2, bus.StatusReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetWithoutClearedAlarmAndReadyBlocksNextStart(bool alarmClears)
    {
        using var bus = new AdcControllerStub { CurrentAlarm = 42, ResetPollsRemaining = alarmClears ? 0 : -1, NotReady = alarmClears };
        var (io, head) = Create(bus, new() { ResponseTimeoutMilliseconds = 60 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.ResetAsync());
        Assert.Equal(1, bus.StatusReads);
        Assert.Equal(1, bus.ResetWrites);
        Assert.Equal(0, bus.StartWrites);
        Assert.False(io.GetOutput(OutputIo.PickupBoltReset));
    }

    [Fact]
    public async Task CancellationDuringResetAlwaysTurnsResetOff()
    {
        using var bus = new AdcControllerStub();
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
    public async Task PresetOutputChangeAndAdcNotReadyBlockStart()
    {
        using var bus = new AdcControllerStub();
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        io.SetOutput(OutputIo.PickupBoltPreset2, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync());
        Assert.Equal(0, bus.StartWrites);
        bus.NotReady = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.SelectPresetAsync(1));
        Assert.Equal(0, bus.StartWrites);
    }

    [Fact]
    public async Task ManualReverseUsesIoAndReleaseStopsIt()
    {
        using var bus = new VirtualAdcBus();
        var (io, head) = Create(bus);
        using var release = new CancellationTokenSource();
        var cycle = head.RunReverseAsync(release.Token);
        Assert.True(await VirtualTest.WaitUntilAsync(() => io.GetOutput(OutputIo.PickupBoltStart), TimeSpan.FromSeconds(2)));
        Assert.True(io.GetOutput(OutputIo.PickupBoltDirection));
        Assert.True(io.GetOutput(OutputIo.PickupBoltStart));
        release.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cycle);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
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
