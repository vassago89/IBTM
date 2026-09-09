using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using IBTM.Ajin;
using IBTM.AlphaMotion;
using IBTM.Core;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class ApplicationLogTests
{
    [Fact]
    public void KeepsEarlierMessagesAndReadsOnlyNewEntries()
    {
        using var log = new ApplicationLog();
        log.Write("Before opening the window");
        var first = Assert.Single(log.ReadAfter(0));
        log.Write("Newest message");

        Assert.Equal("Before opening the window", first.Message);
        Assert.Equal("Newest message", Assert.Single(log.ReadAfter(first.Sequence)).Message);
        Assert.Equal(2, log.LatestSequence);
        Assert.Empty(log.ReadAfter(log.LatestSequence));
    }

    [Fact]
    public void RetainsFullExceptionDetails()
    {
        using var log = new ApplicationLog();
        Exception error;
        try
        {
            throw new IOException("Input scan failed", new InvalidOperationException("Native error"));
        }
        catch (Exception exception) { error = exception; }

        log.Error("AJIN input read", error);

        var entry = Assert.Single(log.ReadAfter(0));
        Assert.Equal("ERROR", entry.Level);
        Assert.Equal(error.ToString(), entry.Detail);
        Assert.Contains(nameof(RetainsFullExceptionDetails), entry.Text);
        Assert.Contains("Native error", entry.Text);
        Assert.Contains("[ERROR] AJIN input read", entry.Text);
    }

    [Fact]
    public void TraceListenerKeepsEachFormattedEventTogetherWithItsSeverity()
    {
        using var log = new ApplicationLog();
        var type = typeof(MachineController).Assembly.GetType("IBTM.ApplicationTraceListener")!;
        using var listener = (TraceListener)Activator.CreateInstance(type, log)!;

        listener.TraceEvent(null, "IBTM", TraceEventType.Error, 0, "Settings failed: {0}", "test failure");
        listener.TraceEvent(null, "IBTM", TraceEventType.Information, 0, "Settings saved");

        var entries = log.ReadAfter(0);
        Assert.Equal(2, entries.Length);
        Assert.Equal("ERROR", entries[0].Level);
        Assert.Equal("Settings failed: test failure", entries[0].Message);
        Assert.Equal("INFO", entries[1].Level);
        Assert.Equal("Settings saved", entries[1].Message);
    }

    [Fact]
    public void ConcurrentWritersHaveOrderedSequencesAndBoundedRecentHistory()
    {
        using var log = new ApplicationLog();
        const int count = ApplicationLog.RecentEntryLimit + 1000;
        Parallel.For(0, count, index => log.Write($"Message {index}"));

        var entries = log.ReadAfter(0);
        Assert.Equal(ApplicationLog.RecentEntryLimit, entries.Length);
        Assert.Equal(Enumerable.Range(1001, ApplicationLog.RecentEntryLimit).Select(value => (long)value),
            entries.Select(entry => entry.Sequence));
        Assert.Equal(count, log.LatestSequence);
    }

    [Fact]
    public void FileRetainsFullHistoryAndDisposeFlushesPendingMessages()
    {
        var directory = Path.Combine(Path.GetTempPath(), "IBTM-log-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "session.log");
        const int count = ApplicationLog.RecentEntryLimit + 10;
        try
        {
            using (var log = new ApplicationLog(path))
            {
                for (var index = 0; index < count; index++) log.Write($"Message {index}");
                Assert.Equal(ApplicationLog.RecentEntryLimit, log.ReadAfter(0).Length);
            }
            var lines = File.ReadAllLines(path);
            Assert.Equal(count, lines.Length);
            Assert.EndsWith("Message 0", lines[0]);
            Assert.EndsWith($"Message {count - 1}", lines[^1]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    [Fact]
    public void FileFailureDoesNotLoseInMemoryMessagesOrThrowOnShutdown()
    {
        var directory = Path.Combine(Path.GetTempPath(), "IBTM-log-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var log = new ApplicationLog(directory);
            log.Write("Original machine error");
            log.Dispose();
            Assert.NotNull(log.FileError);
            Assert.Contains(log.ReadAfter(0), entry => entry.Message == "Original machine error");
            Assert.Contains(log.ReadAfter(0), entry => entry.Level == "ERROR"
                && entry.Message.StartsWith("Log file could not be written", StringComparison.Ordinal));
        }
        finally { Directory.Delete(directory); }
    }

    [Fact]
    public void AjinErrorIncludesOperationModuleOffsetAndNativeResult()
    {
        var check = typeof(AjinController).GetMethod("Check", BindingFlags.Static | BindingFlags.NonPublic)!;
        var error = Assert.Throws<TargetInvocationException>(() =>
            check.Invoke(null, [(uint)0x41D, "AxdiReadInportDword", (int?)4, (int?)0]));

        var message = Assert.IsType<IOException>(error.InnerException).Message;
        Assert.Contains("AxdiReadInportDword", message);
        Assert.Contains("module=4, offset=0", message);
        Assert.Contains("AXT_RT_NOT_OPEN (0x0000041D)", message);
    }

    [Fact]
    public void AlphaMotionErrorIncludesControllerStationAndBit()
    {
        using var controller = new AlphaMotionController(new() { ControllerNumber = 2, StationNumber = 3 });
        var check = typeof(AlphaMotionController).GetMethod("Check", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var error = Assert.Throws<TargetInvocationException>(() =>
            check.Invoke(controller, [-1, "nmiReadInputBit", (int?)7]));

        var message = Assert.IsType<IOException>(error.InnerException).Message;
        Assert.Contains("nmiReadInputBit", message);
        Assert.Contains("controller=2, station=3, bit=7", message);
        Assert.Contains("-1", message);
    }
}
