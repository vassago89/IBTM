using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    public event Action<string, long>? ImageSaved;

    [ObservableProperty]
    public partial string? SaveError { get; private set; }

    [ObservableProperty]
    public partial int PendingCount { get; private set; }

    private sealed record PendingWrite(
        HeatSinkAssembly Assembly, string Directory, PcbRecord Record, InspectionCapture? Capture = null,
        bool ClearInspectionImages = false);

    private void OnAssemblyCreated(HeatSinkAssembly assembly)
    {
        var createdAt = DateTimeOffset.Now;
        var recipe = _recipes.Current;
        // Keep this PCB in its original month/folder. Validate the path on the writer, too.
        var directory = _settings.Directory;
        var initial = new PcbRecord(0, createdAt, createdAt, recipe.Name, assembly.HeatSink,
            assembly.PcbBarcode, assembly.PcbBarcodeResult, assembly.FasteningResult, assembly.InspectionResult,
            assembly.ShootingBoltResults.ToDictionary(), assembly.PickupBoltResults.ToDictionary(),
            recipe.Pcb.BoltPoints.Where(point => point.HeatSink == assembly.HeatSink).Select(bolt => bolt.Id).ToArray())
        {
            IsDataMatrixNgExcluded = assembly.IsDataMatrixNgExcluded,
            HeightReference = assembly.HeightReference,
            Alignment = assembly.Alignment,
            FiducialError = assembly.FiducialError,
            IsFiducialNgExcluded = assembly.IsFiducialNgExcluded,
            HeightResults = assembly.HeightResults.ToDictionary(),
        };

        // The station records results after this creation callback returns.
        assembly.ResultsChanged += QueueResults;
        assembly.InspectionCaptured += QueueImage;
        assembly.InspectionCleared += QueueInspectionClear;
        Enqueue(new(assembly, directory, initial));

        void QueueResults(HeatSinkAssembly source)
        {
            var record = initial with
            {
                UpdatedAt = DateTimeOffset.Now,
                PcbBarcode = source.PcbBarcode,
                PcbBarcodeResult = source.PcbBarcodeResult,
                IsDataMatrixNgExcluded = source.IsDataMatrixNgExcluded,
                HeightReference = source.HeightReference,
                Alignment = source.Alignment,
                FiducialError = source.FiducialError,
                IsFiducialNgExcluded = source.IsFiducialNgExcluded,
                HeightResults = source.HeightResults.ToDictionary(),
                FasteningResult = source.FasteningResult,
                InspectionResult = source.InspectionResult,
                ShootingBoltResults = source.ShootingBoltResults.ToDictionary(),
                PickupBoltResults = source.PickupBoltResults.ToDictionary(),
            };
            Enqueue(new(source, directory, record));
        }

        void QueueImage(InspectionCapture capture)
        {
            // The camera/preview can reuse its buffer after this callback returns.
            var copy = capture with { Frame = capture.Frame with { Pixels = (byte[])capture.Frame.Pixels.Clone() } };
            Enqueue(new(assembly, directory, initial, copy));
        }

        void QueueInspectionClear(HeatSinkAssembly source)
        {
            Enqueue(new(source, directory, initial, ClearInspectionImages: true));
        }
    }

    private void Enqueue(PendingWrite write)
    {
        lock (_gate)
        {
            _pending.Enqueue(write);
            if (_pending.Count == 1)
                _writer = Task.Run(WritePending);
            PendingCount = _pending.Count;
        }
    }

    private void WritePending()
    {
        while (true)
        {
            PendingWrite write;
            lock (_gate)
            {
                write = _pending.Peek();
            }

            var file = write.Directory;
            try
            {
                var number = write.Assembly.PcbNumber ?? _store.NextPcbNumber(write.Directory);
                write.Assembly.PcbNumber = number;
                file = Path.Combine(Path.GetFullPath(write.Directory), $"PCB-{write.Record.CreatedAt:yyyy-MM}.db");
                if (write.ClearInspectionImages)
                {
                    _store.DeletePcbImages(file, number);
                    ImageSaved?.Invoke(file, number);
                }
                else if (write.Capture is { } capture)
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(InspectionPreviewViewModel.CreateBitmap(capture.Frame)));
                    using var output = new MemoryStream();
                    encoder.Save(output);
                    var image = new PcbInspectionImage(capture.BoltId, capture.CapturedAt, capture.Region,
                        capture.Success, capture.Barcode, output.ToArray(),
                        capture.Threshold, capture.Dilated);
                    _store.SavePcbImage(file, number, image);
                    ImageSaved?.Invoke(file, number);
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
                var number = write.Assembly.PcbNumber?.ToString() ?? "—";
                SaveError = UiText.Format($"PCB {number} save failed ({file}): {UiText.Get(exception)}");
                _log.LogError(exception, "PCB {Number} save failed at {File}.", number, file);
            }

            lock (_gate)
            {
                _pending.Dequeue();
                var remaining = _pending.Count;
                PendingCount = remaining;
                if (remaining == 0)
                    return;
            }
        }
    }

    public Task FlushAsync()
    {
        lock (_gate)
            return _writer;
    }

    public ValueTask DisposeAsync()
    {
        return new(FlushAsync());
    }
}
