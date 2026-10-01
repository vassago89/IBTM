using System;
using System.IO;
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
public partial class InspectionPreviewViewModel : ObservableObject
{
    [ObservableProperty] public partial Recipe Recipe { get; internal set; }
    private ImageFrame? _frame;
    private double? _brightRatio;
    private HeatSinkSlot? _dataMatrixHeatSink;
    private BoltPoint? _bolt;
    private PixelRegion? _sourceRegion;
    [ObservableProperty]
    public partial BitmapSource? Image { get; set; }
    [ObservableProperty]
    public partial BitmapSource? Overlay { get; set; }
    [ObservableProperty]
    public partial string? Result { get; set; }
    [ObservableProperty]
    public partial bool? Success { get; private set; }

    public InspectionPreviewViewModel(Recipe recipe)
    {
        Recipe = recipe;
    }

    public bool HasImage => _frame is not null;

    public Rect? Region
    {
        get
        {
            return _sourceRegion is { } region
                ? new Rect(region.X, region.Y, region.Width, region.Height)
                : null;
        }
    }

    public string BinaryDescription
    {
        get
        {
            if (_dataMatrixHeatSink is null)
            {
                return _bolt is null
                    ? UiText.Get("Binary ROI · no inspection target")
                    : UiText.Format($"Binary ROI · threshold {BrightnessThreshold}");
            }
            if (DataMatrixThreshold is { } threshold)
                return UiText.Format($"Binary ROI · threshold {threshold}");
            if (HasImage && Overlay is null)
                return UiText.Get("Automatic binary unavailable · set a threshold");
            return UiText.Get("Binary ROI · automatic");
        }
    }

    public int? DataMatrixThreshold
    {
        get => _dataMatrixHeatSink is { } heatSink ? Recipe.BoltInspection.GetDataMatrix(heatSink).BinaryThreshold : null;
        set
        {
            if (_dataMatrixHeatSink is not { } heatSink)
                throw new InvalidOperationException(UiText.Get("Select a Data Matrix before changing its threshold."));
            Recipe.BoltInspection.GetDataMatrix(heatSink).BinaryThreshold = value;
            RefreshBinaryImage();
            OnPropertyChanged();
        }
    }

    public int BrightnessThreshold
    {
        get => _bolt?.BrightnessThreshold ?? Recipe.BoltInspection.BrightnessThreshold;

        set
        {
            if (_bolt is null)
                throw new InvalidOperationException(UiText.Get("Select a bolt before changing its threshold."));
            _bolt.BrightnessThreshold = value;
            RefreshBinaryImage();
            OnPropertyChanged();
        }
    }

    public double MinimumBrightPercent
    {
        get => (_bolt?.MinimumBrightRatio ?? Recipe.BoltInspection.MinimumBrightRatio) * 100;

        set
        {
            if (_bolt is null)
                throw new InvalidOperationException(UiText.Get("Select a bolt before changing its required bright percentage."));
            _bolt.MinimumBrightRatio = value / 100;
            RefreshResult();
            OnPropertyChanged();
        }
    }

    public void Clear(HeatSinkSlot? dataMatrixHeatSink = null, BoltPoint? bolt = null)
    {
        _dataMatrixHeatSink = dataMatrixHeatSink;
        _bolt = bolt;
        _sourceRegion = null;
        _frame = null;
        Image = null;
        OnPropertyChanged(nameof(Recipe));
        OnPropertyChanged(nameof(Region));
        ClearResult();
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(BrightnessThreshold));
        OnPropertyChanged(nameof(MinimumBrightPercent));
        OnPropertyChanged(nameof(DataMatrixThreshold));
        OnPropertyChanged(nameof(BinaryDescription));
    }

    public void SetSavedImage(BitmapSource image, PixelRegion? region)
    {
        _frame = CreateFrame(image);
        _sourceRegion = region;
        Image = image;
        OnPropertyChanged(nameof(Region));
        RefreshBinaryImage();
        OnPropertyChanged(nameof(HasImage));
    }

    public async Task InspectAsync(CancellationToken token)
    {
        if (_dataMatrixHeatSink is null && _bolt is null)
            throw new InvalidOperationException(UiText.Get("Select an image linked to a Data Matrix or bolt before inspecting."));
        Result = null;
        Success = null;
        var frame = _frame!;
        var region = _sourceRegion ?? throw new InvalidOperationException(UiText.Get("Draw the FOV ROI before inspecting."));
        if (_dataMatrixHeatSink is { } heatSink)
        {
            var settings = Recipe.BoltInspection.GetDataMatrix(heatSink);
            var text = await Task.Run(() => DataMatrixReader.Read(frame, region, settings), token);
            token.ThrowIfCancellationRequested();
            Result = string.IsNullOrEmpty(text) ? UiText.Get("Not Read") : text;
            Success = !string.IsNullOrEmpty(text);
            return;
        }

        var threshold = BrightnessThreshold;
        var (ratio, binary) = await Task.Run(() =>
        {
            var check = BinaryRegionAnalyzer.Check(frame, region, threshold);
            token.ThrowIfCancellationRequested();
            return (check.BrightRatio, CreateBitmap(check.Image));
        }, token);
        token.ThrowIfCancellationRequested();
        _brightRatio = ratio;
        Overlay = binary;
        RefreshResult();
    }

    private void ClearResult()
    {
        _brightRatio = null;
        Overlay = null;
        Result = null;
        Success = null;
    }

    private void RefreshBinaryImage()
    {
        ClearResult();
        if (_frame is not null && _sourceRegion is { } region)
        {
            if (_dataMatrixHeatSink is not null)
            {
                var binary = DataMatrixReader.CreateBinaryImage(_frame, region, DataMatrixThreshold);
                Overlay = binary is null ? null : CreateBitmap(binary);
            }
            else if (_bolt is not null)
            {
                var check = BinaryRegionAnalyzer.Check(_frame, region, BrightnessThreshold);
                _brightRatio = check.BrightRatio;
                Overlay = CreateBitmap(check.Image);
                RefreshResult();
            }
        }
        OnPropertyChanged(nameof(BinaryDescription));
    }

    private void RefreshResult()
    {
        if (_brightRatio is not { } ratio)
            return;
        var minimum = _bolt?.MinimumBrightRatio ?? Recipe.BoltInspection.MinimumBrightRatio;
        Result = UiText.Format($"{(ratio >= minimum ? "OK" : "NG")} · Bright {ratio * 100:0.###}%");
        Success = ratio >= minimum;
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

    public static BitmapSource DecodeImage(byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        var image = new PngBitmapDecoder(
            stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
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
