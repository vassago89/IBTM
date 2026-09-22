using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Display;
using Serilog.Parsing;

namespace IBTM.Core;

public sealed record LogEntry(
    long Sequence,
    DateTimeOffset Time,
    string Level,
    string Message,
    string? Detail)
{
    public string Text => $"{Time:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level}] {Message}" + (Detail is null ? "" : Environment.NewLine + Detail);
}

// Screen history is a sink; Serilog owns file writing, queuing and flushing.
public sealed class ApplicationLog : ILogEventSink, ILoggingFailureListener, INotifyPropertyChanged
{
    public const int RecentEntryLimit = 2000;
    private readonly ObservableCollection<LogEntry> _entries;
    private readonly MessageTemplateTextFormatter _messageFormatter;
    private long _sequence;
    private string? _fileError;

    public ApplicationLog(string? filePath = null, string? communicationFilePath = null)
    {
        _entries = new();
        _messageFormatter = new("{Message:lj}", CultureInfo.InvariantCulture);
        SyncRoot = new();
        Entries = new ReadOnlyObservableCollection<LogEntry>(_entries);
        FilePath = filePath;
        CommunicationFilePath = communicationFilePath;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string? FilePath { get; }

    public string? CommunicationFilePath { get; }

    public ReadOnlyObservableCollection<LogEntry> Entries { get; }

    // Collection readers, including WPF binding, use the same lock as writers.
    public object SyncRoot { get; }

    public string? FileError
    {
        get
        {
            lock (SyncRoot)
                return _fileError;
        }
    }

    public long LatestSequence
    {
        get
        {
            lock (SyncRoot)
                return _sequence;
        }
    }

    public ILoggerFactory CreateLoggerFactory()
    {
        var configuration = new LoggerConfiguration().MinimumLevel.Verbose()
            .WriteTo.Logger(screen => screen
                .Filter.ByExcluding(IsCommunicationDetail)
                .WriteTo.Sink(this));
        foreach (var (path, communication) in new[] { (FilePath, false), (CommunicationFilePath, true) })
        {
            if (path is null)
                continue;
            try
            {
                // AuditTo propagates file failures to the async sink's failure listener.
                var fileLogger = new LoggerConfiguration().MinimumLevel.Verbose()
                    .AuditTo.File(
                        path,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u}] {Message:lj}{NewLine}{Exception}",
                        formatProvider: CultureInfo.InvariantCulture)
                    .CreateLogger();
                configuration.WriteTo.Logger(route => route
                    .Filter.ByIncludingOnly(logEvent => communication
                        ? IsCommunication(logEvent) : !IsCommunicationDetail(logEvent))
                    .WriteTo.Fallible(
                        sink => sink.Async(
                            file => file.Logger(fileLogger, attemptDispose: true),
                            bufferSize: int.MaxValue),
                        this));
            }
            catch (Exception exception)
            {
                OnLoggingFailed(this, LoggingFailureKind.Final, $"Unable to open the log file: {path}", null, exception);
            }
        }

        return new SerilogLoggerFactory(configuration.CreateLogger(), dispose: true);
    }

    private static bool IsCommunication(LogEvent logEvent)
    {
        return logEvent.Properties.TryGetValue("SourceContext", out var source)
            && source is ScalarValue { Value: "IBTM.Hantas.AdcBus" };
    }

    private static bool IsCommunicationDetail(LogEvent logEvent)
    {
        return logEvent.Level < LogEventLevel.Warning && IsCommunication(logEvent);
    }

    public void Emit(LogEvent logEvent)
    {
        using var message = new StringWriter(CultureInfo.InvariantCulture);
        _messageFormatter.Format(logEvent, message);
        lock (SyncRoot)
        {
            _entries.Add(new LogEntry(
                ++_sequence,
                logEvent.Timestamp,
                logEvent.Level.ToString().ToUpperInvariant(),
                message.ToString(),
                logEvent.Exception?.ToString()));
            while (_entries.Count > RecentEntryLimit)
                _entries.RemoveAt(0);
        }
    }

    public void OnLoggingFailed(
        object sender,
        LoggingFailureKind kind,
        string message,
        IReadOnlyCollection<LogEvent>? events,
        Exception? exception)
    {
        lock (SyncRoot)
        {
            if (_fileError is not null)
                return;
            _fileError = exception?.Message ?? message;
        }

        PropertyChanged?.Invoke(this, new(nameof(FileError)));
        // Report directly to the screen sink so a file error cannot recursively log to the failed file.
        Emit(new LogEvent(
            DateTimeOffset.Now,
            LogEventLevel.Error,
            exception,
            new MessageTemplateParser().Parse("Log file could not be written; recent messages remain available in this window."),
            []));
    }
}
