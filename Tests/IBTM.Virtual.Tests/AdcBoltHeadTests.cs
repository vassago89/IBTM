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
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class AdcBoltHeadTests
{
    private static (VirtualIoService Io, AdcBoltHead Head) Create(
        IAdcBus bus, HantasSettings? settings = null, FasteningHead head = FasteningHead.Pickup)
    {
        var io = new VirtualIoService(VirtualTestSupport.Outputs(), new());
        var controller = VirtualTestSupport.CreateAdcHead(bus, io, head, settings ?? new(), 1, "Virtual", 115200);
        return (io, controller);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdleMonitorOnlyRenewsEventsAndReportsRenewalFailure(bool failRenewal)
    {
        using var bus = new AdcControllerStub();
        bus.Open("Virtual", 115200);
        await bus.Monitor.StartAsync(1, CancellationToken.None);
        Assert.Equal(new (ushort, ushort)[] { (4002, 1) }, bus.RegisterWrites);
        if (failRenewal)
            bus.RegisterWriteFailure = (4002, 1, new IOException("Renewal failed"));
        Assert.True(await VirtualTestSupport.WaitUntilAsync(() => bus.RegisterWrites.Count == 2,
            TimeSpan.FromSeconds(12)));
        Assert.Equal((4002, 1), ((int)bus.RegisterWrites[1].Address, (int)bus.RegisterWrites[1].Value));
        Assert.Equal(0, bus.StatusReads);
        Assert.Equal(0, bus.ResultReads);
        Assert.Equal(failRenewal, bus.Monitor.Sample?.Error is not null);
        bus.Close();
        Assert.NotNull(bus.Monitor.Sample?.Error);
    }

    [Theory]
    [InlineData(FasteningHead.Pickup, 1, 15, 100)]
    [InlineData(FasteningHead.Pickup, 2, 30, 80)]
    [InlineData(FasteningHead.Shooting, 3, 45, 95)]
    public async Task UsesIoControlsAndReceivesResultWithoutPolling(
        FasteningHead selected, ushort preset, ushort compensationAddress, ushort compensation)
    {
        using var bus = new VirtualAdcBus();
        var (io, head) = Create(bus, head: selected);
        await bus.WriteRegisterAsync(1, compensationAddress, compensation);
        var start = selected == FasteningHead.Pickup ? OutputIo.PickupBoltStart : OutputIo.ShootingBoltStart;
        var otherStart = selected == FasteningHead.Pickup ? OutputIo.ShootingBoltStart : OutputIo.PickupBoltStart;
        var resultReads = 0;
        var eventReads = 0;
        var statusReads = 0;
        var compensationReads = 0;
        bus.FrameTransferred += (direction, frame) =>
        {
            if (direction != AdcFrameDirection.Transmit)
                return;
            var address = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2));
            if (frame[1] == (byte)AdcFunctionCode.ReadHoldingRegisters)
            {
                compensationReads++;
                Assert.Equal(compensationAddress, address);
                Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4)));
                Assert.False(io.GetOutput(start));
                Assert.Equal(0, eventReads);
                return;
            }
            if (frame[1] == (byte)AdcFunctionCode.WriteSingleRegister && address == 4002)
                return;
            Assert.Equal((byte)AdcFunctionCode.ReadInputRegisters, frame[1]);
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
        var capturedCompensation = await head.ReadTorqueCompensationAsync(preset);
        await head.SelectPresetAsync(preset);
        var fed = false;
        var result = await head.TightenAsync(feedAsync: async token =>
        {
            Assert.True(io.GetOutput(start));
            Assert.False(io.GetOutput(otherStart));
            Assert.True(head.Monitor.Sample?.Status?.Running);
            fed = true;
            await Task.Delay(10, token);
        }, torqueCompensationPercent: capturedCompensation);
        Assert.True(result.Success);
        Assert.True(fed);
        Assert.NotNull(result.Controller);
        Assert.Equal(compensation, result.Controller.TorqueCompensationPercent);
        Assert.Equal(preset, result.Controller.Preset);
        Assert.Equal(1, compensationReads);
        Assert.Equal(1, eventReads); // Pre-START baseline only.
        Assert.Equal(0, resultReads);
        Assert.Equal(2, statusReads); // Preset and one RUN check only.
        Assert.False(io.GetOutput(start));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task ZFeedStopsWithControllerOrCancellation(bool cancel, bool stopFeedOnCompletion)
    {
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        var feedStopped = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var tightening = head.TightenAsync(timeout.Token, async token =>
        {
            Assert.True(head.Monitor.Sample!.Status!.Running);
            if (cancel)
                timeout.Cancel();
            else
                bus.SuppressCompletion = false;
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            finally
            {
                feedStopped = true;
            }
        }, stopFeedOnCompletion: stopFeedOnCompletion);
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tightening);
        else
        {
            var result = await tightening;
            Assert.True(result.Success);
            Assert.NotNull(result.Controller);
        }
        Assert.True(feedStopped);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(BeforeFeedFailure.Cancel)]
    [InlineData(BeforeFeedFailure.Communication)]
    [InlineData(BeforeFeedFailure.Alarm)]
    public async Task ZFeedWithCompletionStopDisabledFinishesTravelOrStopsOnFault(BeforeFeedFailure? failure)
    {
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var feedStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFeed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var feedStopped = false;
        var feedCanceled = false;
        var cycle = head.TightenAsync(stop.Token, async token =>
        {
            feedStarted.SetResult();
            try
            {
                await finishFeed.Task.WaitAsync(token);
            }
            finally
            {
                feedCanceled = token.IsCancellationRequested;
                feedStopped = true;
            }
        }, stopFeedOnCompletion: false);
        try
        {
            await feedStarted.Task.WaitAsync(stop.Token);
            bus.SuppressCompletion = false;
            Assert.True(await VirtualTestSupport.WaitUntilAsync(
                () => head.Monitor.LastEvent?.Result.Status == AdcEventStatus.FasteningOk, TimeSpan.FromSeconds(2)));
            Assert.False(feedStopped);
            Assert.False(cycle.IsCompleted);
            switch (failure)
            {
                case null:
                    finishFeed.SetResult();
                    Assert.True((await cycle).Success);
                    Assert.False(feedCanceled);
                    Assert.Equal(0, bus.ResultReads);
                    break;
                case BeforeFeedFailure.Cancel:
                    stop.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cycle);
                    break;
                case BeforeFeedFailure.Communication:
                    bus.Close();
                    await Assert.ThrowsAsync<IOException>(() => cycle);
                    break;
                case BeforeFeedFailure.Alarm:
                    bus.Monitor.ReceiveEvent(bus.Monitor.LastEvent!.Result with { EventCount = 99, Status = AdcEventStatus.Error, Error = 125 });
                    await Assert.ThrowsAsync<InvalidOperationException>(() => cycle);
                    break;
            }
            Assert.True(feedStopped);
            if (failure is not null)
                Assert.True(feedCanceled);
            Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        }
        finally
        {
            stop.Cancel();
            finishFeed.TrySetResult();
            await ((Task)cycle).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    public enum BeforeFeedFailure { Cancel, Timeout, IoFault, Communication, Alarm }

    [Theory]
    [InlineData(BeforeFeedFailure.Cancel)]
    [InlineData(BeforeFeedFailure.Timeout)]
    [InlineData(BeforeFeedFailure.IoFault)]
    [InlineData(BeforeFeedFailure.Communication)]
    [InlineData(BeforeFeedFailure.Alarm)]
    public async Task FailureBeforeRunCannotLowerHead(BeforeFeedFailure scenario)
    {
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        var (io, head) = Create(bus, new() { FasteningTimeoutMilliseconds = 80 });
        await head.SelectPresetAsync(1);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bus.StatusReadBarrier = blocked.Task;
        using var stop = new CancellationTokenSource();
        var fed = false;
        var cycle = head.TightenAsync(stop.Token, token =>
        {
            fed = true;
            return Task.CompletedTask;
        });
        Assert.True(await VirtualTestSupport.WaitUntilAsync(() => bus.StatusReads == 2, TimeSpan.FromSeconds(1)));
        switch (scenario)
        {
            case BeforeFeedFailure.Cancel:
                stop.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cycle);
                break;
            case BeforeFeedFailure.Timeout:
                await Assert.ThrowsAsync<TimeoutException>(() => cycle);
                break;
            case BeforeFeedFailure.IoFault:
                io.IsReady = false;
                await Assert.ThrowsAsync<InvalidOperationException>(() => cycle);
                break;
            case BeforeFeedFailure.Communication:
                bus.Close();
                await Assert.ThrowsAsync<IOException>(() => cycle);
                break;
            case BeforeFeedFailure.Alarm:
                bus.CurrentAlarm = 125;
                blocked.SetResult();
                await Assert.ThrowsAsync<InvalidOperationException>(() => cycle);
                break;
        }
        blocked.TrySetResult();
        Assert.False(fed);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.Equal(0, bus.ResultReads);
    }

    [Fact]
    public async Task ControllerErrorBeforeRunKeepsItsResultWithoutLoweringHead()
    {
        using var bus = new AdcControllerStub { ResultStatus = AdcEventStatus.Error, ResultError = 125 };
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        bus.RunReplies.Enqueue(false);
        void AlarmOnStart(OutputIo output, bool on)
        {
            if (output == OutputIo.PickupBoltStart && on)
                bus.Monitor.ReceiveEvent(AdcFasteningResult.FromRegisters(
                    [1, 0, 1, 100, 0, 1000, 0, 0, 0, 1, 125, 0, 6, 0]));
        }
        io.OutputChanged += AlarmOnStart;
        try
        {
            var fed = false;
            BoltResult? received = null;
            var result = await head.TightenAsync(feedAsync: token =>
            {
                fed = true;
                return Task.CompletedTask;
            }, resultReceived: completed => received = completed);
            Assert.False(fed);
            Assert.False(result.Success);
            Assert.Same(result, received);
            Assert.Equal((ushort)125, result.Controller!.ErrorCode);
            Assert.Equal(0, bus.ResultReads);
            Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        }
        finally
        {
            io.OutputChanged -= AlarmOnStart;
        }
    }

    [Fact]
    public async Task TighteningUsesCapturedCompensationWithoutRereadingOrRescalingTorque()
    {
        using var bus = new AdcControllerStub();
        var (io, head) = Create(bus);
        var preliminaryCompensation = await head.ReadTorqueCompensationAsync(2);
        var finalCompensation = await head.ReadTorqueCompensationAsync(1);
        await head.SelectPresetAsync(2);
        var first = await head.TightenAsync(torqueCompensationPercent: preliminaryCompensation);
        Assert.True(first.Success);
        Assert.Equal((ushort)80, first.Controller!.TorqueCompensationPercent);
        Assert.Equal((ushort)2, first.Controller.Preset);
        Assert.Equal(1, first.Torque);

        await head.SelectPresetAsync(1);
        var second = await head.TightenAsync(torqueCompensationPercent: finalCompensation);
        Assert.True(second.Success);
        Assert.Equal((ushort)100, second.Controller!.TorqueCompensationPercent);
        Assert.Equal((ushort)1, second.Controller.Preset);

        bus.TorqueCompensations[2] = 85;
        await head.SelectPresetAsync(2);
        var third = await head.TightenAsync(torqueCompensationPercent: preliminaryCompensation);
        Assert.True(third.Success);
        Assert.Equal((ushort)80, third.Controller!.TorqueCompensationPercent);
        Assert.Equal((ushort)80, first.Controller.TorqueCompensationPercent);
        Assert.Equal(1, third.Torque);
        Assert.Equal(2, bus.CompensationReads);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCompensationReadCannotStartFastening(bool rejected)
    {
        var failure = rejected
            ? new AdcResponseException(3, "Compensation query rejected.")
            : new IOException("Compensation query disconnected.");
        using var bus = new AdcControllerStub { CompensationReadFailure = failure };
        var (io, head) = Create(bus);
        Assert.Same(failure, await Assert.ThrowsAnyAsync<IOException>(() => head.ReadTorqueCompensationAsync(1)));
        Assert.Equal(1, bus.CompensationReads);
        Assert.Equal(0, bus.EventReads);
        Assert.Equal(0, bus.StartWrites);
        Assert.Equal(0, bus.ResultReads);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Fact]
    public async Task CancellationDuringCompensationReadCannotStartFastening()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var bus = new AdcControllerStub { CompensationReadBarrier = barrier.Task };
        var (io, head) = Create(bus);
        using var stop = new CancellationTokenSource();
        var cycle = head.ReadTorqueCompensationAsync(1, stop.Token);
        Assert.True(await VirtualTestSupport.WaitUntilAsync(() => bus.CompensationReads == 1, TimeSpan.FromSeconds(2)));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cycle);
        Assert.Equal(0, bus.EventReads);
        Assert.Equal(0, bus.StartWrites);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Fact]
    public async Task ReadinessRejectsUnknownFeedbackWithoutPolling()
    {
        using var bus = new AdcControllerStub { StatusRejection = "RX=0184030301; status request rejected" };
        var (io, head) = Create(bus, new() { ResponseTimeoutMilliseconds = 100 });

        var error = await Assert.ThrowsAsync<IOException>(() => head.CheckReadyAsync());

        Assert.Contains(bus.StatusRejection, error.Message);
        Assert.Equal(1, bus.StatusReads);
        Assert.Null(head.Monitor.Sample?.Status);
        Assert.Equal(0, bus.StartWrites);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Fact]
    public async Task CancelledQueuedReadIsNotSentAndTheFollowingRequestStillRuns()
    {
        var statusResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var bus = new AdcControllerStub { StatusReadBarrier = statusResponse.Task };
        bus.Open("Virtual", 115200);
        await bus.Monitor.StartAsync(1, CancellationToken.None);
        var statusRead = bus.Monitor.ReadStatusAsync(CancellationToken.None);
        Assert.True(await VirtualTestSupport.WaitUntilAsync(() => bus.StatusReads == 1, TimeSpan.FromSeconds(2)));
        using var cancellation = new CancellationTokenSource();
        var cancelled = bus.Monitor.EnqueueAsync(token => bus.ReadFasteningResultAsync(1, token), cancellation.Token);
        var next = bus.Monitor.EnqueueAsync(token => bus.ReadRegistersAsync(1, AdcFunctionCode.ReadInputRegisters,
            (ushort)AdcResultRegister.EventCount, 1, token));
        cancellation.Cancel();
        statusResponse.SetResult();
        await statusRead;
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
        var statusRead = bus.Monitor.ReadStatusAsync(CancellationToken.None);
        Assert.True(await VirtualTestSupport.WaitUntilAsync(() => bus.StatusReads == 1, TimeSpan.FromSeconds(2)));
        var first = bus.Monitor.EnqueueAsync(token => bus.ReadFasteningResultAsync(1, token));
        var second = bus.Monitor.EnqueueAsync(token => bus.ReadFasteningResultAsync(1, token));
        bus.Close();
        await Assert.ThrowsAsync<IOException>(() => statusRead);
        await Assert.ThrowsAsync<IOException>(() => first.WaitAsync(TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAsync<IOException>(() => second.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, bus.ResultReads);
        Assert.Null(bus.Monitor.Sample?.Status);

        bus.StatusReadBarrier = null;
        bus.Open("Virtual", 115200);
        await bus.Monitor.StartAsync(1, CancellationToken.None);
        await bus.Monitor.EnqueueAsync(token => bus.ReadFasteningResultAsync(1, token));
        Assert.Equal(1, bus.ResultReads);
    }

    [Fact]
    public async Task GraphIsRequestedOnlyAfterTheFasteningEvent()
    {
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        await bus.Monitor.SetTorqueCurveMonitoringAsync(true, CancellationToken.None);
        bus.GraphRequestFailure = new TimeoutException("No graph available while fastening.");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cycle = head.TightenAsync(stop.Token);
        try
        {
            Assert.True(await VirtualTestSupport.WaitUntilAsync(
                () => bus.Running, TimeSpan.FromSeconds(2)));
            Assert.False(cycle.IsCompleted);
            Assert.True(io.GetOutput(OutputIo.PickupBoltStart));
            Assert.True(bus.Running);
            Assert.Equal(1, bus.GraphRequests);
            Assert.Equal(0, bus.ResultReads);

            bus.SuppressCompletion = false;
            var result = await cycle.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(result.Success, result.Error);
            Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
            bus.GraphRequestFailure = null;
            var curve = await bus.Monitor.ReadTorqueCurveAsync(result.Controller!, result.Torque, 1_000, stop.Token);
            Assert.Equal(result.Torque, curve.FinalTorque);
            Assert.Equal(2, bus.GraphRequests);
        }
        finally
        {
            stop.Cancel();
            await ((Task)cycle).ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Fact]
    public async Task CancellationWhileWaitingForEventStopsAndNextStartUsesAFreshBaseline()
    {
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        using var stop = new CancellationTokenSource();
        var running = head.TightenAsync(stop.Token);
        Assert.True(await VirtualTestSupport.WaitUntilAsync(() => bus.StartWrites == 1, TimeSpan.FromSeconds(2)));
        Assert.Equal(1, bus.StatusReads);
        Assert.True(io.GetOutput(OutputIo.PickupBoltStart));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(0, bus.ResultReads);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        bus.SuppressCompletion = false;
        var next = head.TightenAsync();
        Assert.False(next.IsCompleted);
        Assert.Equal((ushort)2, (await next).Controller!.EventCount);
        Assert.Equal(0, bus.ResultReads);
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
            return new AdcResponseException(3, "HComm error 0x03; RX=0184030301.");
        return new AdcUnexpectedResponseException("HComm reply mismatch for fastening result.");
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
    public async Task OldOrIncompleteEventCannotCompleteFastening(ushort eventCount, AdcEventStatus status)
    {
        using var bus = new AdcControllerStub();
        var result = AdcFasteningResult.FromRegisters([eventCount, 250, 1, 100, 80, 1000, 0, 0, 0, 1, 0, 0, (ushort)status, 0]);
        bus.ResultReplies.Enqueue(result);
        var (io, head) = Create(bus, new() { FasteningTimeoutMilliseconds = 120 });
        await head.SelectPresetAsync(1);
        var completed = await head.TightenAsync();
        Assert.False(completed.Success);
        Assert.Null(completed.Controller);
        Assert.Contains("no fastening event", completed.Error);
        Assert.Equal(1, bus.EventReads);
        Assert.Equal(0, bus.ResultReads);
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
        Assert.Equal(0, bus.ResultReads);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Fact]
    public async Task EveryStartUsesANewBaselineAndCannotReuseThePreviousBoltResult()
    {
        using var bus = new AdcControllerStub();
        var (io, head) = Create(bus, new() { FasteningTimeoutMilliseconds = 120 });
        await head.SelectPresetAsync(1);
        Assert.Equal((ushort)1, (await head.TightenAsync()).Controller!.EventCount);
        bus.ResultReplies.Enqueue(AdcFasteningResult.FromRegisters(
            [1, 250, 1, 100, 80, 1000, 0, 0, 0, 1, 0, 0, 1, 0]));
        await head.SelectPresetAsync(1);
        var next = await head.TightenAsync();
        Assert.False(next.Success);
        Assert.Null(next.Controller);
        Assert.Equal(2, bus.EventReads); // A fresh baseline for each START.
        Assert.Equal(0, bus.ResultReads);
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
    public async Task StartOffDoesNotWaitForAnotherStatusRead(bool dryRun)
    {
        using var bus = new AdcControllerStub { IgnoreStop = true };
        var (io, head) = Create(bus, new() { ResponseTimeoutMilliseconds = 80 });
        await head.SelectPresetAsync(1);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void BlockAfterStop(OutputIo output, bool on)
        {
            if (output == OutputIo.PickupBoltStart && !on)
                bus.StatusReadBarrier = blocked.Task;
        }
        io.OutputChanged += BlockAfterStop;
        try
        {
            var result = await head.TightenAsync(dryRunMilliseconds: dryRun ? 20 : 0)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(result.Success);
            Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
            Assert.Equal(1, bus.StopWrites);
            Assert.Equal(0, bus.ResultReads);
            blocked.SetResult();
            if (dryRun)
                await Assert.ThrowsAsync<InvalidOperationException>(() => head.SelectPresetAsync(1));
            else
                await head.SelectPresetAsync(1);
        }
        finally
        {
            blocked.TrySetResult();
            io.OutputChanged -= BlockAfterStop;
        }
    }

    [Fact]
    public async Task CancellationAfterReceivingAResultPreservesItAndStopsStart()
    {
        using var bus = new AdcControllerStub();
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        using var stop = new CancellationTokenSource();
        BoltResult? received = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => head.TightenAsync(
            stop.Token, resultReceived: result =>
            {
                Assert.True(io.GetOutput(OutputIo.PickupBoltStart));
                received = result;
                stop.Cancel();
            }));
        Assert.True(received?.Success);
        Assert.NotNull(received?.Torque);
        Assert.NotNull(received?.Controller);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Fact]
    public async Task MissingResultRecordsNgOnlyAfterIoStop()
    {
        using var bus = new AdcControllerStub { SuppressCompletion = true, IgnoreStop = true };
        var (io, head) = Create(bus, new() { FasteningTimeoutMilliseconds = 60 });
        await head.SelectPresetAsync(1);
        var result = await head.TightenAsync();
        Assert.False(result.Success);
        Assert.Null(result.Torque);
        Assert.Null(result.Controller);
        Assert.Contains("no fastening event", result.Error);
        Assert.Equal(1, bus.EventReads);
        Assert.Equal(0, bus.ResultReads);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.True(bus.Running); // START OFF is not a claim that RUN feedback has stopped.
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task InvalidFasteningTimeoutCannotStartMotor(int milliseconds)
    {
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        var (io, head) = Create(bus, new() { FasteningTimeoutMilliseconds = milliseconds });
        await head.SelectPresetAsync(1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => head.TightenAsync(stop.Token));

        Assert.Equal(0, bus.StartWrites);
        Assert.Equal(0, bus.EventReads);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Fact]
    public async Task InvalidReadyTimeoutCannotStartStatusAcquisition()
    {
        var settings = new HantasSettings { ResponseTimeoutMilliseconds = -1 };
        using var controller = new AdcControllerStub();
        var (io, head) = Create(controller, settings);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => head.CheckReadyAsync());
        Assert.False(controller.IsOpen);
        Assert.Equal(0, controller.StatusReads);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SerialQueryFailureStillTurnsStartOff(bool timedOut)
    {
        Exception failure = timedOut ? new TimeoutException("RUN check timed out") : new IOException("Serial disconnected");
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bus.StatusReadBarrier = blocked.Task;
        var cycle = head.TightenAsync(feedAsync: token => Task.CompletedTask);
        Assert.True(await VirtualTestSupport.WaitUntilAsync(() => bus.StatusReads == 2, TimeSpan.FromSeconds(1)));
        if (timedOut)
            blocked.SetException(failure);
        else
        {
            bus.StatusReadFailure = (IOException)failure;
            blocked.SetResult();
        }
        Assert.Same(failure, await Assert.ThrowsAsync(failure.GetType(), () => cycle));
        Assert.Equal(1, bus.StartWrites);
        Assert.Equal(0, bus.ResultReads);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IoFaultAlreadyBeingPublishedCanFinishAfterTheOperationIsDisposed(bool reverse)
    {
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        var (io, head) = Create(bus);
        using var publish = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        io.Faulted += exception =>
        {
            entered.SetResult();
            Assert.True(publish.Wait(TimeSpan.FromSeconds(5)));
        };
        await head.SelectPresetAsync(1);
        using var stop = new CancellationTokenSource();
        var operation = reverse ? head.RunReverseAsync(stop.Token) : head.TightenAsync(stop.Token);
        Assert.True(await VirtualTestSupport.WaitUntilAsync(
            () => io.GetOutput(OutputIo.PickupBoltStart), TimeSpan.FromSeconds(2)));
        var fault = Task.Run(() => io.IsReady = false);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => operation.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
            publish.Set();
            await fault.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            stop.Cancel();
            publish.Set();
            await Task.WhenAll(operation, fault).ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Fact]
    public async Task HeadCommandTimeoutStillFailsAndTurnsStartOff()
    {
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        var (io, head) = Create(bus, new() { FasteningTimeoutMilliseconds = 200 });
        await head.SelectPresetAsync(1);
        var fed = false;
        await Assert.ThrowsAsync<TimeoutException>(() => head.TightenAsync(
            feedAsync: token =>
            {
                fed = true;
                return Task.Delay(Timeout.Infinite, token);
            }));
        Assert.True(fed);
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
        Assert.Equal(0, bus.CompensationReads);
        Assert.Equal(0, bus.ResultReads);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        using var stop = new CancellationTokenSource();
        var cycle = head.TightenAsync(stop.Token, dryRunMilliseconds: 2000);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cycle);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Theory]
    [InlineData(500, 5000)]
    [InlineData(1500, 5000)]
    [InlineData(100, 300)]
    public async Task DryRunDeadlineIncludesZFeedAndWaitsForItsStop(int feedMilliseconds, int fasteningTimeoutMilliseconds)
    {
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        var (io, head) = Create(bus, new()
        {

            FasteningTimeoutMilliseconds = fasteningTimeoutMilliseconds,
        });
        await head.SelectPresetAsync(1);
        var feedStopped = false;
        var feedCanceled = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var started = Stopwatch.GetTimestamp();
        var result = await head.TightenAsync(timeout.Token, async token =>
        {
            try
            {
                await Task.Delay(feedMilliseconds, token);
            }
            catch (OperationCanceledException)
            {
                feedCanceled = true;
                throw;
            }
            finally
            {
                feedStopped = true;
            }
        }, dryRunMilliseconds: 600);

        Assert.InRange(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 550, 1000);
        Assert.Equal(BoltResultSource.DryRun, result.Source);
        Assert.Equal(feedMilliseconds > 600, feedCanceled);
        Assert.True(feedStopped);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.Equal(0, bus.ResultReads);
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
        Assert.Equal((ushort)0, head.Monitor.Sample?.Status!.Alarm);
        Assert.True(head.Monitor.Sample?.Status?.Ready);
        Assert.Equal(4, bus.StatusReads); // Initial selection, alarm check, reset, final preset check.
        bus.ResultStatus = AdcEventStatus.FasteningOk;
        bus.ResultError = 0;
        Assert.True((await head.TightenAsync()).Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetWithoutClearedAlarmAndReadyBlocksNextStart(bool alarmClears)
    {
        using var bus = new AdcControllerStub { CurrentAlarm = 42, IgnoreReset = !alarmClears, NotReady = alarmClears };
        var (io, head) = Create(bus, new() { ResponseTimeoutMilliseconds = 60 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.ResetAsync());
        Assert.Equal(1, bus.StatusReads);
        Assert.Equal(1, bus.ResetWrites);
        Assert.Equal(0, bus.StartWrites);
        Assert.False(io.GetOutput(OutputIo.PickupBoltReset));
    }

    [Fact]
    public async Task ResetOnAndOffFailuresAreBothPreserved()
    {
        using var bus = new AdcControllerStub();
        var (io, head) = Create(bus);
        var onFailure = new IOException("RESET ON failed.");
        var offFailure = new IOException("RESET OFF failed.");
        var resetAttempts = new List<bool>();
        io.OutputChanged += (output, on) =>
        {
            if (output != OutputIo.PickupBoltReset)
                return;
            resetAttempts.Add(on);
            throw on ? onFailure : offFailure;
        };

        var error = await Assert.ThrowsAsync<AggregateException>(() => head.ResetAsync());

        Assert.Equal(new[] { true, false }, resetAttempts);
        Assert.Equal(new Exception[] { onFailure, offFailure }, error.InnerExceptions);
        Assert.Equal(0, bus.StartWrites);
        Assert.Equal(0, bus.StatusReads);
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
        var (io, head) = Create(bus, new() { ResponseTimeoutMilliseconds = 50 });
        await head.SelectPresetAsync(1);
        io.SetOutput(OutputIo.PickupBoltPreset2, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync());
        Assert.Equal(0, bus.StartWrites);
        bus.NotReady = true;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => head.SelectPresetAsync(1));
        Assert.Contains("READY=False", error.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync());
        Assert.Equal(0, bus.StartWrites);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PresetSettlingKeepsStartOffAndChecksFeedbackAfterTheDelay(bool cancel)
    {
        using var bus = new AdcControllerStub();
        var settings = new HantasSettings
        {
            ResponseTimeoutMilliseconds = 500, PresetSettleMilliseconds = 0,
        };
        var (io, head) = Create(bus, settings);
        await head.SelectPresetAsync(2);
        var preliminary = await head.TightenAsync();
        Assert.True(preliminary.Success);
        settings.PresetSettleMilliseconds = 400; // The existing head reads edits at the next selection.
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var started = Stopwatch.StartNew();
        var selecting = head.SelectPresetAsync(1, stop.Token);
        try
        {
            await Task.Delay(250); // Longer than the former hard-coded 200 ms.
            Assert.False(selecting.IsCompleted);
            Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
            Assert.Equal(1, bus.StartWrites);
            if (cancel)
            {
                stop.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selecting);
                await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync());
                Assert.Equal(1, bus.StartWrites);
            }
            else
            {
                // An earlier READY sample cannot release START after the settling delay.
                bus.NotReady = true;
                bus.ReportedPreset = 2;
                await Task.Delay(settings.PresetSettleMilliseconds);
                await Assert.ThrowsAsync<InvalidOperationException>(() => selecting);
                Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
                Assert.Equal(1, bus.StartWrites);
                bus.NotReady = false;
                bus.ReportedPreset = null;
                await head.SelectPresetAsync(1, stop.Token);
                Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(settings.PresetSettleMilliseconds - 10));
                Assert.True(head.Monitor.Sample!.Status!.Ready);
                Assert.Equal((ushort)1, head.Monitor.Sample.Status.Preset);
                var final = await head.TightenAsync();
                Assert.True(final.Success);
                Assert.Equal((ushort)1, final.Controller!.Preset);
                Assert.Equal(2, bus.StartWrites);
            }
            Assert.Equal(0, bus.ResetWrites);
        }
        finally
        {
            stop.Cancel();
            await selecting.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Theory]
    [InlineData(FasteningHead.Pickup)]
    [InlineData(FasteningHead.Shooting)]
    public async Task SamePresetKeepsOutputsAndSkipsSettling(FasteningHead selected)
    {
        using var bus = new AdcControllerStub();
        var (io, head) = Create(bus, new()
        {
            PresetSettleMilliseconds = 5000,
        }, selected);
        OutputIo[] presets = selected == FasteningHead.Pickup
            ? [OutputIo.PickupBoltPreset1, OutputIo.PickupBoltPreset2, OutputIo.PickupBoltPreset3]
            : [OutputIo.ShootingBoltPreset1, OutputIo.ShootingBoltPreset2, OutputIo.ShootingBoltPreset3];
        // A matching live selection also works without any prior software selection.
        io.SetOutput(presets[1], true);
        var changes = new List<(OutputIo Output, bool On)>();
        io.OutputChanged += (output, on) =>
        {
            if (Array.IndexOf(presets, output) >= 0)
                changes.Add((output, on));
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await head.SelectPresetAsync(2, stop.Token);
        Assert.Equal(1, bus.StatusReads);
        Assert.Empty(changes);
        Assert.True((await head.TightenAsync(stop.Token)).Success);

        await head.SelectPresetAsync(2, stop.Token);
        Assert.Equal(2, bus.StatusReads);
        Assert.Empty(changes);
        Assert.True((await head.TightenAsync(stop.Token)).Success);
        Assert.Equal(2, bus.StartWrites);
        Assert.Equal(0, bus.ResultReads);
        Assert.False(bus.Running);
    }

    [Fact]
    public async Task ChangedPresetOutputsRequireSelectionAndSettlingAgain()
    {
        using var bus = new AdcControllerStub();
        var settings = new HantasSettings { PresetSettleMilliseconds = 0 };
        var (io, head) = Create(bus, settings);
        await head.SelectPresetAsync(2);
        // Checking only the requested output or cached preset would miss this invalid pair.
        io.SetOutput(OutputIo.PickupBoltPreset3, true);
        settings.PresetSettleMilliseconds = 200;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var started = Stopwatch.StartNew();
        await head.SelectPresetAsync(2, stop.Token);
        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(settings.PresetSettleMilliseconds - 10));
        Assert.False(io.GetOutput(OutputIo.PickupBoltPreset1));
        Assert.True(io.GetOutput(OutputIo.PickupBoltPreset2));
        Assert.False(io.GetOutput(OutputIo.PickupBoltPreset3));
        Assert.Equal((ushort)2, head.Monitor.Sample!.Status!.Preset);
        Assert.True(head.Monitor.Sample.Status.Ready);
        Assert.Equal(0, bus.StartWrites);
    }

    [Fact]
    public async Task NegativePresetDelayCannotChangeOutputsOrStart()
    {
        using var bus = new AdcControllerStub();
        var (io, head) = Create(bus, new() { PresetSettleMilliseconds = -1 });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => head.SelectPresetAsync(1));
        Assert.False(io.GetOutput(OutputIo.PickupBoltPreset1));
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
        Assert.True(await VirtualTestSupport.WaitUntilAsync(() => io.GetOutput(OutputIo.PickupBoltStart), TimeSpan.FromSeconds(2)));
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
        var settings = new MachineSettings();
        settings.Hantas.PickupPortName = "Pickup";
        settings.Hantas.ShootingPortName = "Shooting";
        settings.Hantas.PickupSlaveAddress = 1;
        settings.Hantas.ShootingSlaveAddress = 1;
        settings.Hantas.ShootingBaudRate = 57600;
        await using var services = MachineTestSupport.CreateServices(settings);
        var pickupBus = (VirtualAdcBus)services.GetRequiredKeyedService<IAdcBus>(FasteningHead.Pickup);
        var shootingBus = (VirtualAdcBus)services.GetRequiredKeyedService<IAdcBus>(FasteningHead.Shooting);
        var pickup = services.GetRequiredKeyedService<IBoltHead>(FasteningHead.Pickup);
        var shooting = services.GetRequiredKeyedService<IBoltHead>(FasteningHead.Shooting);
        Assert.Same(pickupBus.Monitor, pickup.Monitor);
        Assert.Same(shootingBus.Monitor, shooting.Monitor);
        // Editing the settings cannot change an existing head's port or slave.
        settings.Hantas.PickupPortName = "NextPickup";
        settings.Hantas.PickupSlaveAddress = 2;
        settings.Hantas.ShootingBaudRate = 19200;
        pickupBus.SetNextFasteningResult(1, AdcEventStatus.FasteningNg);
        await pickup.SelectPresetAsync(1);
        await shooting.SelectPresetAsync(1);
        var results = await Task.WhenAll(pickup.TightenAsync(), shooting.TightenAsync());
        Assert.False(results[0].Success);
        Assert.Equal("Pickup", results[0].Controller!.Port);
        Assert.True(results[1].Success);
        Assert.Equal("Shooting", results[1].Controller!.Port);
        Assert.Equal(115200, pickupBus.BaudRate);
        Assert.Equal(57600, shootingBus.BaudRate);
    }
}
