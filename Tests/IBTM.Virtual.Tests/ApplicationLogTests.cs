using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IBTM.Core;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class ApplicationLogTests
{
    [Fact]
    public void RetainsFullExceptionDetails()
    {
        var log = new ApplicationLog();
        using var factory = log.CreateLoggerFactory();
        var logger = factory.CreateLogger<ApplicationLogTests>();
        Exception error;
        try
        {
            throw new IOException("Input scan failed", new InvalidOperationException("Native error"));
        }
        catch (Exception exception)
        {
            error = exception;
        }

        logger.LogError(error, "AJIN input read");

        var entry = Assert.Single(log.Snapshot());
        Assert.Equal("ERROR", entry.Level);
        Assert.Equal(error.ToString(), entry.Detail);
        Assert.Contains(nameof(RetainsFullExceptionDetails), entry.Text);
        Assert.Contains("Native error", entry.Text);
        Assert.Contains("[ERROR] AJIN input read", entry.Text);
    }

    [Fact]
    public void TraceListenerKeepsEachFormattedEventTogetherWithItsSeverity()
    {
        var log = new ApplicationLog();
        using var factory = log.CreateLoggerFactory();
        using var listener = new ApplicationTraceListener(factory.CreateLogger<ApplicationTraceListener>());

        listener.TraceEvent(
            null,
            "IBTM",
            TraceEventType.Error,
            0,
            "Settings failed: {0}",
            "test failure");
        listener.TraceEvent(null, "IBTM", TraceEventType.Information, 0, "Settings saved");
        listener.TraceEvent(null, "IBTM", TraceEventType.Warning, 0, "Feedback delayed");
        listener.TraceEvent(null, "IBTM", TraceEventType.Critical, 0, "Control disconnected");

        var entries = log.Snapshot();
        Assert.Equal(4, entries.Length);
        Assert.Equal("ERROR", entries[0].Level);
        Assert.Equal("Settings failed: test failure", entries[0].Message);
        Assert.Equal("INFORMATION", entries[1].Level);
        Assert.Equal("Settings saved", entries[1].Message);
        Assert.Equal("WARNING", entries[2].Level);
        Assert.Equal("FATAL", entries[3].Level);
    }

    [Fact]
    public void ConcurrentWritersHaveOrderedSequencesAndBoundedRecentHistory()
    {
        var log = new ApplicationLog();
        using var factory = log.CreateLoggerFactory();
        var logger = factory.CreateLogger<ApplicationLogTests>();
        const int count = ApplicationLog.RecentEntryLimit + 1000;
        Parallel.For(0, count, index => logger.LogInformation("Message {Index}", index));

        var entries = log.Snapshot();
        Assert.Equal(ApplicationLog.RecentEntryLimit, entries.Length);
        Assert.Equal(
            Enumerable.Range(1001, ApplicationLog.RecentEntryLimit).Select(value => (long)value),
            entries.Select(entry => entry.Sequence));
        Assert.Equal(count, log.LatestSequence);
    }

    [Fact]
    public async Task FileRetainsFullHistoryAndFactoryDisposalFlushesPendingMessages()
    {
        var directory = Path.Combine(Path.GetTempPath(), "IBTM-log-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "session.log");
        const int count = ApplicationLog.RecentEntryLimit + 10;
        try
        {
            var log = new ApplicationLog(path);
            var factory = log.CreateLoggerFactory();
            try
            {
                var logger = factory.CreateLogger<ApplicationLogTests>();
                for (var index = 0; index < count; index++)
                    logger.LogInformation("Message {Index}", index);
                Assert.Equal(ApplicationLog.RecentEntryLimit, log.Snapshot().Length);
            }
            finally
            {
                await Task.Run(factory.Dispose);
            }

            var lines = File.ReadAllLines(path);
            Assert.Equal(count, lines.Length);
            Assert.EndsWith("Message 0", lines[0]);
            Assert.EndsWith($"Message {count - 1}", lines[^1]);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            if (Directory.Exists(directory))
                Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task FileFailureDoesNotLoseInMemoryMessagesOrThrowOnShutdown()
    {
        var directory = Path.Combine(Path.GetTempPath(), "IBTM-log-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var log = new ApplicationLog(directory);
            using var factory = log.CreateLoggerFactory();
            factory.CreateLogger<ApplicationLogTests>().LogError("Original machine error");
            await Task.Run(factory.Dispose);
            Assert.NotNull(log.FileError);
            Assert.Contains(log.Snapshot(), entry => entry.Message == "Original machine error");
            Assert.Contains(
                log.Snapshot(),
                entry =>
                    entry.Level == "ERROR"
                        && entry.Message.StartsWith("Log file could not be written", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory);
        }
    }
}
