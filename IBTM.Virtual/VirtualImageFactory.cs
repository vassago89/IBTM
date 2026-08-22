using System;
using IBTM.Core;

namespace IBTM.Virtual;

internal static class VirtualImageFactory
{
    public const int Width = 320;
    public const int Height = 240;
    public const double InspectionMillimetersPerPixel = 0.05;

    private const int BytesPerPixel = 3;
    private static readonly (double X, double Y)[] BoltCentres =
    [
        (12, 11),
        (28, 11),
        (12, 19),
        (28, 19),
    ];

    public static ImageFrame Fiducial { get; } = CreateFiducial();

    public static ImageFrame CreateInspection(
        (double X, double Y, double Z) center)
    {
        var pixels = new byte[Width * Height * BytesPerPixel];
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
                var color = InspectionColor(x, y);
                SetPixel(
                    pixels,
                    pixelX,
                    pixelY,
                    color.Blue,
                    color.Green,
                    color.Red);
            }
        }

        return Frame(pixels);
    }

    private static ImageFrame CreateFiducial()
    {
        var pixels = new byte[Width * Height * BytesPerPixel];
        Array.Fill(pixels, (byte)28);
        for (var x = 0; x < Width; x++)
        {
            SetPixel(pixels, x, Height / 2, 40, 160, 40);
        }

        for (var y = 0; y < Height; y++)
        {
            SetPixel(pixels, Width / 2, y, 40, 160, 40);
        }

        return Frame(pixels);
    }

    private static (byte Blue, byte Green, byte Red) InspectionColor(
        double x,
        double y)
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

        foreach (var bolt in BoltCentres)
        {
            if (InsideCircle(x, y, bolt.X, bolt.Y, 0.35))
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

    private static ImageFrame Frame(byte[] pixels) =>
        new(Width, Height, Width * BytesPerPixel, pixels);

    private static void SetPixel(
        byte[] pixels,
        int x,
        int y,
        byte blue,
        byte green,
        byte red)
    {
        var index = ((y * Width) + x) * BytesPerPixel;
        pixels[index] = blue;
        pixels[index + 1] = green;
        pixels[index + 2] = red;
    }
}
