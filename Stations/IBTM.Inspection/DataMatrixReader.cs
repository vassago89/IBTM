using System;
using IBTM.Core;
using ZXing;
using ZXing.Common;

namespace IBTM.Inspection;

public static class DataMatrixReader
{
    public static string? Read(ImageFrame image, int width, int height)
    {
        if (width <= 0 || height <= 0 || width > image.Width || height > image.Height)
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "Data Matrix region must fit inside the camera FOV.");
        var stride = width * ImageFrame.ColorChannelCount;
        var pixels = new byte[stride * height];
        var left = (image.Width - width) / 2;
        var top = (image.Height - height) / 2;
        for (var row = 0; row < height; row++)
            Array.Copy(
                image.Pixels,
                (top + row) * image.Stride + left * ImageFrame.ColorChannelCount,
                pixels,
                row * stride,
                stride);
        var reader = new BarcodeReaderGeneric
        {
            Options = new DecodingOptions
            {
                PossibleFormats = [BarcodeFormat.DATA_MATRIX],
                TryHarder = true,
                TryInverted = true,
            },
        };
        return reader.Decode(pixels, width, height, RGBLuminanceSource.BitmapFormat.BGR24)?.Text;
    }
}
