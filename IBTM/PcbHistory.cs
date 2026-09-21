using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media.Imaging;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Inspection;
using IBTM.PcbPlacement;
using IBTM.Storage;
using IBTM.UI;

namespace IBTM;

public sealed class PcbHistory
{
    private readonly Lock _gate;
    private readonly MachineStore _store;
    private readonly RecipeManager _recipes;
    private readonly PcbHistorySettings _settings;

    public PcbHistory(MachineStore store, RecipeManager recipes, PcbHistorySettings settings,
        PcbPlacementWork placement, BoltFasteningWork fastening, InspectionWork inspection)
    {
        _gate = new();
        _store = store;
        _recipes = recipes;
        _settings = settings;
        placement.AssemblyCreated += OnAssemblyCreated;
        fastening.AssemblyCreated += OnAssemblyCreated;
        inspection.AssemblyCreated += OnAssemblyCreated;
    }

    public event Action<PcbRecord>? Saved;
    public event Action<long>? ImageSaved;

    private void OnAssemblyCreated(HeatSinkAssembly assembly)
    {
        lock (_gate)
        {
            var number = _store.NextPcbNumber();
            var createdAt = DateTimeOffset.Now;
            var recipeName = _recipes.Current.Name;
            var file = Path.Combine(Path.GetFullPath(_settings.Directory), $"PCB-{createdAt:yyyy-MM}.db");
            assembly.PcbNumber = number;
            // Keep this PCB in its original file, even across midnight or a folder change.
            assembly.ResultsChanged += SaveResults;
            assembly.InspectionCaptured += SaveImage;
            SaveResults(assembly);

            void SaveResults(HeatSinkAssembly source)
            {
                lock (_gate)
                {
                    var record = new PcbRecord(number, createdAt, DateTimeOffset.Now,
                        recipeName, source.HeatSink, source.PcbBarcode, source.PcbBarcodeResult,
                        source.FasteningResult, source.InspectionResult,
                        source.PcbBoltResults.OrderBy(pair => pair.Key).ToDictionary(),
                        source.PickupBoltResults.OrderBy(pair => pair.Key).ToDictionary(),
                        source.BoltPresenceResults.OrderBy(pair => pair.Key).ToDictionary())
                    {
                        DatabaseFile = file,
                    };
                    _store.SavePcb(file, record);
                    Saved?.Invoke(record);
                }
            }

            void SaveImage(InspectionCapture capture)
            {
                var bitmap = InspectionPreview.CreateBitmap(capture.Frame);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = new MemoryStream();
                encoder.Save(output);
                var image = new PcbInspectionImage(capture.BoltNumber, capture.CapturedAt, capture.Region,
                    capture.Success, capture.Barcode, capture.BrightRatio, capture.MinimumBrightRatio, output.ToArray());
                lock (_gate)
                    _store.SavePcbImage(file, number, image);
                ImageSaved?.Invoke(number);
            }
        }
    }
}
