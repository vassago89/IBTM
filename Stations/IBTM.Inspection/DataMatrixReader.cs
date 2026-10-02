using System;
using System.Threading;
using IBTM.Core;
using ZXing;
using ZXing.Common;

namespace IBTM.Inspection;

public sealed record DataMatrixReadResult(string? Text, int Threshold, bool Dilated, ImageFrame BinaryImage);

public static class DataMatrixReader
{
    public static DataMatrixReadResult Read(
        ImageFrame image, PixelRegion region, DataMatrixInspectionRecipe settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // One captured image and one set of options throughout the retry sequence.
        var minimum = settings.ThresholdMinimum;
        var maximum = settings.ThresholdMaximum;
        var step = settings.ThresholdStep;
        var radius = settings.DilationRadius;
        if (minimum > maximum)
            throw new InvalidOperationException("Data Matrix threshold minimum must not exceed maximum.");
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = settings.AutoRotate,
            Options = new DecodingOptions
            {
                PossibleFormats = [BarcodeFormat.DATA_MATRIX],
                TryHarder = settings.TryHarder,
                TryInverted = settings.TryInverted,
                PureBarcode = settings.PureBarcode,
            },
        };
        var candidate = minimum;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var binary = BinaryRegionAnalyzer.Check(image, region, candidate).Image;
            var text = reader.Decode(new RGBLuminanceSource(
                binary.Pixels, binary.Width, binary.Height, RGBLuminanceSource.BitmapFormat.BGR24))?.Text;
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(text))
                return new(text, candidate, false, binary);

            if (radius > 0)
            {
                // Retry this threshold after expanding black dots, before advancing to the next one.
                var pixels = new byte[binary.Pixels.Length];
                for (var y = 0; y < binary.Height; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (var x = 0; x < binary.Width; x++)
                    {
                        byte value = 255;
                        for (var sourceY = Math.Max(0, y - radius); sourceY <= Math.Min(binary.Height - 1, y + radius); sourceY++)
                        {
                            for (var sourceX = Math.Max(0, x - radius); sourceX <= Math.Min(binary.Width - 1, x + radius); sourceX++)
                                value = Math.Min(value, binary.Pixels[sourceY * binary.Stride + sourceX * ImageFrame.ColorChannelCount]);
                        }
                        var offset = y * binary.Stride + x * ImageFrame.ColorChannelCount;
                        pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
                    }
                }
                binary = binary with { Pixels = pixels };
                text = reader.Decode(new RGBLuminanceSource(
                    binary.Pixels, binary.Width, binary.Height, RGBLuminanceSource.BitmapFormat.BGR24))?.Text;
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(text))
                    return new(text, candidate, true, binary);
            }

            if (candidate == maximum)
                return new(null, candidate, radius > 0, binary);
            // Include the upper bound even when the step does not divide the range evenly.
            candidate = Math.Min(candidate + step, maximum);
        }
    }
}
