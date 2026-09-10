using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.Core;
using IBTM.Inspection;
using IBTM.Inspection.Training;

namespace IBTM.UI;

// One captured frame, shared by ROI edits and reinspection. Never moves hardware.
public partial class InspectionPreview(
    BoltInspector inspector, Recipe recipe, BoltTrainingSettings training) : ObservableObject
{
    private ImageFrame? _frame;
    private BoltPrediction? _prediction;
    private BitmapSource? _input;
    private HeatSinkSlot? _pcb;
    [ObservableProperty] private BitmapSource? _image;
    [ObservableProperty] private BitmapSource? _overlay;
    [ObservableProperty] private Rect? _region;
    [ObservableProperty] private string? _result;
    public bool HasImage => _frame is not null;
    public bool IsBolt => _pcb is null;

    public int RegionSize
    {
        get => recipe.BoltInspection.RegionSizePixels;
        set
        {
            if (value < 1) throw new ArgumentOutOfRangeException(nameof(value), "ROI size must be positive.");
            if (value == RegionSize) return;
            recipe.BoltInspection.RegionSizePixels = value;
            ClearPrediction();
            RefreshRegion();
            OnPropertyChanged();
        }
    }

    public double MinimumMaskPercent
    {
        get => recipe.BoltInspection.MinimumMaskRatio * 100;
        set
        {
            if (!(value >= 0 && value <= 100)) throw new ArgumentOutOfRangeException(nameof(value), "Use 0 to 100 percent.");
            recipe.BoltInspection.MinimumMaskRatio = value / 100;
            RefreshResult();
            OnPropertyChanged();
        }
    }

    public void Clear(HeatSinkSlot? pcb = null)
    {
        _pcb = pcb;
        _frame = null;
        Image = null;
        Region = null;
        ClearPrediction();
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(IsBolt));
        OnPropertyChanged(nameof(RegionSize));
        OnPropertyChanged(nameof(MinimumMaskPercent));
    }

    public async Task SetImageAsync(ImageFrame frame, CancellationToken token)
    {
        var image = await Task.Run(() => CreateBitmap(frame), token);
        token.ThrowIfCancellationRequested();
        _frame = frame;
        Image = image;
        ClearPrediction();
        RefreshRegion();
        OnPropertyChanged(nameof(HasImage));
    }

    public async Task InspectAsync(CancellationToken token)
    {
        ClearPrediction();
        var frame = _frame!;
        if (_pcb is not null)
        {
            var text = await Task.Run(() => inspector.ReadBarcode(frame), token);
            token.ThrowIfCancellationRequested();
            Result = string.IsNullOrEmpty(text) ? "Not Read" : text;
            return;
        }
        var prediction = await Task.Run(() => inspector.Predict(frame), token);
        var input = await Task.Run(() => CreateBitmap(prediction.Input), token);
        token.ThrowIfCancellationRequested();
        _prediction = prediction;
        _input = input;
        RefreshResult();
        RefreshOverlay();
    }

    private void ClearPrediction()
    {
        _prediction = null;
        _input = null;
        Overlay = null;
        Result = null;
    }

    private void RefreshResult()
    {
        if (_prediction is null) return;
        var ratio = _prediction.MaskRatio(training.MaskThreshold);
        Result = $"{(ratio >= recipe.BoltInspection.MinimumMaskRatio ? "OK" : "NG")} · Mask {ratio * 100:0.###}%";
    }

    private void RefreshOverlay()
    {
        if (_prediction is not null)
            Overlay = BoltTrainingImages.CreateOverlay(_input!, _prediction.Probabilities, training.MaskThreshold);
    }

    public void RefreshBarcodeRegion()
    {
        if (IsBolt) return;
        Result = null;
        RefreshRegion();
    }

    private void RefreshRegion()
    {
        if (_frame is null) return;
        if (!IsBolt && inspector.GetFieldOfView((_frame.Width, _frame.Height)).Width <= 0)
        {
            Region = null;
            return;
        }
        var (width, height) = _pcb is not null
            ? inspector.BarcodePixelSize()
            : (RegionSize, RegionSize);
        Region = new Rect((_frame.Width - width) / 2, (_frame.Height - height) / 2, width, height);
    }

    public static BitmapSource CreateBitmap(ImageFrame frame)
    {
        var image = BitmapSource.Create(frame.Width, frame.Height, 96, 96,
            PixelFormats.Bgr24, null, frame.Pixels, frame.Stride);
        image.Freeze();
        return image;
    }
}
