using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using IBTM.Mlcp;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class MlcpTests
{
    [Fact]
    public void ManualExampleKeepsIntegerValuesWithoutGuessingDecimalPlaces()
    {
        var frame = Frame("OK,02,+0043,-0025,");
        var result = MlcpResult.Parse(frame);

        Assert.True(result.Success);
        Assert.Equal(new decimal[] { 43, -25 }, result.Measurements.Select(item => item.Value));
        Assert.All(result.Measurements, item => Assert.Null(item.Success));
        Assert.Equal(Encoding.ASCII.GetString(frame), result.RawFrame);
    }

    [Fact]
    public void DecimalAndGroupResultsUseInvariantCultureAndKeepNg()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var result = MlcpResult.Parse(Frame("NG,02,+12.043,OK,-0.025,NG"));

            Assert.False(result.Success);
            Assert.Equal(new MlcpMeasurement[] { new(12.043m, true), new(-0.025m, false) }, result.Measurements);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void StartNumberHeaderDoesNotInventAnOkVerdict()
    {
        var result = MlcpResult.Parse(Frame("01,01,+0.043"));

        Assert.Equal("01", result.Header);
        Assert.Null(result.Success);
        Assert.Equal(0.043m, Assert.Single(result.Measurements).Value);
    }

    [Theory]
    [InlineData("OK,00,")]
    [InlineData("OK,02,+1.2")]
    [InlineData("OK,01,ERROR")]
    [InlineData("OK,01,+1.2,UNKNOWN")]
    public void InvalidDataIsRejectedInsteadOfReturningZeroOrOk(string body)
    {
        Assert.Throws<InvalidDataException>(() => MlcpResult.Parse(Frame(body)));
    }

    [Fact]
    public async Task StartWaitsForTheCompleteMeasurementAndIgnoresAck()
    {
        var connection = new TestConnection();
        using var controller = new MlcpController(connection);
        var measurement = controller.MeasureAsync();
        Assert.Equal("START\r\n", await connection.Commands.Reader.ReadAsync());
        connection.Reply(Encoding.ASCII.GetBytes("A\r\n"));
        var frame = Frame("OK,02,+0.043,-0.025,");
        connection.Reply(frame[..^1]);
        Assert.False(measurement.IsCompleted);
        connection.Reply(frame[^1..]);

        var result = await measurement.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.Success);
        Assert.Equal(new decimal[] { 0.043m, -0.025m }, result.Measurements.Select(item => item.Value));
        Assert.True(controller.IsOpen);
    }

    [Fact]
    public async Task InterruptedFrameDoesNotUseTheNextFrameForTheSameStart()
    {
        var connection = new TestConnection();
        using var controller = new MlcpController(connection);
        var measurement = controller.MeasureAsync();
        Assert.Equal("START\r\n", await connection.Commands.Reader.ReadAsync());
        connection.Reply([0x05, (byte)'O']);
        connection.Reply(Frame("OK,01,+1.20"));
        await Assert.ThrowsAsync<InvalidDataException>(() => measurement);
        Assert.False(connection.Commands.Reader.TryRead(out _));
    }

    [Fact]
    public async Task RecallAndStartAreSerializedAndCanUseThePage18CommandFormat()
    {
        var connection = new TestConnection();
        using var controller = new MlcpController(connection) { CommandFormat = MlcpCommandFormat.CrCommandCr };
        var measurement = controller.MeasureAsync();
        Assert.Equal("\rSTART\r", await connection.Commands.Reader.ReadAsync());
        var recalled = controller.ReadLastMeasurementAsync();
        Assert.False(connection.Commands.Reader.TryRead(out _));
        connection.Reply(Frame("NG,01,+1.2"));
        Assert.False((await measurement).Success);

        Assert.Equal("\rRECALL\r", await connection.Commands.Reader.ReadAsync());
        connection.Reply(Frame("OK,01,-2.4"));
        Assert.Equal(-2.4m, Assert.Single((await recalled).Measurements).Value);
    }

    [Fact]
    public async Task CancellingAQueuedRequestDoesNotInterruptTheActiveMeasurement()
    {
        var connection = new TestConnection();
        using var controller = new MlcpController(connection);
        var active = controller.MeasureAsync();
        Assert.Equal("START\r\n", await connection.Commands.Reader.ReadAsync());
        using var cancellation = new CancellationTokenSource();
        var queued = controller.ReadLastMeasurementAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.True(controller.IsOpen);
        Assert.False(connection.Commands.Reader.TryRead(out _));
        connection.Reply(Frame("OK,01,1.25"));
        Assert.Equal(1.25m, Assert.Single((await active).Measurements).Value);
    }

    [Fact]
    public async Task TimeoutClosesAnUncancellableReadWithoutRetryingStart()
    {
        var connection = new TestConnection { IgnoreReadCancellation = true };
        using var controller = new MlcpController(connection) { ResponseTimeout = TimeSpan.FromMilliseconds(100) };
        var measurement = controller.MeasureAsync();
        Assert.Equal("START\r\n", await connection.Commands.Reader.ReadAsync());

        await Assert.ThrowsAsync<TimeoutException>(() => measurement.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(connection.IsDisposed);
        Assert.False(controller.IsOpen);
        Assert.False(connection.Commands.Reader.TryRead(out _));
        await Assert.ThrowsAsync<IOException>(() => controller.ReadLastMeasurementAsync());
    }

    [Fact]
    public async Task CancellingAnActiveRequestClosesItsResponseChannel()
    {
        var connection = new TestConnection { IgnoreReadCancellation = true };
        using var controller = new MlcpController(connection);
        using var cancellation = new CancellationTokenSource();
        var measurement = controller.MeasureAsync(cancellation.Token);
        Assert.Equal("START\r\n", await connection.Commands.Reader.ReadAsync());
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => measurement.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(connection.IsDisposed);
        Assert.False(controller.IsOpen);
    }

    [Fact]
    public async Task CloseReleasesAnActiveWaitAsAConnectionFailure()
    {
        var connection = new TestConnection { IgnoreReadCancellation = true };
        using var controller = new MlcpController(connection);
        var measurement = controller.MeasureAsync();
        Assert.Equal("START\r\n", await connection.Commands.Reader.ReadAsync());
        controller.Close();

        await Assert.ThrowsAsync<IOException>(() => measurement.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(controller.IsOpen);
    }

    [Fact]
    public async Task HexResponseReportsTheUnsupportedMode()
    {
        var connection = new TestConnection();
        using var controller = new MlcpController(connection);
        var frames = new List<(MlcpFrameDirection Direction, byte[] Bytes)>();
        controller.FrameTransferred += (direction, bytes) => frames.Add((direction, bytes));
        var measurement = controller.MeasureAsync();
        await connection.Commands.Reader.ReadAsync();
        connection.Reply([0x02, 0x00, 0x01, 0x03]);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => measurement);
        Assert.Contains("ASCII", error.Message);
        Assert.False(controller.IsOpen);
        Assert.Equal(Encoding.ASCII.GetBytes("START\r\n"), frames[0].Bytes);
        Assert.Equal(MlcpFrameDirection.Transmit, frames[0].Direction);
        Assert.Equal(new byte[] { 0x02, 0x00, 0x01, 0x03 }, frames[1].Bytes);
        Assert.Equal(MlcpFrameDirection.Receive, frames[1].Direction);
    }

    private static byte[] Frame(string body)
    {
        return Encoding.ASCII.GetBytes("\x05" + body + "\x03@@\r\n");
    }

    internal sealed class TestConnection : Stream
    {
        private readonly Channel<byte> _received;

        public TestConnection()
        {
            _received = Channel.CreateUnbounded<byte>();
            Commands = Channel.CreateUnbounded<string>();
        }

        public Channel<string> Commands { get; }
        public bool IgnoreReadCancellation { get; init; }
        public string? ResponseBody { get; set; }
        public bool IsDisposed { get; private set; }
        public override bool CanRead => !IsDisposed;
        public override bool CanSeek => false;
        public override bool CanWrite => !IsDisposed;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public void Reply(byte[] bytes)
        {
            foreach (var value in bytes)
                Assert.True(_received.Writer.TryWrite(value));
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var first = await _received.Reader.ReadAsync(IgnoreReadCancellation ? CancellationToken.None : cancellationToken);
            buffer.Span[0] = first;
            var count = 1;
            while (count < buffer.Length && _received.Reader.TryRead(out var value))
                buffer.Span[count++] = value;
            return count;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            Assert.True(Commands.Writer.TryWrite(Encoding.ASCII.GetString(buffer.Span)));
            if (ResponseBody is { } body)
                Reply(Frame(body));
            return ValueTask.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            _received.Writer.TryComplete();
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
    }
}
