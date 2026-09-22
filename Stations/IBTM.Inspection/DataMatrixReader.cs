using System;
using IBTM.Core;
using ZXing;
using ZXing.Common;

namespace IBTM.Inspection;

public static class DataMatrixReader
{
    public static string? Read(ImageFrame image, PixelRegion region, DataMatrixInspectionRecipe? settings = null)
    {
        if (!region.IsInside(image.Width, image.Height))
            throw new ArgumentOutOfRangeException(
                nameof(region),
                "Data Matrix region must fit inside the camera FOV.");
        if (settings?.BinaryThreshold is { } threshold)
        {
            image = BinaryChecker.Check(image, region, threshold).Image;
            region = new(0, 0, image.Width, image.Height);
        }
        var width = region.Width;
        var height = region.Height;
        var stride = width * ImageFrame.ColorChannelCount;
        var pixels = new byte[stride * height];
        for (var row = 0; row < height; row++)
            Array.Copy(
                image.Pixels,
                (region.Y + row) * image.Stride + region.X * ImageFrame.ColorChannelCount,
                pixels,
                row * stride,
                stride);
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = settings?.AutoRotate ?? false,
            Options = new DecodingOptions
            {
                PossibleFormats = [BarcodeFormat.DATA_MATRIX],
                TryHarder = settings?.TryHarder ?? true,
                TryInverted = settings?.TryInverted ?? true,
                PureBarcode = settings?.PureBarcode ?? false,
            },
        };
        return reader.Decode(pixels, width, height, RGBLuminanceSource.BitmapFormat.BGR24)?.Text;
    }
}
