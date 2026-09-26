using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Hantas;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class ApplicationLogTests
{
    [Fact]
    public async Task LogFolderAndRetentionPersistInMachineSettings()
    {
        var store = VirtualTest.OpenMachineStore();
        var settings = await MachineSettings.LoadAsync(store);
        Assert.Equal(30, settings.Logging.RetentionDays);
        settings.Logging.Directory = Path.Combine(Path.GetTempPath(), "IBTM configured logs");
        settings.Logging.RetentionDays = 14;
        await store.SaveSettingsAsync(settings.Sections);

        var reloaded = await MachineSettings.LoadAsync(store);
        Assert.Equal(settings.Logging.Directory, reloaded.Logging.Directory);
        Assert.Equal(14, reloaded.Logging.RetentionDays);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.Logging.RetentionDays = 0);
    }

    [Fact]
    public async Task SerilogRetentionRemovesOnlyExpiredDailyLogsAndAppendsAfterRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "IBTM-log-test-" + Guid.NewGuid().ToString("N"));
        var communicationDirectory = Path.Combine(directory, "Communication");
        var folders = new[] { directory, communicationDirectory };
        var today = DateTime.Today;
        try
        {
            foreach (var folder in folders)
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, $"IBTM-{today.AddDays(-10):yyyyMMdd}.log"), "expired");
                File.WriteAllText(Path.Combine(folder, $"IBTM-{today.AddDays(-2):yyyyMMdd}.log"), "retained");
                File.WriteAllText(Path.Combine(folder, "IBTM-20000101-120000-000-123.log"), "legacy session");
                File.WriteAllText(Path.Combine(folder, "other.log"), "unrelated");
            }
            for (var session = 1; session <= 2; session++)
            {
                var log = new ApplicationLog(Path.Combine(directory, "IBTM-.log"),
                    Path.Combine(communicationDirectory, "IBTM-.log"), retentionDays: 7);
                using var factory = log.CreateLoggerFactory();
                factory.CreateLogger<ApplicationLogTests>().LogInformation("Session {Session}", session);
                factory.CreateLogger<AdcBus>().LogInformation("Session {Session}", session);
                await Task.Run(factory.Dispose);
                Assert.Null(log.FileError);
            }
            foreach (var folder in folders)
            {
                Assert.False(File.Exists(Path.Combine(folder, $"IBTM-{today.AddDays(-10):yyyyMMdd}.log")));
                Assert.True(File.Exists(Path.Combine(folder, $"IBTM-{today.AddDays(-2):yyyyMMdd}.log")));
                Assert.True(File.Exists(Path.Combine(folder, "IBTM-20000101-120000-000-123.log")));
                Assert.True(File.Exists(Path.Combine(folder, "other.log")));
                var text = File.ReadAllText(Path.Combine(folder, $"IBTM-{today:yyyyMMdd}.log"));
                Assert.Contains("Session 1", text);
                Assert.Contains("Session 2", text);
            }
        }
        finally
        {
            foreach (var folder in folders.Reverse())
            {
                if (!Directory.Exists(folder))
                    continue;
                foreach (var file in Directory.GetFiles(folder))
                    File.Delete(file);
                Directory.Delete(folder);
            }
        }
    }

    [Fact]
    public async Task CommunicationFilesKeepFramesOutOfMachineHistoryButRetainErrorsInBoth()
    {
        var directory = Path.Combine(Path.GetTempPath(), "IBTM-log-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "session.log");
        var communicationDirectory = Path.Combine(directory, "Communication");
        var communicationPath = Path.Combine(communicationDirectory, "session.log");
        try
        {
            var log = new ApplicationLog(path, communicationPath);
            using (var factory = log.CreateLoggerFactory())
            {
                var communication = factory.CreateLogger<AdcBus>();
                Parallel.For(0, 64, index => communication.LogInformation(
                    "ADC [{Port}] RX RAW {Frame}", "COM9", $"FRAME-{index}"));
                communication.LogDebug("RTU response decoded");
                communication.LogWarning("ADC request rejected");
                communication.LogError(new IOException("Response timed out"), "ADC exchange failed");
                factory.CreateLogger<ApplicationLogTests>().LogInformation("Machine cycle completed");

                Assert.Equal(3, log.Snapshot().Length);
                Assert.DoesNotContain(log.Snapshot(), entry => entry.Message.Contains("RX RAW"));
                await Task.Run(factory.Dispose);
            }

            var machine = File.ReadAllText(path);
            var communicationText = File.ReadAllText(communicationPath);
            Assert.Contains("Machine cycle completed", machine);
            Assert.DoesNotContain("RX RAW", machine);
            Assert.DoesNotContain("RTU response decoded", machine);
            Assert.DoesNotContain("Machine cycle completed", communicationText);
            Assert.Equal(64, File.ReadAllLines(communicationPath).Count(line => line.Contains("RX RAW")));
            Assert.Contains("RTU response decoded", communicationText);
            foreach (var text in new[] { machine, communicationText })
            {
                Assert.Contains("ADC request rejected", text);
                Assert.Contains("ADC exchange failed", text);
                Assert.Contains("Response timed out", text);
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            if (File.Exists(communicationPath))
                File.Delete(communicationPath);
            if (Directory.Exists(communicationDirectory))
                Directory.Delete(communicationDirectory);
            if (Directory.Exists(directory))
                Directory.Delete(directory);
        }
    }

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
