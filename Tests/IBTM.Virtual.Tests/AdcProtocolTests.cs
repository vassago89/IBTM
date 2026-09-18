using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;
using IBTM.Hantas;
using IBTM.Virtual;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class AdcProtocolTests
{
    [Fact]
    public async Task FailedFeedRequiresANewConfirmedFeedBeforeRecordingResult()
    {
        var bus = new ControllerBus();
        var head = new AdcBoltHead(bus, new HantasSettings(), 1);
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
        Assert.True(head.HasPendingResult);
        // A controller result cannot prove that the cylinder fed the bolt.
        Assert.Null(await head.ReadPendingResultAsync());
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
        Assert.False(head.HasPendingResult);
    }

    [Fact]
    public async Task StopAcknowledgementWaitsForMotorFeedbackBeforeReleasingResult()
    {
        var bus = new ControllerBus { StopPollsRemaining = 2 };
        var head = new AdcBoltHead(bus, new HantasSettings(), 1);
        var tightening = head.TightenAsync();
        Assert.Equal(1, bus.StopWrites);
        Assert.True(bus.Running);
        Assert.True(head.HasPendingResult);
        Assert.False(tightening.IsCompleted);

        Assert.True((await tightening.WaitAsync(TimeSpan.FromSeconds(2))).Success);
        Assert.False(bus.Running);
        Assert.Equal(3, bus.StopFeedbackReads);
        Assert.False(head.HasPendingResult);
    }

    [Fact]
    public async Task UnconfirmedStopKeepsTheResultPendingAcrossRecoveryReads()
    {
        var bus = new ControllerBus { StopPollsRemaining = -1 };
        var head = new AdcBoltHead(bus, new HantasSettings { ResponseTimeoutMilliseconds = 40 }, 1);
        var failure = await Assert.ThrowsAsync<TimeoutException>(() => head.TightenAsync());
        Assert.Contains("motor stop was not confirmed", failure.Message);
        Assert.True(head.HasPendingResult);
        await Assert.ThrowsAsync<TimeoutException>(() => head.ReadPendingResultAsync());
        Assert.True(head.HasPendingResult);

        bus.StopPollsRemaining = 0;
        Assert.True((await head.ReadPendingResultAsync())!.Success);
        Assert.False(head.HasPendingResult);
        Assert.Equal(1, bus.StartWrites);
        Assert.Equal(1, bus.StopWrites); // Recovery only reads feedback; it does not restart the motor.
    }

    [Fact]
    public async Task MissingStopFeedbackDoesNotBecomeStopped()
    {
        var bus = new ControllerBus { StopReadFailure = new IOException("RUN feedback unavailable.") };
        var head = new AdcBoltHead(bus, new HantasSettings(), 1);
        var failure = await Assert.ThrowsAsync<IOException>(() => head.TightenAsync());
        Assert.Same(bus.StopReadFailure, failure);
        Assert.True(head.HasPendingResult);
        Assert.Equal(1, bus.StopWrites);
    }

    [Fact]
    public async Task InterruptedFasteningRestartsOnlyWithMatchingPreset()
    {
        IAdcBus bus = new VirtualAdcBus();
        var head = new AdcBoltHead(bus, new HantasSettings(), 1);
        await head.SelectPresetAsync(3);
        using var stop = new CancellationTokenSource();
        var tightening = head.TightenAsync(stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tightening);
        Assert.True(head.HasPendingResult);

        await bus.SelectPresetAsync(1, 7);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync());
        Assert.Contains("preset", failure.Message);
        Assert.True(head.HasPendingResult);
        Assert.Equal(0, (await bus.ReadFasteningResultAsync(1)).EventCount);
        Assert.False((await bus.ReadControllerStatusAsync(1)).Running);

        await bus.SelectPresetAsync(1, 3);
        Assert.True((await head.TightenAsync()).Success);
        Assert.False(head.HasPendingResult);
        Assert.Equal(1, (await bus.ReadFasteningResultAsync(1)).EventCount);
        Assert.False((await bus.ReadControllerStatusAsync(1)).Running);
    }

    [Theory]
    [InlineData(7, AdcDirection.Fastening)]
    [InlineData(3, AdcDirection.Loosening)]
    public async Task MismatchedResultIsNotRecordedOrDiscarded(ushort preset, AdcDirection direction)
    {
        var bus = new ControllerBus { ResultPreset = preset, ResultDirection = direction };
        var head = new AdcBoltHead(bus, new HantasSettings(), 1);
        await head.SelectPresetAsync(3);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync());
        Assert.Contains("does not match this fastening", failure.Message);
        Assert.True(head.HasPendingResult);
        Assert.False(bus.Running);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.ReadPendingResultAsync());
        Assert.True(head.HasPendingResult);
        Assert.Equal(1, bus.StartWrites);
    }

    [Fact]
    public async Task PresetRequiresReadbackAndMustStillMatchAtStart()
    {
        var bus = new ControllerBus { CurrentPreset = 1, IgnorePresetWrites = true };
        var head = new AdcBoltHead(bus, new HantasSettings(), 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.SelectPresetAsync(3));
        Assert.Equal(0, bus.StartWrites);

        bus.IgnorePresetWrites = false;
        await head.SelectPresetAsync(3);
        bus.CurrentPreset = 7; // Controller-panel change after successful selection.
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync());
        Assert.Equal(0, bus.StartWrites);
        Assert.False(head.HasPendingResult);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectionMustBeConfirmedBeforeStarting(bool reverse)
    {
        var bus = new ControllerBus
        {
            CurrentDirection = reverse ? AdcDirection.Fastening : AdcDirection.Loosening,
            IgnoreDirectionWrites = true,
        };
        var head = new AdcBoltHead(bus, new HantasSettings(), 1);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reverse ? head.RunReverseAsync(CancellationToken.None) : head.TightenAsync());
        Assert.Equal(0, bus.StartWrites);
        Assert.Equal(1, bus.StopWrites);
    }

    // Valid replies with independently controlled RUN feedback and command readback.
    internal sealed class ControllerBus : IAdcBus
    {
        public event Action<AdcFrameDirection, byte[]>? FrameTransferred { add { } remove { } }

        public ushort CurrentPreset { get; set; } = 3;
        public AdcDirection CurrentDirection { get; set; }
        public bool IgnorePresetWrites { get; set; }
        public bool IgnoreDirectionWrites { get; init; }
        public ushort? ResultPreset { get; init; }
        public AdcDirection? ResultDirection { get; init; }
        public int StopPollsRemaining { get; set; }
        public IOException? StopReadFailure { get; init; }
        public bool Running { get; private set; }
        public int StartWrites { get; private set; }
        public int StopWrites { get; private set; }
        public int StopFeedbackReads { get; private set; }
        public bool IsOpen { get; private set; } = true;

        public string PortName { get { return "Controller test bus"; } }

        public int BaudRate { get { return 115200; } }

        public string[] GetPortNames() { return []; }

        public void Open(string portName, int baudRate) { IsOpen = true; }

        public void Close() { IsOpen = false; }

        public Task<byte[]> ReadDeviceInformationAsync(byte slaveAddress, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new byte[12]);
        }

        public Task<byte[]> CaptureDeviceInformationAsync(byte slaveAddress, int durationMilliseconds, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task WriteRegisterAsync(byte slaveAddress, ushort address, ushort value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch ((AdcRemoteRegister)address)
            {
                case AdcRemoteRegister.Preset when !IgnorePresetWrites:
                    CurrentPreset = value;
                    break;
                case AdcRemoteRegister.Direction when !IgnoreDirectionWrites:
                    CurrentDirection = (AdcDirection)value;
                    break;
                case AdcRemoteRegister.RemoteStart:
                    if (value != 0)
                    {
                        StartWrites++;
                        Running = true;
                    }
                    else
                    {
                        StopWrites++;
                    }
                    break;
            }
            return Task.CompletedTask;
        }

        public Task<ushort[]> ReadRegistersAsync(byte slaveAddress, AdcFunctionCode function, ushort address, ushort count, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (address == (ushort)AdcStatusRegister.Preset)
            {
                if (StopWrites > 0)
                {
                    StopFeedbackReads++;
                    if (StopReadFailure is not null)
                        throw StopReadFailure;
                    if (StopPollsRemaining == 0)
                        Running = false;
                    else if (StopPollsRemaining > 0)
                        StopPollsRemaining--;
                }
                return Task.FromResult<ushort[]>([
                    CurrentPreset, 0, 0, (ushort)(Running ? 0 : 1), (ushort)(Running ? 1 : 0), 0, (ushort)CurrentDirection,
                ]);
            }
            if (address == (ushort)AdcResultRegister.EventCount)
                return Task.FromResult<ushort[]>([
                    (ushort)StartWrites, 250, ResultPreset ?? CurrentPreset, 100, 100, 1000, 0, 0, 0, (ushort)StartWrites, 0,
                    (ushort)(ResultDirection ?? CurrentDirection), (ushort)(StartWrites == 0 ? AdcEventStatus.None : AdcEventStatus.FasteningOk), 0,
                ]);
            throw new NotSupportedException();
        }
    }
}
