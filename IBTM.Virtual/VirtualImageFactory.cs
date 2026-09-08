using System;
using System.Collections.Generic;
using System.Linq;
using IBTM.Core;
using ZXing;
using ZXing.Datamatrix;

namespace IBTM.Virtual;

internal static class VirtualImageFactory
{
    public const int Width = 320;
    public const int Height = 240;
    public const double InspectionMillimetersPerPixel = 0.05;
    public const byte BoltRecessIntensity = 8;
    private const double BoltRadius = 0.35;

    public static ImageFrame CreateInspection(
        (double X, double Y, double Z) center,
        IEnumerable<AxisPosition> boltCentres,
        IEnumerable<VirtualDataMatrix> dataMatrices)
    {
        var halfWidth = (Width / 2) * InspectionMillimetersPerPixel;
        var halfHeight = (Height / 2) * InspectionMillimetersPerPixel;
        var visibleBolts = boltCentres.Where(bolt =>
                Math.Abs(bolt.X - center.X) <= halfWidth + BoltRadius
                && Math.Abs(bolt.Y - center.Y) <= halfHeight + BoltRadius)
            .ToArray();
        var pixels = new byte[
            Width * Height * ImageFrame.ColorChannelCount];
        for (var pixelY = 0; pixelY < Height; pixelY++)
        {
            for (var pixelX = 0; pixelX < Width; pixelX++)
            {
                var x = center.X
                    + ((pixelX - (Width / 2))
                       * InspectionMillimetersPerPixel);
                var y = center.Y
                    + ((pixelY - (Height / 2))
                       * InspectionMillimetersPerPixel);
                var color = InspectionColor(x, y, visibleBolts);
                SetPixel(
                    pixels,
                    pixelX,
                    pixelY,
                    color.Blue,
                    color.Green,
                    color.Red);
            }
        }

        foreach (var code in dataMatrices)
        {
            var size = (int)(Math.Min(code.Width, code.Height) / InspectionMillimetersPerPixel);
            const int border = 8;
            var matrix = new DataMatrixWriter().encode(code.Text, BarcodeFormat.DATA_MATRIX,
                0, 0);
            var left = (int)Math.Round((code.Center.X - center.X) / InspectionMillimetersPerPixel) + Width / 2 - size / 2;
            var top = (int)Math.Round((code.Center.Y - center.Y) / InspectionMillimetersPerPixel) + Height / 2 - size / 2;
            for (var y = Math.Max(0, -top); y < Math.Min(size, Height - top); y++)
            for (var x = Math.Max(0, -left); x < Math.Min(size, Width - left); x++)
            {
                var black = x >= border && y >= border && x < size - border && y < size - border
                    && matrix[(x - border) * matrix.Width / (size - border * 2),
                        (y - border) * matrix.Height / (size - border * 2)];
                var intensity = black ? (byte)0 : (byte)255;
                SetPixel(pixels, left + x, top + y, intensity, intensity, intensity);
            }
        }

        return new ImageFrame(
            Width,
            Height,
            Width * ImageFrame.ColorChannelCount,
            pixels);
    }

    private static (byte Blue, byte Green, byte Red) InspectionColor(
        double x,
        double y,
        IReadOnlyList<AxisPosition> boltCentres)
    {
        if (OnRectangle(x, y, 0, 0, 40, 30, 0.12))
        {
            return (94, 104, 116);
        }

        if (OnRectangle(x, y, 4, 5, 18, 25, 0.10)
            || OnRectangle(x, y, 22, 5, 36, 25, 0.10))
        {
            return (48, 92, 116);
        }

        if (InsideCircle(x, y, 2, 2, 0.6)
            || InsideCircle(x, y, 38, 28, 0.6))
        {
            return (40, 190, 230);
        }

        foreach (var bolt in boltCentres)
        {
            var offsetX = Math.Abs(x - bolt.X);
            var offsetY = Math.Abs(y - bolt.Y);
            if ((offsetX <= 0.06 && offsetY <= 0.22)
                || (offsetX <= 0.22 && offsetY <= 0.06))
            {
                return (
                    BoltRecessIntensity,
                    BoltRecessIntensity,
                    BoltRecessIntensity);
            }

            if (InsideCircle(x, y, bolt.X, bolt.Y, BoltRadius))
            {
                return (190, 190, 190);
            }
        }

        if (Math.Abs(x % 5) < 0.04 || Math.Abs(y % 5) < 0.04)
        {
            return (38, 38, 38);
        }

        return (28, 28, 28);
    }

    private static bool OnRectangle(
        double x,
        double y,
        double left,
        double top,
        double right,
        double bottom,
        double thickness) =>
        x >= left - thickness
        && x <= right + thickness
        && y >= top - thickness
        && y <= bottom + thickness
        && (Math.Abs(x - left) <= thickness
            || Math.Abs(x - right) <= thickness
            || Math.Abs(y - top) <= thickness
            || Math.Abs(y - bottom) <= thickness);

    private static bool InsideCircle(
        double x,
        double y,
        double centerX,
        double centerY,
        double radius)
    {
        var offsetX = x - centerX;
        var offsetY = y - centerY;
        return (offsetX * offsetX) + (offsetY * offsetY)
            <= radius * radius;
    }

    private static void SetPixel(
        byte[] pixels,
        int x,
        int y,
        byte blue,
        byte green,
        byte red)
    {
        var index = ((y * Width) + x) * ImageFrame.ColorChannelCount;
        pixels[index + ImageFrame.BlueChannel] = blue;
        pixels[index + ImageFrame.GreenChannel] = green;
        pixels[index + ImageFrame.RedChannel] = red;
    }
}
