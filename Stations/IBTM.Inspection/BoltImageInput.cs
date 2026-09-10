using System;
using IBTM.Core;

namespace IBTM.Inspection;

public static class BoltImageInput
{
    public static ImageFrame Create(ImageFrame image, int regionSize)
    {
        return Create(
            image,
            new PixelRegion((image.Width - regionSize) / 2, (image.Height - regionSize) / 2, regionSize, regionSize));
    }

    public static ImageFrame Create(ImageFrame image, PixelRegion region)
    {
        if (!region.IsInside(image.Width, image.Height))
            throw new ArgumentOutOfRangeException(
                nameof(region),
                $"ROI must fit within the {image.Width} x {image.Height} image.");

        var size = IBoltRecessSegmenter.InputSize;
        var stride = size * ImageFrame.ColorChannelCount;
        var pixels = new byte[stride * size];
        var left = region.X;
        var top = region.Y;
        var scaleX = (double)region.Width / size;
        var scaleY = (double)region.Height / size;

        for (var y = 0; y < size; y++)
        {
            var sourceY = Math.Clamp((y + 0.5) * scaleY - 0.5, 0, region.Height - 1);
            var y0 = (int)sourceY;
            var y1 = Math.Min(y0 + 1, region.Height - 1);
            var fy = sourceY - y0;
            var upper = (top + y0) * image.Stride;
            var lower = (top + y1) * image.Stride;
            for (var x = 0; x < size; x++)
            {
                var sourceX = Math.Clamp((x + 0.5) * scaleX - 0.5, 0, region.Width - 1);
                var x0 = (int)sourceX;
                var x1 = Math.Min(x0 + 1, region.Width - 1);
                var fx = sourceX - x0;
                for (var channel = 0; channel < ImageFrame.ColorChannelCount; channel++)
                {
                    var first = (left + x0) * ImageFrame.ColorChannelCount + channel;
                    var second = (left + x1) * ImageFrame.ColorChannelCount + channel;
                    var a = image.Pixels[upper + first] * (1 - fx) + image.Pixels[upper + second] * fx;
                    var b = image.Pixels[lower + first] * (1 - fx) + image.Pixels[lower + second] * fx;
                    pixels[y * stride + x * ImageFrame.ColorChannelCount + channel] = (byte)Math.Round(
                        a * (1 - fy) + b * fy);
                }
            }
        }

        return new ImageFrame(size, size, stride, pixels);
    }
}
