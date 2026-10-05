using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Device;
using IBTM.Hantas;
using IBTM.Virtual;
using Microsoft.Extensions.Logging;
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
    [InlineData(FasteningHead.Pickup, 1, 15, 100)]
    [InlineData(FasteningHead.Pickup, 2, 30, 80)]
    [InlineData(FasteningHead.Shooting, 3, 45, 95)]
    public async Task UsesIoControlsAndReadsResultOnceAfterRunTurnsOff(
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
        Assert.Equal(1, resultReads);
        Assert.True(statusReads >= 3); // Preset, RUN ON and RUN OFF.
        Assert.False(io.GetOutput(start));
    }

    [Fact]
    public async Task HeadDownIgnoresAQueryStartedBeforeStart()
    {
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        var (io, head) = Create(bus, new() { StatusPollMilliseconds = 10 });
        await head.SelectPresetAsync(1);
        var oldReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var freshReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bus.StatusReadBarrier = oldReply.Task;
        var reads = bus.StatusReads;
        Assert.True(await VirtualTestSupport.WaitUntilAsync(() => bus.StatusReads > reads, TimeSpan.FromSeconds(2)));
        var fed = false;
        void HoldNextQuery(AdcStatusSample sample)
        {
            if (!oldPublished.Task.IsCompleted)
            {
                bus.StatusReadBarrier = freshReply.Task;
                oldPublished.TrySetResult();
            }
        }
        head.Monitor.Sampled += HoldNextQuery;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var cycle = head.TightenAsync(stop.Token, token =>
        {
            fed = true;
            return Task.CompletedTask;
        }, dryRunMilliseconds: 10);
        try
        {
            Assert.True(io.GetOutput(OutputIo.PickupBoltStart));
            oldReply.SetResult();
            await oldPublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(await VirtualTestSupport.WaitUntilAsync(() => bus.StatusReads > reads + 1, TimeSpan.FromSeconds(2)));
            Assert.False(fed);
            freshReply.SetResult();
            Assert.Equal(BoltResultSource.DryRun, (await cycle).Source);
            Assert.True(fed);
            Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        }
        finally
        {
            stop.Cancel();
            oldReply.TrySetResult();
            freshReply.TrySetResult();
            head.Monitor.Sampled -= HoldNextQuery;
            await ((Task)cycle).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Theory]
    [InlineData(FasteningHead.Pickup, 0)]
    [InlineData(FasteningHead.Shooting, 0)]
    [InlineData(FasteningHead.Pickup, 20)]
    public async Task HeadDownWaitsThroughRejectedAndRunOffFeedback(FasteningHead selected, int dryRunMilliseconds)
    {
        using var bus = new AdcControllerStub();
        var io = new VirtualIoService(VirtualTestSupport.Outputs(new BoltFasteningHardwareSettings()), new());
        var head = VirtualTestSupport.CreateAdcHead(bus, io, selected,
            new() { StatusPollMilliseconds = 10 }, 1, "Virtual", 115200);
        await head.SelectPresetAsync(1);
        bus.StatusRejection = "0x03: no status feedback";
        bus.RunReplies.Enqueue(false);
        bus.RunReplies.Enqueue(false);
        bus.RunReplies.Enqueue(true);
        bus.RunReplies.Enqueue(false);
        var start = selected == FasteningHead.Pickup ? OutputIo.PickupBoltStart : OutputIo.ShootingBoltStart;
        var down = selected == FasteningHead.Pickup ? OutputIo.PickupHeadDown : OutputIo.ShootingHeadDown;
        var offSamples = 0;
        void CheckBeforeRun(AdcStatusSample sample)
        {
            if (io.GetOutput(start) && !io.GetOutput(down) && sample.Status is { Running: false })
                offSamples++;
        }
        head.Monitor.Sampled += CheckBeforeRun;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var reads = bus.StatusReads;
        var cycle = head.TightenAsync(stop.Token, token =>
        {
            token.ThrowIfCancellationRequested();
            Assert.True(io.GetOutput(start));
            Assert.True(head.Monitor.Sample?.Status?.Running);
            Assert.Equal(2, offSamples);
            io.SetOutput(down, true);
            return Task.CompletedTask;
        }, dryRunMilliseconds);
        try
        {
            Assert.True(await VirtualTestSupport.WaitUntilAsync(() => bus.StatusReads >= reads + 3, TimeSpan.FromSeconds(2)));
            Assert.True(io.GetOutput(start));
            Assert.False(io.GetOutput(down));
            bus.StatusRejection = null;
            Assert.True((await cycle).Success);
            Assert.True(io.GetOutput(down));
            Assert.False(io.GetOutput(start));
            Assert.Equal(dryRunMilliseconds == 0 ? 1 : 0, bus.ResultReads);
            Assert.False(bus.ConcurrentStatusReadsDetected);
        }
        finally
        {
            stop.Cancel();
            head.Monitor.Sampled -= CheckBeforeRun;
            await ((Task)cycle).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(120, 0)]
    [InlineData(120, 20)]
    public async Task HeadFeedDelayStartsAfterRun(int delayMilliseconds, int dryRunMilliseconds)
    {
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        var (io, head) = Create(bus, new() { StatusPollMilliseconds = 10 });
        await head.SelectPresetAsync(1);
        var runAt = 0L;
        void RecordRun(AdcStatusSample sample)
        {
            if (sample.Status is { Running: true })
                Interlocked.CompareExchange(ref runAt, Stopwatch.GetTimestamp(), 0);
        }
        head.Monitor.Sampled += RecordRun;
        try
        {
            var fed = false;
            var result = await head.TightenAsync(feedAsync: token =>
            {
                Assert.NotEqual(0, runAt);
                Assert.True(Stopwatch.GetElapsedTime(runAt).TotalMilliseconds >= delayMilliseconds - 1);
                Assert.True(head.Monitor.Sample?.Status?.Running);
                Assert.True(io.GetOutput(OutputIo.PickupBoltStart));
                fed = true;
                bus.SuppressCompletion = false;
                return Task.CompletedTask;
            }, dryRunMilliseconds: dryRunMilliseconds, feedDelayMilliseconds: delayMilliseconds);
            Assert.True(fed);
            Assert.True(result.Success);
            Assert.Equal(dryRunMilliseconds == 0 ? 1 : 0, bus.ResultReads);
            Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        }
        finally
        {
            head.Monitor.Sampled -= RecordRun;
        }
    }

    public enum BeforeFeedFailure { Cancel, Timeout, IoFault, Communication, Alarm, RunOff }

    [Theory]
    [InlineData(BeforeFeedFailure.Cancel)]
    [InlineData(BeforeFeedFailure.Timeout)]
    [InlineData(BeforeFeedFailure.IoFault)]
    [InlineData(BeforeFeedFailure.Communication)]
    [InlineData(BeforeFeedFailure.Alarm)]
    [InlineData(BeforeFeedFailure.RunOff)]
    public async Task HeadFeedDelayCannotLowerAfterInterruption(BeforeFeedFailure scenario)
    {
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        var (io, head) = Create(bus, new()
        {
            StatusPollMilliseconds = 10,
            FasteningTimeoutMilliseconds = scenario == BeforeFeedFailure.Timeout ? 200 : 3000,
        });
        await head.SelectPresetAsync(1);
        using var stop = new CancellationTokenSource();
        var fed = false;
        BoltResult? received = null;
        var cycle = head.TightenAsync(stop.Token, token =>
        {
            fed = true;
            return Task.CompletedTask;
        }, resultReceived: result => received = result, feedDelayMilliseconds: 5000);
        try
        {
            Assert.True(await VirtualTestSupport.WaitUntilAsync(
                () => head.Monitor.Sample?.Status?.Running == true, TimeSpan.FromSeconds(2)));
            await Task.Delay(30); // Remain inside the configured 5-second feed delay.
            Assert.False(fed);
            switch (scenario)
            {
                case BeforeFeedFailure.Cancel:
                    stop.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cycle.WaitAsync(TimeSpan.FromSeconds(2)));
                    break;
                case BeforeFeedFailure.Timeout:
                    await Assert.ThrowsAsync<TimeoutException>(() => cycle.WaitAsync(TimeSpan.FromSeconds(2)));
                    break;
                case BeforeFeedFailure.IoFault:
                    io.IsReady = false;
                    await Assert.ThrowsAsync<InvalidOperationException>(() => cycle.WaitAsync(TimeSpan.FromSeconds(2)));
                    break;
                case BeforeFeedFailure.Communication:
                    bus.StatusReadFailure = new IOException("ADC disconnected during feed delay");
                    Assert.Same(bus.StatusReadFailure,
                        await Assert.ThrowsAsync<IOException>(() => cycle.WaitAsync(TimeSpan.FromSeconds(2))));
                    break;
                case BeforeFeedFailure.Alarm:
                    bus.CurrentAlarm = 125;
                    Assert.False((await cycle.WaitAsync(TimeSpan.FromSeconds(2))).Success);
                    break;
                case BeforeFeedFailure.RunOff:
                    bus.ResultStatus = AdcEventStatus.FasteningNg;
                    bus.SuppressCompletion = false;
                    var completed = await cycle.WaitAsync(TimeSpan.FromSeconds(2));
                    Assert.Same(received, completed);
                    Assert.False(received!.Success);
                    Assert.NotNull(received.Controller);
                    Assert.Equal((ushort)AdcEventStatus.FasteningNg, received.Controller.StatusCode);
                    Assert.Null(received.Error);
                    break;
            }
            Assert.False(fed);
            Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
            Assert.Equal(scenario == BeforeFeedFailure.RunOff ? 1 : 0, bus.ResultReads);
        }
        finally
        {
            stop.Cancel();
            await ((Task)cycle).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Fact]
    public async Task HeadFeedDelayWaitsForFeedbackAfterRejection()
    {
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        var (io, head) = Create(bus, new() { StatusPollMilliseconds = 10 });
        await head.SelectPresetAsync(1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var fed = false;
        var cycle = head.TightenAsync(stop.Token, token =>
        {
            Assert.True(head.Monitor.Sample?.Status?.Running);
            fed = true;
            bus.SuppressCompletion = false;
            return Task.CompletedTask;
        }, feedDelayMilliseconds: 200);
        try
        {
            Assert.True(await VirtualTestSupport.WaitUntilAsync(
                () => head.Monitor.Sample?.Status?.Running == true, TimeSpan.FromSeconds(2)));
            bus.StatusRejection = "0x03: no feedback during feed delay";
            await Task.Delay(300);
            Assert.False(fed);
            Assert.True(io.GetOutput(OutputIo.PickupBoltStart));
            bus.StatusRejection = null;
            Assert.True((await cycle).Success);
            Assert.True(fed);
        }
        finally
        {
            stop.Cancel();
            await ((Task)cycle).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Fact]
    public async Task InvalidHeadFeedDelayCannotStartMotor()
    {
        using var bus = new AdcControllerStub();
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => head.TightenAsync(feedDelayMilliseconds: -1));
        Assert.Equal(0, bus.StartWrites);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Theory]
    [InlineData(BeforeFeedFailure.Cancel)]
    [InlineData(BeforeFeedFailure.Timeout)]
    [InlineData(BeforeFeedFailure.IoFault)]
    [InlineData(BeforeFeedFailure.Communication)]
    [InlineData(BeforeFeedFailure.Alarm)]
    public async Task FailureBeforeRunCannotLowerHead(BeforeFeedFailure scenario)
    {
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        var (io, head) = Create(bus, new() { StatusPollMilliseconds = 10, FasteningTimeoutMilliseconds = 300 });
        await head.SelectPresetAsync(1);
        bus.StatusRejection = "Waiting for RUN";
        using var stop = new CancellationTokenSource();
        var fed = false;
        var reads = bus.StatusReads;
        var cycle = head.TightenAsync(stop.Token, token =>
        {
            fed = true;
            return Task.CompletedTask;
        }, dryRunMilliseconds: scenario == BeforeFeedFailure.Timeout ? 10 : 0);
        try
        {
            Assert.True(await VirtualTestSupport.WaitUntilAsync(() => bus.StatusReads >= reads + 2, TimeSpan.FromSeconds(2)));
            Assert.False(fed);
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
                    bus.StatusReadFailure = new IOException("ADC disconnected before RUN");
                    bus.StatusRejection = null;
                    Assert.Same(bus.StatusReadFailure, await Assert.ThrowsAsync<IOException>(() => cycle));
                    break;
                case BeforeFeedFailure.Alarm:
                    bus.CurrentAlarm = 125;
                    bus.StatusRejection = null;
                    Assert.False((await cycle).Success);
                    break;
            }
            Assert.False(fed);
            Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
            Assert.Equal(0, bus.ResultReads);
        }
        finally
        {
            stop.Cancel();
            await ((Task)cycle).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Fact]
    public async Task ControllerErrorBeforeRunKeepsItsResultWithoutLoweringHead()
    {
        using var bus = new AdcControllerStub { ResultStatus = AdcEventStatus.Error, ResultError = 125 };
        var (io, head) = Create(bus, new() { StatusPollMilliseconds = 10 });
        await head.SelectPresetAsync(1);
        bus.RunReplies.Enqueue(false);
        void AlarmOnStart(OutputIo output, bool on)
        {
            if (output == OutputIo.PickupBoltStart && on)
                bus.CurrentAlarm = 125;
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
            Assert.Equal(1, bus.ResultReads);
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
        var (io, head) = Create(bus, new() { StatusPollMilliseconds = 10 });
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
    public async Task SharedMonitorRunsWhileIdleAndDisconnectInvalidatesItsSample()
    {
        using var bus = new AdcControllerStub { StatusReadDelayMilliseconds = 15 };
        var (io, head) = Create(bus, new() { StatusPollMilliseconds = 20 });
        var notifiedSamples = new System.Collections.Concurrent.ConcurrentQueue<AdcStatusSample?>();
        bus.Monitor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AdcStatusMonitor.Sample))
                notifiedSamples.Enqueue(bus.Monitor.Sample);
        };
        await head.CheckReadyAsync();
        Assert.Null(head.Monitor.Sample?.Error);
        await Task.WhenAll(bus.Monitor.StartAsync(1, CancellationToken.None),
            bus.Monitor.StartAsync(1, CancellationToken.None));
        var reads = bus.StatusReads;
        Assert.True(await VirtualTestSupport.WaitUntilAsync(() => bus.StatusReads >= reads + 3, TimeSpan.FromSeconds(2)));
        Assert.False(bus.ConcurrentStatusReadsDetected);
        Assert.Equal(0, bus.ResultReads);
        Assert.Equal(0, bus.StartWrites);
        Assert.True(head.Monitor.Sample?.Status!.Ready);
        bus.StatusReadFailure = new IOException("Status disconnected");
        Assert.True(await VirtualTestSupport.WaitUntilAsync(() => head.Monitor.Sample?.Error is not null, TimeSpan.FromSeconds(2)));
        Assert.Null(head.Monitor.Sample?.Status);
        bus.StatusReadFailure = null;
        Assert.True(await VirtualTestSupport.WaitUntilAsync(() => head.Monitor.Sample?.Status is not null, TimeSpan.FromSeconds(2)));
        using var disconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var pending = head.Monitor.WaitForSampleAsync(Stopwatch.GetTimestamp(), disconnectTimeout.Token);
        bus.Close();
        var disconnected = await Assert.ThrowsAsync<IOException>(() => pending);
        Assert.Contains("Controller test bus/1", disconnected.Message);
        Assert.Null(head.Monitor.Sample?.Status);
        reads = bus.StatusReads;
        await Task.Delay(80);
        Assert.Equal(reads, bus.StatusReads);
        await head.CheckReadyAsync();
        Assert.True(head.Monitor.Sample?.Status!.Ready);
        Assert.Null(head.Monitor.Sample?.Error);
        Assert.False(bus.ConcurrentStatusReadsDetected);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.Contains(notifiedSamples, sample => sample is { Status.Ready: true, Error: null });
        Assert.Contains(notifiedSamples, sample => sample is { Status: null, Error: IOException });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadinessWaitsForValidFeedbackAfterARejectedStatusSample(bool reset)
    {
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var bus = new AdcControllerStub
        {
            StatusRejection = "RX=0184030301; status request rejected",
            StatusReadBarrier = release.Task,
        };
        var (io, head) = Create(bus, new() { ResponseTimeoutMilliseconds = 1_000, StatusPollMilliseconds = 10 });
        bus.Monitor.Sampled += sample =>
        {
            if (sample.Rejection is not null)
            {
                bus.StatusRejection = null;
                rejected.TrySetResult();
            }
        };
        var checking = reset ? head.ResetAsync() : head.SelectPresetAsync(1);
        try
        {
            await rejected.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(await VirtualTestSupport.WaitUntilAsync(() => bus.StatusReads >= 2, TimeSpan.FromSeconds(2)));
            Assert.Null(head.Monitor.Sample?.Status);
            Assert.False(checking.IsCompleted);
            Assert.Equal(0, bus.StartWrites);
            release.TrySetResult();
            await checking.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(head.Monitor.Sample?.Status?.Ready);
            Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        }
        finally
        {
            release.TrySetResult();
            await checking.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Fact]
    public async Task ReadinessCannotAcceptRepeatedStatusRejectionsAsReady()
    {
        using var bus = new AdcControllerStub { StatusRejection = "RX=0184030301; status request rejected" };
        var (io, head) = Create(bus, new() { ResponseTimeoutMilliseconds = 100, StatusPollMilliseconds = 10 });

        var error = await Assert.ThrowsAsync<TimeoutException>(() => head.CheckReadyAsync());

        Assert.Contains(bus.StatusRejection, error.Message);
        Assert.True(bus.StatusReads > 1);
        Assert.Null(head.Monitor.Sample?.Status);
        Assert.Equal(0, bus.StartWrites);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Fact]
    public async Task ReadinessUsesTheConfiguredReadAttemptBudget()
    {
        using var bus = new AdcControllerStub { StatusReadDelayMilliseconds = 140 };
        var (io, head) = Create(bus, new()
        {
            ResponseTimeoutMilliseconds = 60,
            ReadAttempts = 4,
            StatusPollMilliseconds = 10,
        });

        await head.CheckReadyAsync();

        Assert.True(head.Monitor.Sample?.Status?.Ready);
        Assert.Equal(1, bus.StatusReads);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Fact]
    public async Task RetryingReadClearsCachedFeedbackUntilTheNextStatusReply()
    {
        using var bus = new AdcControllerStub();
        var (_, head) = Create(bus);
        await head.CheckReadyAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = bus.Monitor.EnqueueAsync(async token =>
        {
            bus.Monitor.InvalidateSample("Response timed out; retrying.");
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return 0;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(head.Monitor.Sample?.Status);
        Assert.Null(head.Monitor.Sample?.Error);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var feedback = head.Monitor.WaitForSampleAsync(Stopwatch.GetTimestamp(), timeout.Token);
        Assert.False(feedback.IsCompleted);

        release.SetResult();
        await request;

        Assert.True((await feedback).Ready);
        Assert.Null(head.Monitor.Sample?.Rejection);
    }

    [Fact]
    public async Task CancelledStatusWaitDoesNotReturnAnAlreadyAvailableSample()
    {
        using var bus = new AdcControllerStub();
        var (_, head) = Create(bus);
        await head.CheckReadyAsync();
        Assert.NotNull(bus.Monitor.Sample?.Status);
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => bus.Monitor.WaitForSampleAsync(0, stop.Token));
    }

    [Fact]
    public async Task IdleMonitorSamplesAndAcceptsQueuedWorkWithoutCancellationExceptions()
    {
        using var bus = new AdcControllerStub();
        bus.Open("Virtual", 115200);
        bus.Monitor.IntervalMilliseconds = 20;
        var monitoring = new AsyncLocal<bool> { Value = true };
        var cancellations = 0;
        void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs args)
        {
            if (monitoring.Value && args.Exception is OperationCanceledException)
                Interlocked.Increment(ref cancellations);
        }
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        try
        {
            await bus.Monitor.StartAsync(1, CancellationToken.None);
            Assert.True(await VirtualTestSupport.WaitUntilAsync(() => bus.StatusReads >= 4, TimeSpan.FromSeconds(2)));
            bus.Monitor.IntervalMilliseconds = 10_000;
            var queued = bus.Monitor.EnqueueAsync(token => Task.FromResult(42));
            Assert.Equal(42, await queued.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(0, Volatile.Read(ref cancellations));
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
            monitoring.Value = false;
        }
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
        Assert.True(await VirtualTestSupport.WaitUntilAsync(() => bus.StatusReads == 1, TimeSpan.FromSeconds(2)));
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
            Assert.True(await VirtualTestSupport.WaitUntilAsync(() => bus.StatusReads > statusReads, TimeSpan.FromSeconds(2)));
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
        Assert.True(await VirtualTestSupport.WaitUntilAsync(() => bus.StatusReads == 1, TimeSpan.FromSeconds(2)));
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
        Assert.True(await VirtualTestSupport.WaitUntilAsync(() => bus.StatusReads == 1, TimeSpan.FromSeconds(2)));
        var first = bus.Monitor.EnqueueAsync(token => bus.ReadFasteningResultAsync(1, token));
        var second = bus.Monitor.EnqueueAsync(token => bus.ReadFasteningResultAsync(1, token));
        bus.Close();
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
    public async Task MonitorReadFailureDuringFasteningStopsInsteadOfTreatingUnknownAsRunOff()
    {
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        var (io, head) = Create(bus, new() { StatusPollMilliseconds = 10 });
        await head.SelectPresetAsync(1);
        var cycle = head.TightenAsync();
        Assert.True(await VirtualTestSupport.WaitUntilAsync(() => head.Monitor.Sample?.Status?.Running == true, TimeSpan.FromSeconds(2)));
        bus.StatusReadFailure = new IOException("Status lost");
        Assert.Same(bus.StatusReadFailure, await Assert.ThrowsAsync<IOException>(() => cycle));
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.Equal(0, bus.ResultReads);
        Assert.Null(head.Monitor.Sample?.Status);
    }

    [Theory]
    [InlineData("0184030301", AdcEventStatus.FasteningOk, 5)]
    [InlineData("018C0304C1", AdcEventStatus.FasteningOk, 1)]
    [InlineData("018C0304C1", AdcEventStatus.FasteningNg, 1)]
    public async Task RejectedStatusSamplesWaitForActualRunOffAndReadTheResult(
        string response, AdcEventStatus resultStatus, int rejectionCount)
    {
        using var bus = new AdcControllerStub { SuppressCompletion = true, ResultStatus = resultStatus };
        var log = new ApplicationLog();
        using var factory = log.CreateLoggerFactory();
        var io = new VirtualIoService(VirtualTestSupport.Outputs(), new());
        bus.BindIo(io, FasteningHead.Pickup);
        var head = new AdcBoltHead(bus, io, FasteningHead.Pickup,
            new() { StatusPollMilliseconds = 10 }, 1, "Virtual", 115200, factory.CreateLogger<AdcBoltHead>());
        bool? startDuringSummary = null;
        ((INotifyCollectionChanged)log.Entries).CollectionChanged += (sender, args) =>
        {
            if (log.Entries[^1].Message.Contains("completion status timing"))
                startDuringSummary = io.GetOutput(OutputIo.PickupBoltStart);
        };
        await head.SelectPresetAsync(1);
        var rejection = $"HComm error 0x03; RX={response}.";
        Assert.NotNull(rejection);
        var rejected = 0;
        var unknownDuringRejection = true;
        var startHeldDuringRejection = true;
        void RejectStatusSamples(AdcStatusSample sample)
        {
            if (sample.Rejection == rejection)
            {
                rejected++;
                unknownDuringRejection &= head.Monitor.Sample is { Status: null, Error: null };
                startHeldDuringRejection &= io.GetOutput(OutputIo.PickupBoltStart) && bus.ResultReads == 0;
                if (rejected == rejectionCount)
                {
                    bus.StatusRejection = null;
                    bus.SuppressCompletion = false;
                }
            }
            else if (rejected == 0 && sample.Status is { Running: true })
                bus.StatusRejection = rejection;
        }
        head.Monitor.Sampled += RejectStatusSamples;
        try
        {
            var result = await head.TightenAsync();
            Assert.Equal(rejectionCount, rejected);
            Assert.True(unknownDuringRejection);
            Assert.True(startHeldDuringRejection);
            Assert.Equal(resultStatus == AdcEventStatus.FasteningOk, result.Success);
            Assert.Null(result.Error);
            Assert.NotNull(result.Controller);
            Assert.Equal((ushort)resultStatus, result.Controller.StatusCode);
            Assert.Equal(1, bus.ResultReads);
            Assert.False(bus.ResultReadWhileRunning);
            Assert.Equal(1, bus.StartWrites);
            Assert.Equal(1, bus.StopWrites);
            Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
            Assert.False(startDuringSummary);
            Assert.Contains(log.Snapshot(), entry => entry.Message.Contains("completion status timing")
                && entry.Message.Contains($"rejected={rejectionCount}"));
            Assert.Contains(log.Snapshot(), entry => entry.Message.Contains("cycle timing")
                && entry.Message.Contains("controller fastening=250 ms"));
        }
        finally
        {
            head.Monitor.Sampled -= RejectStatusSamples;
        }
    }

    [Theory]
    [InlineData("0184030301")]
    [InlineData("018C0304C1")]
    public async Task RepeatedStatusRejectionsCannotCompleteFasteningAndStillTimeOut(string response)
    {
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        var (io, head) = Create(bus, new()
        {
            StatusPollMilliseconds = 10,
            FasteningTimeoutMilliseconds = 150,
        });
        await head.SelectPresetAsync(1);
        var rejection = $"HComm error 0x03; RX={response}.";
        Assert.NotNull(rejection);
        void RejectStatusWhileStarted(OutputIo output, bool on)
        {
            if (output == OutputIo.PickupBoltStart)
                bus.StatusRejection = on ? rejection : null;
        }
        io.OutputChanged += RejectStatusWhileStarted;
        try
        {
            var result = await head.TightenAsync();
            Assert.False(result.Success);
            Assert.Contains("timed out", result.Error);
            Assert.Contains(response, result.Error);
            Assert.Null(result.Controller);
            Assert.Null(result.Torque);
            Assert.Equal(0, bus.ResultReads);
            Assert.Equal(1, bus.StartWrites);
            Assert.Equal(1, bus.StopWrites);
            Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        }
        finally
        {
            io.OutputChanged -= RejectStatusWhileStarted;
        }
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
        Assert.True(await VirtualTestSupport.WaitUntilAsync(() => bus.StartWrites == 1, TimeSpan.FromSeconds(2)));
        Assert.False(cycle.IsCompleted);
        Assert.False(head.Monitor.Sample?.Status!.Running); // Initial OFF cannot finish a new cycle.
        Assert.Equal(1, bus.EventReads);
        Assert.Equal(0, bus.ResultReads);
        Assert.True((await cycle).Success);
        Assert.True(Environment.TickCount64 - startedAt >= 140);
        Assert.Equal(1, bus.ResultReads);
        Assert.Equal(1, bus.EventReads);
        Assert.True(bus.StatusReads >= 6);
        Assert.False(bus.ResultReadWhileRunning);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedResultReadIsRecordedWithoutRestartingMotor(bool rejected)
    {
        using var bus = new AdcControllerStub { NextResultReadFailure = ResultReplyFailure(rejected) };
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
        Assert.True(await VirtualTestSupport.WaitUntilAsync(() => bus.StartWrites == 1, TimeSpan.FromSeconds(2)));
        bus.StatusRejection = "RX=0184030301; status request rejected";
        Assert.True(await VirtualTestSupport.WaitUntilAsync(
            () => head.Monitor.Sample?.Rejection is not null, TimeSpan.FromSeconds(2)));
        Assert.True(io.GetOutput(OutputIo.PickupBoltStart));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(0, bus.ResultReads);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        bus.StatusRejection = null;
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
            return new AdcResponseException(3, "HComm error 0x03; RX=0184030301.");
        return new AdcUnexpectedResponseException("HComm reply mismatch for fastening result.");
    }

    [Fact]
    public async Task OtherResultQueryRejectionsStillEndTheCycleAfterStop()
    {
        using var bus = new AdcControllerStub
        {
            NextResultReadFailure = new AdcResponseException(2, "HComm error 0x02."),
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
    public async Task StartOffDoesNotWaitForAnotherStatusRead(bool dryRun)
    {
        using var bus = new AdcControllerStub { StopPollsRemaining = -1 };
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
            Assert.Equal(dryRun ? 0 : 1, bus.ResultReads);
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
    public async Task StatusRejectionAfterStopCannotEraseTheMeasuredResult()
    {
        using var bus = new AdcControllerStub();
        var (io, head) = Create(bus, new() { StatusPollMilliseconds = 10, ResponseTimeoutMilliseconds = 50 });
        await head.SelectPresetAsync(1);
        var rejection = "Status read rejected after START OFF";
        void RejectAfterStop(OutputIo output, bool on)
        {
            if (output == OutputIo.PickupBoltStart && !on)
                bus.StatusRejection = rejection;
        }
        io.OutputChanged += RejectAfterStop;
        try
        {
            var result = await head.TightenAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(result.Success, result.Error);
            Assert.NotNull(result.Torque);
            Assert.NotNull(result.Controller);
            Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
            var error = await Assert.ThrowsAsync<TimeoutException>(() => head.SelectPresetAsync(1));
            Assert.Contains(rejection, error.Message);
            Assert.Equal(1, bus.ResultReads);
            Assert.Equal(1, bus.StartWrites);
        }
        finally
        {
            io.OutputChanged -= RejectAfterStop;
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
        Exception failure = timedOut
            ? new TimeoutException("Result query timed out after all attempts.")
            : new IOException("Serial disconnected");
        using var bus = new AdcControllerStub { NextResultReadFailure = failure };
        var (io, head) = Create(bus);
        await head.SelectPresetAsync(1);
        Assert.Same(failure, await Assert.ThrowsAsync(failure.GetType(), () => head.TightenAsync()));
        Assert.Equal(1, bus.StartWrites);
        Assert.Equal(1, bus.ResultReads);
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
        var (io, head) = Create(bus, new() { StatusPollMilliseconds = 10, FasteningTimeoutMilliseconds = 200 });
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
        Assert.True(bus.StatusReads >= 5); // Shared monitoring includes RUN transitions.
        bus.ResultStatus = AdcEventStatus.FasteningOk;
        bus.ResultError = 0;
        Assert.True((await head.TightenAsync()).Success);
    }

    [Fact]
    public async Task ControllerAlarmStopsWithoutWaitingForTheFasteningTimeout()
    {
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        var (io, head) = Create(bus, new() { FasteningTimeoutMilliseconds = 5000, StatusPollMilliseconds = 10 });
        await head.SelectPresetAsync(1);
        var cycle = head.TightenAsync();
        bus.StatusRejection = "RX=0184030301; status request rejected";
        Assert.True(await VirtualTestSupport.WaitUntilAsync(
            () => head.Monitor.Sample?.Rejection is not null, TimeSpan.FromSeconds(2)));
        Assert.True(io.GetOutput(OutputIo.PickupBoltStart));
        bus.CurrentAlarm = 125;
        bus.StatusRejection = null;
        var result = await cycle.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(result.Success);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.Contains("125", result.Error);
        Assert.Equal((ushort)125, head.Monitor.Sample?.Status!.Alarm);
        Assert.Equal(0, bus.ResultReads);
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
        var (io, head) = Create(bus, new() { ResponseTimeoutMilliseconds = 50, StatusPollMilliseconds = 10 });
        await head.SelectPresetAsync(1);
        io.SetOutput(OutputIo.PickupBoltPreset2, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync());
        Assert.Equal(0, bus.StartWrites);
        bus.NotReady = true;
        var error = await Assert.ThrowsAsync<TimeoutException>(() => head.SelectPresetAsync(1));
        Assert.Contains("READY timeout", error.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync());
        Assert.Equal(0, bus.StartWrites);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NextPresetWaitsForReadyAfterSuccessfulTighteningWithoutStartingOrResetting(bool cancel)
    {
        using var bus = new AdcControllerStub();
        var (io, head) = Create(bus, new() { StatusPollMilliseconds = 10, ResponseTimeoutMilliseconds = 500 });
        await head.SelectPresetAsync(2);
        var preliminary = await head.TightenAsync();
        Assert.True(preliminary.Success);
        bus.NotReady = true;
        var notReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selectingAt = Stopwatch.GetTimestamp();
        head.Monitor.Sampled += sample =>
        {
            if (sample.Status is { Ready: false, Running: false, Alarm: 0 }
                && Stopwatch.GetElapsedTime(selectingAt, sample.StartedAt) >= TimeSpan.FromMilliseconds(200))
                notReady.TrySetResult();
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var selecting = head.SelectPresetAsync(1, stop.Token);
        try
        {
            await notReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(selecting.IsCompleted);
            Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
            Assert.Equal(1, bus.StartWrites);
            Assert.Equal(1, bus.ResultReads);
            Assert.Equal(0, bus.ResetWrites);
            if (cancel)
            {
                stop.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selecting);
                await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync());
                Assert.Equal(1, bus.StartWrites);
            }
            else
            {
                bus.NotReady = false;
                await selecting;
                Assert.True(head.Monitor.Sample?.Status?.Ready);
                Assert.Equal(1, bus.StartWrites);
                var final = await head.TightenAsync();
                Assert.True(final.Success);
                Assert.Equal((ushort)1, final.Controller!.Preset);
                Assert.Equal(2, bus.StartWrites);
                Assert.Equal(0, bus.ResetWrites);
            }
        }
        finally
        {
            stop.Cancel();
            await selecting.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task ReadyFromPreviousPresetCannotReleaseNextFastening(bool cancel, bool timeout, bool resetAlarm)
    {
        using var bus = new AdcControllerStub();
        var (io, head) = Create(bus, new() { StatusPollMilliseconds = 10, ResponseTimeoutMilliseconds = 100 });
        await head.SelectPresetAsync(2);
        var preliminary = await head.TightenAsync();
        Assert.True(preliminary.Success);
        bus.ReportedPreset = 2;
        bus.CurrentAlarm = resetAlarm ? (ushort)42 : (ushort)0;
        var previousPreset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selectingAt = Stopwatch.GetTimestamp();
        head.Monitor.Sampled += sample =>
        {
            if (sample.Status is { Preset: 2, Ready: true, Running: false, Alarm: 0 }
                && Stopwatch.GetElapsedTime(selectingAt, sample.StartedAt) >= TimeSpan.FromMilliseconds(200))
                previousPreset.TrySetResult();
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var selecting = head.SelectPresetAsync(1, stop.Token);
        try
        {
            await previousPreset.Task.WaitAsync(TimeSpan.FromSeconds(2));
            // Let selection consume the sample; READY alone must not finish it.
            await Task.Delay(30);
            Assert.False(selecting.IsCompleted);
            Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
            Assert.Equal(1, bus.StartWrites);
            Assert.Equal(1, bus.ResultReads);
            Assert.Equal(resetAlarm ? 1 : 0, bus.ResetWrites);
            if (cancel)
            {
                stop.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selecting);
            }
            else if (timeout)
            {
                var error = await Assert.ThrowsAsync<TimeoutException>(() => selecting);
                Assert.Contains("requested preset=1", error.Message);
                Assert.Contains("actual preset=2", error.Message);
            }
            else
            {
                bus.ReportedPreset = null;
                await selecting;
                Assert.Equal((ushort)1, head.Monitor.Sample!.Status!.Preset);
                var final = await head.TightenAsync();
                Assert.True(final.Success);
                Assert.Equal((ushort)1, final.Controller!.Preset);
                Assert.Equal(2, bus.StartWrites);
                Assert.Equal(resetAlarm ? 1 : 0, bus.ResetWrites);
                return;
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync());
            Assert.Equal(1, bus.StartWrites);
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task PresetSettlingKeepsStartOffAndChecksFeedbackAfterTheDelay(bool cancel)
    {
        using var bus = new AdcControllerStub();
        var settings = new HantasSettings
        {
            StatusPollMilliseconds = 10, ResponseTimeoutMilliseconds = 500, PresetSettleMilliseconds = 0,
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
                Assert.False(selecting.IsCompleted);
                Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
                Assert.Equal(1, bus.StartWrites);
                bus.NotReady = false;
                bus.ReportedPreset = null;
                await selecting;
                Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(settings.PresetSettleMilliseconds));
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
            StatusPollMilliseconds = 10, PresetSettleMilliseconds = 5000,
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
        var checkingAt = Stopwatch.GetTimestamp();
        await head.SelectPresetAsync(2, stop.Token);
        Assert.True(head.Monitor.Sample!.StartedAt >= checkingAt);
        Assert.Empty(changes);
        Assert.True((await head.TightenAsync(stop.Token)).Success);

        checkingAt = Stopwatch.GetTimestamp();
        await head.SelectPresetAsync(2, stop.Token);
        Assert.True(head.Monitor.Sample!.StartedAt >= checkingAt);
        Assert.Empty(changes);
        Assert.True((await head.TightenAsync(stop.Token)).Success);
        Assert.Equal(2, bus.StartWrites);
        Assert.Equal(2, bus.ResultReads);
        Assert.False(bus.Running);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SamePresetStillWaitsForLivePresetAndReady(bool cancel)
    {
        using var bus = new AdcControllerStub();
        var (io, head) = Create(bus, new()
        {
            StatusPollMilliseconds = 10, ResponseTimeoutMilliseconds = 500, PresetSettleMilliseconds = 0,
        });
        await head.SelectPresetAsync(2);
        Assert.True((await head.TightenAsync()).Success);
        bus.NotReady = true;
        bus.ReportedPreset = 1;
        var notReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wrongPresetReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selectingAt = Stopwatch.GetTimestamp();
        head.Monitor.Sampled += sample =>
        {
            if (sample.StartedAt < selectingAt || sample.Status is not { Preset: 1 } status)
                return;
            if (status.Ready)
                wrongPresetReady.TrySetResult();
            else
                notReady.TrySetResult();
        };
        var presetChanges = new List<OutputIo>();
        io.OutputChanged += (output, on) =>
        {
            if (output is OutputIo.PickupBoltPreset1 or OutputIo.PickupBoltPreset2 or OutputIo.PickupBoltPreset3)
                presetChanges.Add(output);
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var selecting = head.SelectPresetAsync(2, stop.Token);
        try
        {
            await notReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(30);
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
                bus.NotReady = false;
                await wrongPresetReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await Task.Delay(30);
                Assert.False(selecting.IsCompleted);
                Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
                bus.ReportedPreset = null;
                await selecting;
                Assert.True((await head.TightenAsync()).Success);
                Assert.Equal(2, bus.StartWrites);
            }
            Assert.Empty(presetChanges);
            Assert.Equal(0, bus.ResetWrites);
        }
        finally
        {
            stop.Cancel();
            await selecting.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Fact]
    public async Task ChangedPresetOutputsRequireSelectionAndSettlingAgain()
    {
        using var bus = new AdcControllerStub();
        var settings = new HantasSettings { StatusPollMilliseconds = 10, PresetSettleMilliseconds = 0 };
        var (io, head) = Create(bus, settings);
        await head.SelectPresetAsync(2);
        // Checking only the requested output or cached preset would miss this invalid pair.
        io.SetOutput(OutputIo.PickupBoltPreset3, true);
        settings.PresetSettleMilliseconds = 200;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var started = Stopwatch.StartNew();
        await head.SelectPresetAsync(2, stop.Token);
        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(settings.PresetSettleMilliseconds));
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
        var io = new VirtualIoService(VirtualTestSupport.Outputs(), new());
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
