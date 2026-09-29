using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Inspection;
using IBTM.PcbPlacement;
using IBTM.Storage;
using IBTM.UI;
using Microsoft.Extensions.Logging;

namespace IBTM;

// Sequences enqueue result copies. This manager owns numbering, encoding and disk writes.
public sealed partial class PcbHistoryWriter : ObservableObject, IAsyncDisposable
{
    private readonly Lock _gate;
    private readonly Queue<PendingWrite> _pending;
    private readonly MachineStore _store;
    private readonly RecipeManager _recipes;
    private readonly PcbHistorySettings _settings;
    private readonly ILogger<PcbHistoryWriter> _log;
    private Task _writer;
    private bool _writing;
    private Exception? _failure;

    public PcbHistoryWriter(MachineStore store, RecipeManager recipes, PcbHistorySettings settings,
        PcbPlacer placement, BoltFasteningStation fastening, InspectionStation inspection,
        ILogger<PcbHistoryWriter> log)
    {
        _gate = new();
        _pending = new();
        _writer = Task.CompletedTask;
        _store = store;
        _recipes = recipes;
        _settings = settings;
        _log = log;
        placement.Station.AssemblyCreated += OnAssemblyCreated;
        fastening.Station.AssemblyCreated += OnAssemblyCreated;
        inspection.Station.AssemblyCreated += OnAssemblyCreated;
    }

    public event Action<PcbRecord>? Saved;
    public event Action<long>? ImageSaved;

    [ObservableProperty]
    public partial string? SaveError { get; private set; }

    [ObservableProperty]
    public partial int PendingCount { get; private set; }

    private sealed record PendingWrite(
        HeatSinkAssembly Assembly, string Directory, PcbRecord Record, InspectionCapture? Capture = null);

    private void OnAssemblyCreated(HeatSinkAssembly assembly)
    {
        var createdAt = DateTimeOffset.Now;
        var recipe = _recipes.Current;
        var bolts = recipe.Pcb.GetBolts(assembly.HeatSink).ToArray();
        // Keep this PCB in its original month/folder. Validate the path on the writer, too.
        var directory = _settings.Directory;
        var initial = new PcbRecord(0, createdAt, createdAt, recipe.Name, assembly.HeatSink,
            assembly.PcbBarcode, assembly.PcbBarcodeResult, assembly.FasteningResult, assembly.InspectionResult,
            assembly.ShootingBoltResults.ToDictionary(), assembly.PickupBoltResults.ToDictionary(),
            assembly.BoltPresenceResults.ToDictionary(),
            bolts.Select(bolt => bolt.Id).ToArray())
        {
            TurnsResult = assembly.TurnsResult,
            BoltNames = bolts.ToDictionary(bolt => bolt.Id, bolt => bolt.Name),
        };

        // The station records results after this creation callback returns.
        assembly.ResultsChanged += QueueResults;
        assembly.InspectionCaptured += QueueImage;
        Enqueue(new(assembly, directory, initial));

        void QueueResults(HeatSinkAssembly source)
        {
            var record = initial with
            {
                UpdatedAt = DateTimeOffset.Now,
                PcbBarcode = source.PcbBarcode,
                PcbBarcodeResult = source.PcbBarcodeResult,
                FasteningResult = source.FasteningResult,
                InspectionResult = source.InspectionResult,
                TurnsResult = source.TurnsResult,
                ShootingBoltResults = source.ShootingBoltResults.ToDictionary(),
                PickupBoltResults = source.PickupBoltResults.ToDictionary(),
                BoltPresenceResults = source.BoltPresenceResults.ToDictionary(),
            };
            Enqueue(new(source, directory, record));
        }

        void QueueImage(InspectionCapture capture)
        {
            // The camera/preview can reuse its buffer after this callback returns.
            var copy = capture with { Frame = capture.Frame with { Pixels = (byte[])capture.Frame.Pixels.Clone() } };
            Enqueue(new(assembly, directory, initial, copy));
        }
    }

    private void Enqueue(PendingWrite write)
    {
        lock (_gate)
        {
            _pending.Enqueue(write);
            PendingCount = _pending.Count;
            StartWriter();
        }
    }

    // Called under _gate. A failed head item stays queued until an explicit retry/flush.
    private void StartWriter()
    {
        if (_writing || _failure is not null || _pending.Count == 0)
            return;
        _writing = true;
        _writer = Task.Run(WritePending);
    }

    private void WritePending()
    {
        while (true)
        {
            PendingWrite write;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    _writing = false;
                    return;
                }
                write = _pending.Peek();
            }

            var file = write.Directory;
            try
            {
                // Allocate once on this single writer; a failed save keeps the same number.
                var number = write.Assembly.PcbNumber ?? _store.NextPcbNumber();
                write.Assembly.PcbNumber = number;
                file = Path.Combine(Path.GetFullPath(write.Directory), $"PCB-{write.Record.CreatedAt:yyyy-MM}.db");
                if (write.Capture is { } capture)
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(InspectionPreviewViewModel.CreateBitmap(capture.Frame)));
                    using var output = new MemoryStream();
                    encoder.Save(output);
                    var image = new PcbInspectionImage(capture.BoltId, capture.CapturedAt, capture.Region,
                        capture.Success, capture.Barcode, capture.BrightRatio, capture.MinimumBrightRatio, output.ToArray());
                    _store.SavePcbImage(file, number, image);
                    ImageSaved?.Invoke(number);
                }
                else
                {
                    var record = write.Record with { Number = number, DatabaseFile = file };
                    _store.SavePcb(file, record);
                    Saved?.Invoke(record);
                }
            }
            catch (Exception exception)
            {
                var number = write.Assembly.PcbNumber?.ToString() ?? "number pending";
                var failure = new IOException(
                    $"PCB {number} save failed ({file}): {exception.Message}. "
                    + "Unsaved data is held in memory; retry saving before exiting.", exception);
                _log.LogError(failure, "PCB persistence paused; machine operation continues.");
                lock (_gate)
                {
                    _failure = failure;
                    SaveError = failure.Message;
                    _writing = false;
                }
                return;
            }

            lock (_gate)
            {
                _pending.Dequeue();
                PendingCount = _pending.Count;
            }
        }
    }

    // Used by explicit retry and orderly exit, never by a machine sequence.
    public async Task FlushAsync()
    {
        Task writer;
        lock (_gate)
        {
            _failure = null;
            SaveError = null;
            StartWriter();
            writer = _writer;
        }

        while (true)
        {
            await writer.ConfigureAwait(false);
            lock (_gate)
            {
                if (_failure is not null)
                    ExceptionDispatchInfo.Throw(_failure);
                if (_pending.Count == 0)
                    return;
                writer = _writer;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await FlushAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A forced exit must still dispose the remaining hardware services.
            _log.LogError(exception, "Exiting with {Count} unsaved PCB writes.", PendingCount);
        }
    }
}
