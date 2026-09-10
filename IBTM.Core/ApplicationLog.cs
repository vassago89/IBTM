using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace IBTM.Core;

public sealed record LogEntry(
    long Sequence,
    DateTimeOffset Time,
    string Level,
    string Message,
    string? Detail)
{
    public string Text
    {
        get
        {
            return $"{Time:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level}] {Message}" + (Detail is null ? "" : Environment.NewLine + Detail);
        }
    }
}

// Writers never wait for UI dispatch or file I/O from a hardware thread.
public sealed class ApplicationLog : IDisposable, INotifyPropertyChanged
{
    public const int RecentEntryLimit = 2000;
    private readonly object _gate = new();
    private readonly ObservableCollection<LogEntry> _entries = new();
    private readonly Channel<string>? _fileQueue;
    private readonly Task? _fileWriter;
    private long _sequence;
    private string? _fileError;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ApplicationLog(string? filePath = null)
    {
        Entries = new ReadOnlyObservableCollection<LogEntry>(_entries);
        FilePath = filePath;
        if (filePath is null)
            return;
        _fileQueue = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false, });
        _fileWriter = Task.Run(WriteFileAsync);
    }

    public string? FilePath { get; }

    public ReadOnlyObservableCollection<LogEntry> Entries { get; }

    // Collection readers, including WPF binding, use the same lock as writers.
    public object SyncRoot
    {
        get
        {
            return _gate;
        }
    }

    public string? FileError
    {
        get
        {
            lock (_gate)
                return _fileError;
        }
    }

    public long LatestSequence
    {
        get
        {
            lock (_gate)
                return _sequence;
        }
    }

    public void Write(string message)
    {
        Add("INFO", message, null);
    }

    public void Error(string message, Exception? exception = null)
    {
        Add("ERROR", message, exception?.ToString());
    }

    private void Add(string level, string message, string? detail)
    {
        lock (_gate)
        {
            var entry = new LogEntry(++_sequence, DateTimeOffset.Now, level, message, detail);
            _entries.Add(entry);
            while (_entries.Count > RecentEntryLimit)
                _entries.RemoveAt(0);
            _fileQueue?.Writer.TryWrite(entry.Text);
        }
    }

    private async Task WriteFileAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath!))!);
            await using var stream = new FileStream(
                FilePath!,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                4096,
                useAsync: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            await foreach (var text in _fileQueue!.Reader.ReadAllAsync())
                await writer.WriteLineAsync(text).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            _fileQueue!.Writer.TryComplete();
            lock (_gate)
                _fileError = exception.Message;
            PropertyChanged?.Invoke(this, new(nameof(FileError)));
            Error(
                "Log file could not be written; recent messages remain available in this window.",
                exception);
            while (_fileQueue.Reader.TryRead(out _))
            {
            }
        }
    }

    public void Dispose()
    {
        _fileQueue?.Writer.TryComplete();
        _fileWriter?.GetAwaiter().GetResult();
    }
}
