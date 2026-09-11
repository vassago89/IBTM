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
    BoltInspector inspector,
    Recipe recipe,
    BoltTrainingSettings training) : ObservableObject
{
    private ImageFrame? _frame;
    private BoltPrediction? _prediction;
    private BitmapSource? _input;
    private HeatSinkSlot? _pcb;
    private PixelRegion? _sourceRegion;
    [ObservableProperty]
    private BitmapSource? _image;
    [ObservableProperty]
    private BitmapSource? _overlay;
    [ObservableProperty]
    private Rect? _region;
    [ObservableProperty]
    private string? _result;
    public bool HasImage
    {
        get
        {
            return _frame is not null;
        }
    }

    public bool IsBolt
    {
        get
        {
            return _pcb is null;
        }
    }

    public double MinimumMaskPercent
    {
        get
        {
            return recipe.BoltInspection.MinimumMaskRatio * 100;
        }

        set
        {
            if (!(value >= 0 && value <= 100))
                throw new ArgumentOutOfRangeException(nameof(value), "Use 0 to 100 percent.");
            recipe.BoltInspection.MinimumMaskRatio = value / 100;
            RefreshResult();
            OnPropertyChanged();
        }
    }

    public void Clear(HeatSinkSlot? pcb = null)
    {
        _pcb = pcb;
        _sourceRegion = null;
        _frame = null;
        Image = null;
        Region = null;
        ClearPrediction();
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(IsBolt));
        OnPropertyChanged(nameof(MinimumMaskPercent));
    }

    public async Task SetImageAsync(ImageFrame frame, CancellationToken token, PixelRegion? region = null)
    {
        var image = await Task.Run(() => CreateBitmap(frame), token);
        token.ThrowIfCancellationRequested();
        _frame = frame;
        _sourceRegion = region;
        Image = image;
        ClearPrediction();
        RefreshRegion();
        OnPropertyChanged(nameof(HasImage));
    }

    public async Task InspectAsync(CancellationToken token)
    {
        ClearPrediction();
        var frame = _frame!;
        var region = _sourceRegion ?? throw new InvalidOperationException("Draw the FOV ROI before inspecting.");
        if (_pcb is not null)
        {
            var text = await Task.Run(() => DataMatrixReader.Read(frame, region), token);
            token.ThrowIfCancellationRequested();
            Result = string.IsNullOrEmpty(text) ? "Not Read" : text;
            return;
        }

        var prediction = await Task.Run(() => inspector.Predict(frame, region), token);
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
        if (_prediction is null)
            return;
        var ratio = _prediction.MaskRatio(training.MaskThreshold);
        Result = $"{(ratio >= recipe.BoltInspection.MinimumMaskRatio ? "OK" : "NG")} · Mask {ratio * 100:0.###}%";
    }

    private void RefreshOverlay()
    {
        if (_prediction is not null)
            Overlay = BoltTrainingImages.CreateOverlay(
                _input!,
                _prediction.Probabilities,
                training.MaskThreshold);
    }

    private void RefreshRegion()
    {
        Region = _sourceRegion is { } region
            ? new Rect(region.X, region.Y, region.Width, region.Height)
            : null;
    }

    public static BitmapSource CreateBitmap(ImageFrame frame)
    {
        var image = BitmapSource.Create(
            frame.Width,
            frame.Height,
            96,
            96,
            PixelFormats.Bgr24,
            null,
            frame.Pixels,
            frame.Stride);
        image.Freeze();
        return image;
    }

    public static ImageFrame CreateFrame(BitmapSource image)
    {
        if (image.Format != PixelFormats.Bgr24)
            image = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
        var stride = image.PixelWidth * ImageFrame.ColorChannelCount;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        return new ImageFrame(image.PixelWidth, image.PixelHeight, stride, pixels);
    }
}
