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
    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public async Task FasteningAndDryRunWaitForMotorStopBeforeReturning(int dryRunMilliseconds)
    {
        var bus = new AdcControllerStub
        {
            ResultReceiveFailure = dryRunMilliseconds > 0
                ? new IOException("No automatic result output in dry run.") : null,
            StopPollsRemaining = 2,
        };
        var settings = new HantasSettings { FasteningTimeoutMilliseconds = dryRunMilliseconds > 0 ? 1 : 1_000 };
        var head = new AdcBoltHead(bus, settings, 1, "Virtual", 115200);
        var fed = false;
        Task FeedAsync(CancellationToken token)
        {
            Assert.True(bus.Running);
            fed = true;
            return Task.CompletedTask;
        }

        var cycle = head.TightenAsync(feedAsync: FeedAsync, dryRunMilliseconds: dryRunMilliseconds);
        Assert.True(fed);
        Assert.True(bus.Running);
        Assert.False(cycle.IsCompleted);
        var result = await cycle.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(dryRunMilliseconds > 0 ? BoltResultSource.DryRun : BoltResultSource.Controller, result.Source);
        Assert.Equal(dryRunMilliseconds > 0 ? null : (double?)1, result.Torque);
        Assert.Equal(dryRunMilliseconds > 0 ? 0 : 1, bus.ResultReceives);
        Assert.Equal(1, bus.StartWrites);
        Assert.Equal(1, bus.StopWrites);
        Assert.Equal(3, bus.StopFeedbackReads);
        Assert.False(bus.Running);
    }

    [Fact]
    public async Task DryRunCancellationStopsMotorWithoutReturningAResult()
    {
        var bus = new AdcControllerStub();
        var head = new AdcBoltHead(bus, new HantasSettings(), 1, "Virtual", 115200);
        using var stop = new CancellationTokenSource();
        var cycle = head.TightenAsync(stop.Token, dryRunMilliseconds: 10_000);
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cycle);
        Assert.Equal(0, bus.ResultReceives);
        Assert.Equal(1, bus.StopWrites);
        Assert.False(bus.Running);
    }
    [Fact]
    public async Task TighteningReceivesAutomaticResultWithoutPolling()
    {
        var bus = new VirtualAdcBus();
        var head = new AdcBoltHead(bus, new HantasSettings(), 1, "Virtual", 115200);
        var started = false;
        var resultReads = 0;
        var receivedAfterStart = false;
        bus.FrameTransferred += (direction, frame) =>
        {
            if (direction == AdcFrameDirection.Receive)
            {
                if (started && frame[1] == 4 && frame[2] == 28)
                    receivedAfterStart = true;
                return;
            }
            var address = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2));
            if (frame[1] == 6 && address == (ushort)AdcRemoteRegister.RemoteStart)
                started = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4)) != 0;
            if (frame[1] == 4 && address == (ushort)AdcResultRegister.EventCount)
            {
                Assert.False(started); // One pre-START baseline, no result queries during fastening.
                resultReads++;
            }
        };

        Assert.True((await head.TightenAsync()).Success);
        Assert.True(receivedAfterStart);
        Assert.Equal(1, resultReads);
    }

    [Fact]
    public async Task AutomaticOutputIgnoresOldAndNonCompletionEvents()
    {
        var bus = new AdcControllerStub();
        var head = new AdcBoltHead(bus, new HantasSettings(), 1, "Virtual", 115200);
        var result = new AdcFasteningResult(0, 250, 3, 1, 0.8, 1000, 0, 0, 0, 1, 0,
            AdcDirection.Fastening, AdcEventStatus.FasteningOk, 0);
        bus.AutomaticResults.Enqueue(result); // Same event as the pre-START baseline.
        bus.AutomaticResults.Enqueue(result with { EventCount = 1, Status = AdcEventStatus.DirectionChanged });
        bus.AutomaticResults.Enqueue(result with { EventCount = 2, Status = AdcEventStatus.PresetChanged });
        bus.AutomaticResults.Enqueue(result with { EventCount = 3, Status = AdcEventStatus.FasteningNg });

        var completed = await head.TightenAsync();

        Assert.False(completed.Success);
        Assert.Equal(0.8, completed.Torque);
        Assert.Equal(4, bus.ResultReceives);
        Assert.False(bus.Running);
        Assert.Equal(1, bus.StopWrites);
    }

    [Fact]
    public async Task FailedFeedRequiresANewConfirmedFeedBeforeRecordingResult()
    {
        var bus = new AdcControllerStub();
        var head = new AdcBoltHead(bus, new HantasSettings(), 1, "Virtual", 115200);
        var failure = new IoTimeoutException(InputIo.PickupHeadDown, true, 100);
        Task FeedAsync(CancellationToken token)
        {
            Assert.True(bus.Running);
            Assert.Equal(1, bus.StartWrites);
            throw failure;
        }

        Assert.Same(failure, await Assert.ThrowsAsync<IoTimeoutException>(
            () => head.TightenAsync(feedAsync: FeedAsync)));
        Assert.Equal(1, bus.StopWrites);
        Assert.False(bus.Running);
        // A controller result cannot prove that the cylinder fed the bolt.
        Assert.Equal(1, bus.StartWrites);
        var feeds = 0;
        Task ConfirmFeedAsync(CancellationToken token)
        {
            feeds++;
            return Task.CompletedTask;
        }
        Assert.True((await head.TightenAsync(feedAsync: ConfirmFeedAsync)).Success);
        Assert.Equal(2, bus.StartWrites);
        Assert.Equal(1, feeds);
    }

    [Fact]
    public async Task RejectedResultCannotReleaseTheBoltBeforeMotorStopIsConfirmed()
    {
        var bus = new AdcControllerStub
        {
            ResultReceiveFailure = new AdcResponseException(3, "Controller rejected result output."),
            StopPollsRemaining = -1,
        };
        var head = new AdcBoltHead(bus, new HantasSettings { ResponseTimeoutMilliseconds = 40 }, 1, "Virtual", 115200);
        var failure = await Assert.ThrowsAsync<AggregateException>(() => head.TightenAsync());
        Assert.IsType<AdcResponseException>(failure.InnerExceptions[0]);
        Assert.IsType<TimeoutException>(failure.InnerExceptions[1]);
        Assert.True(bus.Running);
        Assert.Equal(1, bus.StopWrites);
    }

    [Fact]
    public async Task ResultCommunicationFailureStillStopsWithoutInventingNg()
    {
        var bus = new AdcControllerStub { ResultReceiveFailure = new IOException("Serial connection lost.") };
        var head = new AdcBoltHead(bus, new HantasSettings(), 1, "Virtual", 115200);
        var failure = await Assert.ThrowsAsync<IOException>(() => head.TightenAsync());
        Assert.Equal("Serial connection lost.", failure.Message);
        Assert.False(bus.Running);
        Assert.Equal(1, bus.StopWrites);
    }


    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public async Task UnconfirmedStopRequiresPhysicalStopBeforeANewFastening(int dryRunMilliseconds)
    {
        var bus = new AdcControllerStub { StopPollsRemaining = -1 };
        var head = new AdcBoltHead(bus, new HantasSettings { ResponseTimeoutMilliseconds = 40 }, 1, "Virtual", 115200);
        var failure = await Assert.ThrowsAsync<TimeoutException>(
            () => head.TightenAsync(dryRunMilliseconds: dryRunMilliseconds));
        Assert.Contains("motor stop was not confirmed", failure.Message);
        Assert.True(bus.Running);
        Assert.Equal(dryRunMilliseconds > 0 ? 0 : 1, bus.ResultReceives);
        await Assert.ThrowsAsync<TimeoutException>(() => head.StopAsync());

        bus.StopPollsRemaining = 0;
        await head.StopAsync();
        async Task ConfirmRunningAsync(CancellationToken token)
        {
            // The preceding STOP must not make this new START read back as stopped.
            Assert.True((await ((IAdcBus)bus).ReadControllerStatusAsync(1, token)).Running);
        }
        Assert.True((await head.TightenAsync(feedAsync: ConfirmRunningAsync, dryRunMilliseconds: dryRunMilliseconds)).Success);
        Assert.Equal(2, bus.StartWrites);
        Assert.False(bus.Running);
    }

    [Fact]
    public async Task MissingStopFeedbackDoesNotBecomeStopped()
    {
        var bus = new AdcControllerStub { StopReadFailure = new IOException("RUN feedback unavailable.") };
        var head = new AdcBoltHead(bus, new HantasSettings(), 1, "Virtual", 115200);
        var failure = await Assert.ThrowsAsync<IOException>(() => head.TightenAsync());
        Assert.Same(bus.StopReadFailure, failure);
        Assert.Equal(1, bus.StopWrites);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public async Task RejectedStopDoesNotReturnACompletedResult(int dryRunMilliseconds)
    {
        // The equipment's 0x06 rejection, including the original CRC.
        using var response = new MemoryStream([0x01, 0x86, 0x03, 0x02, 0x61]);
        var rejection = await Assert.ThrowsAsync<AdcResponseException>(
            () => AdcBus.ReadResponseAsync(response, () => { }, bytes => { },
                1, AdcFunctionCode.WriteSingleRegister, CancellationToken.None));
        var bus = new AdcControllerStub { StopWriteFailure = rejection };
        var head = new AdcBoltHead(bus, new HantasSettings(), 1, "Virtual", 115200);

        Assert.Same(rejection, await Assert.ThrowsAsync<AdcResponseException>(
            () => head.TightenAsync(dryRunMilliseconds: dryRunMilliseconds)));
        Assert.True(bus.Running);
        Assert.Equal(1, bus.StopWrites);
        Assert.Equal(0, bus.StopFeedbackReads);
        Assert.True((await ((IAdcBus)bus).ReadControllerStatusAsync(1)).Running);
    }

    [Fact]
    public async Task InterruptedFasteningRestartsOnlyWithMatchingPreset()
    {
        IAdcBus bus = new VirtualAdcBus();
        var head = new AdcBoltHead(bus, new HantasSettings(), 1, "Virtual", 115200);
        await head.SelectPresetAsync(3);
        using var stop = new CancellationTokenSource();
        var tightening = head.TightenAsync(stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tightening);

        await bus.SelectPresetAsync(1, 7);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync());
        Assert.Contains("preset", failure.Message);
        Assert.Equal(0, (await bus.ReadFasteningResultAsync(1)).EventCount);
        Assert.False((await bus.ReadControllerStatusAsync(1)).Running);

        await bus.SelectPresetAsync(1, 3);
        Assert.True((await head.TightenAsync()).Success);
        Assert.Equal(1, (await bus.ReadFasteningResultAsync(1)).EventCount);
        Assert.False((await bus.ReadControllerStatusAsync(1)).Running);
    }

    [Theory]
    [InlineData(7, AdcDirection.Fastening)]
    [InlineData(3, AdcDirection.Loosening)]
    public async Task MismatchedResultIsNotRecorded(ushort preset, AdcDirection direction)
    {
        var bus = new AdcControllerStub { ResultPreset = preset, ResultDirection = direction };
        var head = new AdcBoltHead(bus, new HantasSettings(), 1, "Virtual", 115200);
        await head.SelectPresetAsync(3);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync());
        Assert.Contains("does not match this fastening", failure.Message);
        Assert.False(bus.Running);
        Assert.Equal(1, bus.StartWrites);
    }

    [Fact]
    public async Task PresetRequiresReadbackAndMustStillMatchAtStart()
    {
        var bus = new AdcControllerStub { CurrentPreset = 1, IgnorePresetWrites = true };
        var head = new AdcBoltHead(bus, new HantasSettings(), 1, "Virtual", 115200);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.SelectPresetAsync(3));
        Assert.Equal(0, bus.StartWrites);

        bus.IgnorePresetWrites = false;
        await head.SelectPresetAsync(3);
        bus.CurrentPreset = 7; // Controller-panel change after successful selection.
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync());
        Assert.Equal(0, bus.StartWrites);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectionMustBeConfirmedBeforeStarting(bool reverse)
    {
        var bus = new AdcControllerStub
        {
            CurrentDirection = reverse ? AdcDirection.Fastening : AdcDirection.Loosening,
            IgnoreDirectionWrites = true,
        };
        var head = new AdcBoltHead(bus, new HantasSettings(), 1, "Virtual", 115200);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reverse ? head.RunReverseAsync(CancellationToken.None) : head.TightenAsync());
        Assert.Equal(0, bus.StartWrites);
        Assert.Equal(1, bus.StopWrites);
    }

    [Fact]
    public async Task AdcConnectionEditsApplyWhenHeadsAreRecreated()
    {
        var settings = new HantasSettings { PickupPortName = "Virtual", PickupBaudRate = 19200 };
        var bus = new VirtualAdcBus();
        var pickup = new AdcBoltHead(bus, settings, settings.PickupSlaveAddress, settings.PickupPortName, settings.PickupBaudRate);
        var shooting = new AdcBoltHead(bus, settings, settings.ShootingSlaveAddress, settings.PickupPortName, settings.PickupBaudRate);
        byte expectedSlave = 0;
        var expectedBaudRate = 19200;
        bus.FrameTransferred += (direction, frame) =>
        {
            if (direction == AdcFrameDirection.Transmit)
            {
                Assert.Equal(expectedSlave, frame[0]);
                Assert.Equal(expectedBaudRate, bus.BaudRate);
            }
        };

        await pickup.CheckReadyAsync();
        settings.PickupPortName = "COM5";
        settings.PickupBaudRate = 115200;
        settings.PickupSlaveAddress = 2;
        settings.ShootingSlaveAddress = 3;

        foreach (var head in new[] { pickup, shooting })
        {
            await head.CheckReadyAsync();
            await head.ResetAsync();
            bus.Close();
            await head.ResetAsync();
            expectedSlave++;
        }

        bus.Close();
        expectedBaudRate = settings.PickupBaudRate;
        pickup = new AdcBoltHead(bus, settings, settings.PickupSlaveAddress, settings.PickupPortName, settings.PickupBaudRate);
        shooting = new AdcBoltHead(bus, settings, settings.ShootingSlaveAddress, settings.PickupPortName, settings.PickupBaudRate);
        foreach (var head in new[] { pickup, shooting })
        {
            await head.CheckReadyAsync();
            expectedSlave++;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdcPreservesTheOperationFailureWhenStopAlsoFails(bool reverse)
    {
        var bus = new VirtualAdcBus();
        var head = new AdcBoltHead(bus, new HantasSettings(), 0, "Virtual", 115200);
        var operationError = new IOException("Start response lost.");
        var stopError = new IOException("Stop response lost.");
        bus.FrameTransferred += (direction, frame) =>
        {
            if (direction == AdcFrameDirection.Transmit
                && frame[1] == (byte)AdcFunctionCode.WriteSingleRegister
                && BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2)) == (ushort)AdcRemoteRegister.RemoteStart)
            {
                throw BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4)) == 1 ? operationError : stopError;
            }
        };

        var error = await Assert.ThrowsAsync<AggregateException>(
            () => reverse ? head.RunReverseAsync(CancellationToken.None) : head.TightenAsync());
        Assert.Equal(new[] { operationError, stopError }, error.InnerExceptions);
    }

    [Fact]
    public async Task AdcDisconnectedRequestsFailClearlyAndCancelledReadinessDoesNotOpenTheBus()
    {
        var settings = new HantasSettings();
        Assert.Equal((byte)0, settings.PickupSlaveAddress);
        Assert.Equal((byte)1, settings.ShootingSlaveAddress);
        using var bus = new AdcBus(settings);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => bus.ReadDeviceInformationAsync(0));
        Assert.Contains("ADC is not connected", error.Message);

        var virtualBus = new VirtualAdcBus();
        var head = new AdcBoltHead(virtualBus, settings, 0, "Virtual", 115200);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => head.CheckReadyAsync(cancellation.Token));
        Assert.False(virtualBus.IsOpen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualReverseStopsOnReleaseOrCommunicationFailureWithoutACompletionResult(
        bool communicationFailure)
    {
        IAdcBus bus = new VirtualAdcBus();
        var head = new AdcBoltHead(bus, new HantasSettings(), 2, "Virtual", 115200);
        var started = false;
        var stops = 0;
        var writes = new List<(byte Slave, ushort Address, ushort Value)>();
        bus.FrameTransferred += (direction, frame) =>
        {
            if (direction != AdcFrameDirection.Transmit)
                return;
            var address = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2));
            if (frame[1] == (byte)AdcFunctionCode.WriteSingleRegister)
            {
                var value = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4));
                writes.Add((frame[0], address, value));
                if (address == (ushort)AdcRemoteRegister.RemoteStart)
                {
                    if (value == 0)
                        stops++;
                    else
                        started = true;
                }
            }

            if (communicationFailure
                && started
                && stops == 0
                && frame[1] == (byte)AdcFunctionCode.ReadInputRegisters)
                throw new IOException("Reverse monitoring failed.");
        };
        using var release = new CancellationTokenSource();
        var running = head.RunReverseAsync(release.Token);
        Assert.True(started);
        if (communicationFailure)
            await Assert.ThrowsAsync<IOException>(() => running);
        else
        {
            await Task.Delay(300);
            Assert.False(running.IsCompleted);
            var status = await bus.ReadControllerStatusAsync(2);
            Assert.True(status.Running);
            Assert.Equal(AdcDirection.Loosening, status.Direction);
            release.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }

        Assert.Equal(
            new (byte Slave, ushort Address, ushort Value)[]
            {
                (2, (ushort)AdcRemoteRegister.Direction, (ushort)AdcDirection.Loosening),
                (2, (ushort)AdcRemoteRegister.RemoteStart, 1),
                (2, (ushort)AdcRemoteRegister.RemoteStart, 0),
            },
            writes);
        Assert.Equal(1, stops);
        Assert.False((await bus.ReadControllerStatusAsync(2)).Running);
        Assert.Equal((ushort)0, (await bus.ReadFasteningResultAsync(2)).EventCount);
        Assert.True((await head.TightenAsync()).Success); // Forward explicitly restores its own direction.
    }

    [Fact]
    public async Task AdcReadinessUsesLiveRunAndDoesNotCallReverseAFastening()
    {
        IAdcBus bus = new VirtualAdcBus();
        var head = new AdcBoltHead(bus, new HantasSettings(), 1, "Virtual", 115200);
        await head.CheckReadyAsync();
        Assert.Equal((ushort)1, (await bus.ReadControllerStatusAsync(1)).Preset);
        var statusReads = 0;
        bus.FrameTransferred += (direction, frame) =>
        {
            if (direction == AdcFrameDirection.Transmit
                && frame[1] == (byte)AdcFunctionCode.ReadInputRegisters
                && BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2)) == (ushort)AdcStatusRegister.Preset)
                statusReads++;
        };
        await head.SelectPresetAsync(3);
        Assert.Equal(2, statusReads); // Selection is confirmed from the current preset register.
        Assert.Equal((ushort)3, (await bus.ReadControllerStatusAsync(1)).Preset);

        await bus.SetDirectionAsync(1, AdcDirection.Loosening);
        await bus.StartAsync(1);
        var running = await bus.ReadControllerStatusAsync(1);
        Assert.True(running.Running);
        Assert.False(running.Ready);
        Assert.Equal(AdcDirection.Loosening, running.Direction);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.CheckReadyAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.SelectPresetAsync(3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.SelectPresetAsync(4));
        Assert.Equal((ushort)3, (await bus.ReadControllerStatusAsync(1)).Preset);
        await Task.Delay(300);
        Assert.True((await bus.ReadControllerStatusAsync(1)).Running);
        Assert.Equal((ushort)0, (await bus.ReadFasteningResultAsync(1)).EventCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync());
        Assert.Equal((ushort)0, (await bus.ReadFasteningResultAsync(1)).EventCount);
        await head.CheckReadyAsync();
        Assert.False((await bus.ReadControllerStatusAsync(1)).Running);
    }

    [Theory]
    [InlineData(AdcFunctionCode.ReadInputRegisters)]
    [InlineData(AdcFunctionCode.WriteSingleRegister)]
    public async Task FailedFasteningPreparationStillStopsTheHead(AdcFunctionCode failingFunction)
    {
        IAdcBus bus = new VirtualAdcBus();
        var head = new AdcBoltHead(bus, new HantasSettings(), 1, "Virtual", 115200);
        await head.CheckReadyAsync();
        var failed = false;
        var stops = 0;
        bus.FrameTransferred += (direction, frame) =>
        {
            if (direction != AdcFrameDirection.Transmit)
                return;
            if (frame[1] == (byte)AdcFunctionCode.WriteSingleRegister
                && BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2)) == (ushort)AdcRemoteRegister.RemoteStart
                && BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4)) == 0)
            {
                stops++;
            }
            else if (!failed && frame[1] == (byte)failingFunction)
            {
                failed = true;
                throw new IOException("Injected ADC communication failure.");
            }
        };

        var fed = false;
        Task FeedAsync(CancellationToken token)
        {
            fed = true;
            return Task.CompletedTask;
        }
        await Assert.ThrowsAsync<IOException>(() => head.TightenAsync(feedAsync: FeedAsync));
        Assert.False(fed);
        Assert.Equal(1, stops);
        Assert.Equal(0, (await bus.ReadFasteningResultAsync(1)).EventCount);
        Assert.True((await head.TightenAsync()).Success);
        Assert.Equal(2, stops);
    }

    [Fact]
    public async Task ControllerErrorIsRecordedAsNgAndHardwareReadinessIsStillRequired()
    {
        IAdcBus bus = new VirtualAdcBus();
        var virtualBus = (VirtualAdcBus)bus;
        var head = new AdcBoltHead(bus, new HantasSettings(), 2, "Virtual", 115200);
        await head.CheckReadyAsync();
        virtualBus.SetNextFasteningResult(2, AdcEventStatus.Error);

        var result = await head.TightenAsync();
        Assert.False(result.Success);
        Assert.Contains("controller error", result.Error);
        Assert.Equal(1, (await bus.ReadFasteningResultAsync(2)).EventCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.CheckReadyAsync());

        virtualBus.SetNextFasteningResult(2, AdcEventStatus.FasteningNg);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.SelectPresetAsync(3));
        Assert.Equal(AdcEventStatus.Error, (await bus.ReadFasteningResultAsync(2)).Status);
        await bus.SetDirectionAsync(2, AdcDirection.Loosening);
        Assert.Equal(AdcEventStatus.Error, (await bus.ReadFasteningResultAsync(2)).Status);
        await bus.StartAsync(2);
        Assert.Equal(AdcEventStatus.Error, (await bus.ReadFasteningResultAsync(2)).Status);
        await bus.StopAsync(2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.CheckReadyAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync());
        Assert.Equal(1, (await bus.ReadFasteningResultAsync(2)).EventCount);

        await bus.ResetAlarmAsync(2);
        await head.CheckReadyAsync();
        Assert.False((await head.TightenAsync()).Success);
        Assert.Equal(2, (await bus.ReadFasteningResultAsync(2)).EventCount);
    }

    [Fact]
    public async Task ResultQueuedDuringFasteningAppliesOnceToSelectedSlave()
    {
        var bus = new VirtualAdcBus();
        var selected = new AdcBoltHead(bus, new HantasSettings(), 1, "Virtual", 115200);
        var other = new AdcBoltHead(bus, new HantasSettings(), 2, "Virtual", 115200);
        var current = selected.TightenAsync();
        Assert.False(current.IsCompleted);

        bus.SetNextFasteningResult(1, AdcEventStatus.FasteningNg);

        Assert.True((await current).Success);
        Assert.True((await other.TightenAsync()).Success);
        Assert.False((await selected.TightenAsync()).Success);
        Assert.True((await selected.TightenAsync()).Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedFasteningStopsAndPreservesTheNextResult(bool timedOut)
    {
        IAdcBus bus = new VirtualAdcBus();
        var settings = new HantasSettings();
        var head = new AdcBoltHead(bus, settings, 1, "Virtual", 115200);
        await head.SelectPresetAsync(3);
        Assert.True((await head.TightenAsync()).Success);
        using var stop = new CancellationTokenSource();
        if (timedOut)
            settings.FasteningTimeoutMilliseconds = 20;
        var tightening = head.TightenAsync(stop.Token);
        Assert.Equal(1, (await bus.ReadFasteningResultAsync(1)).EventCount);
        ((VirtualAdcBus)bus).SetNextFasteningResult(1, AdcEventStatus.FasteningNg);
        if (timedOut)
        {
            var error = await Assert.ThrowsAsync<TimeoutException>(() => tightening);
            Assert.Contains("ADC Virtual/1", error.Message);
            Assert.Contains("start event=1", error.Message);
            Assert.Contains("last event=1", error.Message);
            Assert.Contains("expected preset=3", error.Message);
        }
        else
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tightening);
        }
        await Task.Delay(300);
        Assert.Equal(1, (await bus.ReadFasteningResultAsync(1)).EventCount);
        Assert.False((await bus.ReadControllerStatusAsync(1)).Running);

        settings.FasteningTimeoutMilliseconds = 15_000;
        Assert.False((await head.TightenAsync()).Success);
        var completed = await bus.ReadFasteningResultAsync(1);
        Assert.Equal(2, completed.EventCount);
        Assert.Equal(3, completed.Preset);
        Assert.True((await head.TightenAsync()).Success);
    }
}
