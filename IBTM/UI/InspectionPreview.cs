using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.Core;
using IBTM.Inspection;

namespace IBTM.UI;
// One captured frame, shared by ROI edits and reinspection. Never moves hardware.
public partial class InspectionPreview : ObservableObject
{
    private readonly BoltInspector _inspector;
    private readonly Recipe _recipe;
    private ImageFrame? _frame;
    private BinaryCheckResult? _check;
    private HeatSinkSlot? _pcb;
    private BoltTarget? _bolt;
    private PixelRegion? _sourceRegion;
    [ObservableProperty]
    private BitmapSource? _image;
    [ObservableProperty]
    private BitmapSource? _overlay;
    [ObservableProperty]
    private Rect? _region;
    [ObservableProperty]
    private string? _result;

    public InspectionPreview(
        BoltInspector inspector,
        Recipe recipe)
    {
        _inspector = inspector;
        _recipe = recipe;
    }

    public bool HasImage
    {
        get
        {
            return _frame is not null;
        }
    }

    public int BrightnessThreshold
    {
        get
        {
            return _bolt?.Point.BrightnessThreshold ?? _recipe.BoltInspection.BrightnessThreshold;
        }

        set
        {
            if (_bolt is null)
                throw new InvalidOperationException("Select a bolt before changing its threshold.");
            _bolt.Point.BrightnessThreshold = value;
            if (_check is not null)
            {
                _check = _inspector.Check(_frame!, _sourceRegion!, _bolt);
                Overlay = CreateBitmap(_check.Image);
                RefreshResult();
            }
            OnPropertyChanged();
        }
    }

    public double MinimumBrightPercent
    {
        get
        {
            return (_bolt?.Point.MinimumBrightRatio ?? _recipe.BoltInspection.MinimumBrightRatio) * 100;
        }

        set
        {
            if (!(value >= 0 && value <= 100))
                throw new ArgumentOutOfRangeException(nameof(value), "Use 0 to 100 percent.");
            if (_bolt is null)
                throw new InvalidOperationException("Select a bolt before changing its required bright percentage.");
            _bolt.Point.MinimumBrightRatio = value / 100;
            RefreshResult();
            OnPropertyChanged();
        }
    }

    public void Clear(HeatSinkSlot? pcb = null, BoltTarget? bolt = null)
    {
        _pcb = pcb;
        _bolt = bolt;
        _sourceRegion = null;
        _frame = null;
        Image = null;
        Region = null;
        ClearResult();
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(BrightnessThreshold));
        OnPropertyChanged(nameof(MinimumBrightPercent));
    }

    public async Task SetImageAsync(ImageFrame frame, CancellationToken token, PixelRegion? region = null)
    {
        var image = await Task.Run(() => CreateBitmap(frame), token);
        token.ThrowIfCancellationRequested();
        _frame = frame;
        _sourceRegion = region;
        Image = image;
        ClearResult();
        RefreshRegion();
        OnPropertyChanged(nameof(HasImage));
    }

    public void SetSavedImage(BitmapSource image, PixelRegion? region)
    {
        ClearResult();
        _frame = CreateFrame(image);
        _sourceRegion = region;
        Image = image;
        RefreshRegion();
        if (_bolt is not null && region is not null)
        {
            _check = _inspector.Check(_frame, region, _bolt);
            Overlay = CreateBitmap(_check.Image);
            RefreshResult();
        }
        OnPropertyChanged(nameof(HasImage));
    }

    public async Task InspectAsync(CancellationToken token)
    {
        ClearResult();
        var frame = _frame!;
        var region = _sourceRegion ?? throw new InvalidOperationException("Draw the FOV ROI before inspecting.");
        if (_pcb is not null)
        {
            var text = await Task.Run(() => DataMatrixReader.Read(frame, region), token);
            token.ThrowIfCancellationRequested();
            Result = string.IsNullOrEmpty(text) ? "Not Read" : text;
            return;
        }

        var threshold = BrightnessThreshold;
        var check = await Task.Run(() => BinaryChecker.Check(frame, region, threshold), token);
        var binary = await Task.Run(() => CreateBitmap(check.Image), token);
        token.ThrowIfCancellationRequested();
        if (threshold != BrightnessThreshold)
        {
            check = _inspector.Check(frame, region, _bolt!);
            binary = CreateBitmap(check.Image);
        }
        _check = check;
        Overlay = binary;
        RefreshResult();
    }

    private void ClearResult()
    {
        _check = null;
        Overlay = null;
        Result = null;
    }

    private void RefreshResult()
    {
        if (_check is null)
            return;
        var ratio = _check.BrightRatio;
        var minimum = _bolt?.Point.MinimumBrightRatio ?? _recipe.BoltInspection.MinimumBrightRatio;
        Result = $"{(ratio >= minimum ? "OK" : "NG")} · Bright {ratio * 100:0.###}%";
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
