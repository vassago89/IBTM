using System;
using IBTM.Core;

namespace IBTM.Inspection;

public sealed record BinaryCheckResult(ImageFrame Image, double BrightRatio);

public static class BinaryChecker
{
    public static BinaryCheckResult Check(ImageFrame image, PixelRegion region, int threshold)
    {
        if (threshold < 0 || threshold > 255)
            throw new ArgumentOutOfRangeException(nameof(threshold), "Use a brightness threshold from 0 to 255.");
        if (!region.IsInside(image.Width, image.Height))
            throw new ArgumentOutOfRangeException(nameof(region), "ROI must fit within the image.");
        if (image.Stride < (long)image.Width * ImageFrame.ColorChannelCount
            || image.Pixels.LongLength < (long)image.Stride * image.Height)
            throw new ArgumentException("The image does not contain complete BGR rows.", nameof(image));

        var stride = checked(region.Width * ImageFrame.ColorChannelCount);
        var pixels = new byte[checked(stride * region.Height)];
        var brightPixels = 0;
        for (var y = 0; y < region.Height; y++)
        {
            for (var x = 0; x < region.Width; x++)
            {
                var source = (region.Y + y) * image.Stride + (region.X + x) * ImageFrame.ColorChannelCount;
                var brightness = (299 * image.Pixels[source + ImageFrame.RedChannel]
                    + 587 * image.Pixels[source + ImageFrame.GreenChannel]
                    + 114 * image.Pixels[source + ImageFrame.BlueChannel] + 500) / 1000;
                var bright = brightness >= threshold;
                if (bright)
                    brightPixels++;
                var target = y * stride + x * ImageFrame.ColorChannelCount;
                pixels[target] = pixels[target + 1] = pixels[target + 2] = bright ? (byte)255 : (byte)0;
            }
        }

        return new(
            new ImageFrame(region.Width, region.Height, stride, pixels),
            (double)brightPixels / (region.Width * region.Height));
    }
}
