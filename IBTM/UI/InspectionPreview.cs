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
public partial class InspectionPreview : ObservableObject
{
    private readonly Recipe _recipe;
    private ImageFrame? _frame;
    private double? _brightRatio;
    private HeatSinkSlot? _pcb;
    private BoltPoint? _bolt;
    private PixelRegion? _sourceRegion;
    [ObservableProperty]
    public partial BitmapSource? Image { get; set; }
    [ObservableProperty]
    public partial BitmapSource? Overlay { get; set; }
    [ObservableProperty]
    public partial string? Result { get; set; }

    public InspectionPreview(Recipe recipe)
    {
        _recipe = recipe;
    }

    public bool HasImage => _frame is not null;

    public Rect? Region => _sourceRegion is { } region
        ? new Rect(region.X, region.Y, region.Width, region.Height)
        : null;

    public string BinaryDescription => _pcb is null ? $"Binary ROI · threshold {BrightnessThreshold}"
        : DataMatrixThreshold is { } threshold ? $"Binary ROI · threshold {threshold}"
        : HasImage && Overlay is null ? "Automatic binary unavailable · set a threshold"
        : "Binary ROI · automatic (ZXing)";

    public int? DataMatrixThreshold
    {
        get => _pcb is { } pcb ? _recipe.BoltInspection.GetDataMatrix(pcb).BinaryThreshold : null;
        set
        {
            if (_pcb is not { } pcb)
                throw new InvalidOperationException("Select a Data Matrix before changing its threshold.");
            _recipe.BoltInspection.GetDataMatrix(pcb).BinaryThreshold = value;
            RefreshBinaryImage();
            OnPropertyChanged();
        }
    }

    public int BrightnessThreshold
    {
        get => _bolt?.BrightnessThreshold ?? _recipe.BoltInspection.BrightnessThreshold;

        set
        {
            if (_bolt is null)
                throw new InvalidOperationException("Select a bolt before changing its threshold.");
            _bolt.BrightnessThreshold = value;
            RefreshBinaryImage();
            OnPropertyChanged();
        }
    }

    public double MinimumBrightPercent
    {
        get => (_bolt?.MinimumBrightRatio ?? _recipe.BoltInspection.MinimumBrightRatio) * 100;

        set
        {
            if (!(value >= 0 && value <= 100))
                throw new ArgumentOutOfRangeException(nameof(value), "Use 0 to 100 percent.");
            if (_bolt is null)
                throw new InvalidOperationException("Select a bolt before changing its required bright percentage.");
            _bolt.MinimumBrightRatio = value / 100;
            RefreshResult();
            OnPropertyChanged();
        }
    }

    public void Clear(HeatSinkSlot? pcb = null, BoltPoint? bolt = null)
    {
        _pcb = pcb;
        _bolt = bolt;
        _sourceRegion = null;
        _frame = null;
        Image = null;
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
        Result = null;
        var frame = _frame!;
        var region = _sourceRegion ?? throw new InvalidOperationException("Draw the FOV ROI before inspecting.");
        if (_pcb is not null)
        {
            RefreshBinaryImage();
            var settings = _recipe.BoltInspection.GetDataMatrix(_pcb.Value);
            var text = await Task.Run(() => DataMatrixReader.Read(frame, region, settings), token);
            token.ThrowIfCancellationRequested();
            Result = string.IsNullOrEmpty(text) ? "Not Read" : text;
            return;
        }

        var threshold = BrightnessThreshold;
        var (ratio, binary) = await Task.Run(() =>
        {
            var check = BinaryChecker.Check(frame, region, threshold);
            token.ThrowIfCancellationRequested();
            return (check.BrightRatio, CreateBitmap(check.Image));
        }, token);
        token.ThrowIfCancellationRequested();
        if (threshold != BrightnessThreshold)
        {
            var check = BinaryChecker.Check(frame, region, BrightnessThreshold);
            ratio = check.BrightRatio;
            binary = CreateBitmap(check.Image);
        }
        _brightRatio = ratio;
        Overlay = binary;
        RefreshResult();
    }

    private void ClearResult()
    {
        _brightRatio = null;
        Overlay = null;
        Result = null;
    }

    private void RefreshBinaryImage()
    {
        ClearResult();
        if (_frame is not null && _sourceRegion is { } region)
        {
            if (_pcb is not null)
            {
                var binary = DataMatrixReader.CreateBinaryImage(_frame, region, DataMatrixThreshold);
                Overlay = binary is null ? null : CreateBitmap(binary);
            }
            else if (_bolt is not null)
            {
                var check = BinaryChecker.Check(_frame, region, BrightnessThreshold);
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
        var minimum = _bolt?.MinimumBrightRatio ?? _recipe.BoltInspection.MinimumBrightRatio;
        Result = $"{(ratio >= minimum ? "OK" : "NG")} · Bright {ratio * 100:0.###}%";
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
