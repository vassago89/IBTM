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
    public Recipe Recipe { get; }
    private ImageFrame? _frame;
    private HeatSinkSlot? _dataMatrixHeatSink;
    private PixelRegion? _sourceRegion;
    private DataMatrixReadResult? _dataMatrixResult;
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
            if (_dataMatrixResult is { } result)
            {
                var description = UiText.Format($"Binary ROI · threshold {result.Threshold}");
                return result.Dilated ? description + UiText.Get(" · black dot dilation") : description;
            }
            return UiText.Format($"Binary ROI · threshold {DataMatrixThresholdMinimum}");
        }
    }

    public int DataMatrixThresholdMinimum
    {
        get => _dataMatrixHeatSink is { } heatSink ? Recipe.BoltInspection.GetDataMatrix(heatSink).ThresholdMinimum : 0;
        set
        {
            if (_dataMatrixHeatSink is not { } heatSink)
                throw new InvalidOperationException(UiText.Get("Select a Data Matrix before changing its threshold."));
            Recipe.BoltInspection.GetDataMatrix(heatSink).ThresholdMinimum = value;
            RefreshBinaryImage();
            OnPropertyChanged();
        }
    }

    public void Clear(HeatSinkSlot? dataMatrixHeatSink = null)
    {
        _dataMatrixHeatSink = dataMatrixHeatSink;
        _sourceRegion = null;
        _frame = null;
        Image = null;
        OnPropertyChanged(nameof(Recipe));
        OnPropertyChanged(nameof(Region));
        ClearResult();
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(DataMatrixThresholdMinimum));
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
        if (_dataMatrixHeatSink is not { } heatSink)
            throw new InvalidOperationException(UiText.Get("Select a Data Matrix."));
        Result = null;
        Success = null;
        _dataMatrixResult = null;
        OnPropertyChanged(nameof(BinaryDescription));
        var frame = _frame!;
        var region = _sourceRegion ?? throw new InvalidOperationException(UiText.Get("Draw the FOV ROI before inspecting."));

        var settings = Recipe.BoltInspection.GetDataMatrix(heatSink);
        var result = await Task.Run(() => DataMatrixReader.Read(frame, region, settings, token), token);
        token.ThrowIfCancellationRequested();
        // Show the successful attempt, or the last attempt if the whole search failed.
        _dataMatrixResult = result;
        Overlay = CreateBitmap(result.BinaryImage);
        Result = result.Text ?? UiText.Get("Not Read");
        Success = !string.IsNullOrEmpty(result.Text);
        OnPropertyChanged(nameof(BinaryDescription));
    }

    private void ClearResult()
    {
        _dataMatrixResult = null;
        Overlay = null;
        Result = null;
        Success = null;
    }

    internal void RefreshBinaryImage()
    {
        ClearResult();
        if (_dataMatrixHeatSink is not null && _frame is not null && _sourceRegion is { } region)
            Overlay = CreateBitmap(ImageBinarizer.Binarize(_frame, region, DataMatrixThresholdMinimum));
        OnPropertyChanged(nameof(BinaryDescription));
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
