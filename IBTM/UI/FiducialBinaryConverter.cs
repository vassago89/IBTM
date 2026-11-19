using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IBTM.Core;
using IBTM.Inspection;
using OpenCvSharp;

namespace IBTM.UI;

public sealed class FiducialBinaryConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not [BitmapSource source, int size, int threshold, bool whiteMark])
            return null;

        var region = PixelRegion.CenteredSquare(source.PixelWidth, source.PixelHeight, size);
        var stride = region.Width * 3;
        var pixels = new byte[stride * region.Height];
        if (source.Format != PixelFormats.Bgr24)
            source = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        source.CopyPixels(new Int32Rect(region.X, region.Y, region.Width, region.Height), pixels, stride, 0);
        using var cropped = Mat.FromPixelData(region.Height, region.Width, MatType.CV_8UC3, pixels, stride);
        using var binary = FiducialDetector.Binarize(cropped, threshold, whiteMark);
        var preview = BitmapSource.Create(region.Width, region.Height, 96, 96, PixelFormats.Gray8, null,
            binary.Data, checked((int)(binary.Step() * region.Height)), checked((int)binary.Step()));
        preview.Freeze();
        return preview;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
