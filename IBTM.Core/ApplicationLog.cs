using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace IBTM.Core;

public sealed record LogEntry(long Sequence, DateTimeOffset Time, string Level, string Message, string? Detail)
{
    public string Text => $"{Time:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level}] {Message}"
        + (Detail is null ? "" : Environment.NewLine + Detail);
}

// Writers never invoke UI code or wait for the log file from a hardware thread.
public sealed class ApplicationLog : IDisposable
{
    public const int RecentEntryLimit = 2000;
    private readonly object _gate = new();
    private readonly Queue<LogEntry> _entries = new();
    private readonly Channel<string>? _fileQueue;
    private readonly Task? _fileWriter;
    private long _sequence;
    private string? _fileError;

    public ApplicationLog(string? filePath = null)
    {
        FilePath = filePath;
        if (filePath is null) return;
        _fileQueue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });
        _fileWriter = Task.Run(WriteFileAsync);
    }

    public string? FilePath { get; }
    public string? FileError { get { lock (_gate) return _fileError; } }
    public long LatestSequence { get { lock (_gate) return _sequence; } }
    public LogEntry[] ReadAfter(long sequence)
    {
        lock (_gate) return _entries.Where(entry => entry.Sequence > sequence).ToArray();
    }

    public void Write(string message) => Add("INFO", message, null);
    public void Error(string message, Exception? exception = null) =>
        Add("ERROR", message, exception?.ToString());

    private void Add(string level, string message, string? detail)
    {
        lock (_gate)
        {
            var entry = new LogEntry(++_sequence, DateTimeOffset.Now, level, message, detail);
            _entries.Enqueue(entry);
            while (_entries.Count > RecentEntryLimit) _entries.Dequeue();
            _fileQueue?.Writer.TryWrite(entry.Text);
        }
    }

    private async Task WriteFileAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath!))!);
            await using var stream = new FileStream(FilePath!, FileMode.Append, FileAccess.Write,
                FileShare.Read, 4096, useAsync: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            await foreach (var text in _fileQueue!.Reader.ReadAllAsync())
                await writer.WriteLineAsync(text).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            _fileQueue!.Writer.TryComplete();
            lock (_gate) _fileError = exception.Message;
            Error("Log file could not be written; recent messages remain available in this window.", exception);
            while (_fileQueue.Reader.TryRead(out _)) { }
        }
    }

    public void Dispose()
    {
        _fileQueue?.Writer.TryComplete();
        _fileWriter?.GetAwaiter().GetResult();
    }
}
