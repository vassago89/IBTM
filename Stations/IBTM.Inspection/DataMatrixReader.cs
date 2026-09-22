using System;
using IBTM.Core;
using ZXing;
using ZXing.Common;

namespace IBTM.Inspection;

public static class DataMatrixReader
{
    public static string? Read(ImageFrame image, PixelRegion region, DataMatrixInspectionRecipe? settings = null)
    {
        if (settings?.BinaryThreshold is { } threshold)
        {
            image = BinaryChecker.Check(image, region, threshold).Image;
            region = new(0, 0, image.Width, image.Height);
        }
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
        return reader.Decode(CreateLuminanceSource(image, region))?.Text;
    }

    public static ImageFrame? CreateBinaryImage(ImageFrame image, PixelRegion region, int? threshold)
    {
        if (threshold is { } value)
            return BinaryChecker.Check(image, region, value).Image;
        var matrix = new HybridBinarizer(CreateLuminanceSource(image, region)).BlackMatrix;
        if (matrix is null)
            return null;
        var stride = matrix.Width * ImageFrame.ColorChannelCount;
        var pixels = new byte[stride * matrix.Height];
        for (var y = 0; y < matrix.Height; y++)
        {
            for (var x = 0; x < matrix.Width; x++)
            {
                var offset = y * stride + x * ImageFrame.ColorChannelCount;
                var pixel = matrix[x, y] ? (byte)0 : (byte)255;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = pixel;
            }
        }
        return new(matrix.Width, matrix.Height, stride, pixels);
    }

    private static RGBLuminanceSource CreateLuminanceSource(ImageFrame image, PixelRegion region)
    {
        if (!region.IsInside(image.Width, image.Height))
            throw new ArgumentOutOfRangeException(nameof(region), "Data Matrix region must fit inside the camera FOV.");
        var stride = region.Width * ImageFrame.ColorChannelCount;
        var pixels = new byte[stride * region.Height];
        for (var row = 0; row < region.Height; row++)
            Array.Copy(image.Pixels, (region.Y + row) * image.Stride + region.X * ImageFrame.ColorChannelCount,
                pixels, row * stride, stride);
        return new RGBLuminanceSource(pixels, region.Width, region.Height, RGBLuminanceSource.BitmapFormat.BGR24);
    }
}
