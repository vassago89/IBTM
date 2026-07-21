using System;
using IBTM.Device;

namespace IBTM.Virtual;

internal static class VirtualImageFactory
{
    public const int Width = 320;
    public const int Height = 240;

    private const int BytesPerPixel = 3;
    private static readonly (int X, int Y)[] BoltCentres =
    [
        (120, 90),
        (200, 90),
        (120, 150),
        (200, 150),
    ];

    public static ImageFrame CreateCameraFrame()
    {
        var pixels = new byte[Width * Height * BytesPerPixel];
        FillNoise(pixels, 18, 12);
        FillRectangle(pixels, 60, 50, 260, 190, 45, 15);
        DrawCrosshair(pixels);

        foreach (var (x, y) in BoltCentres)
        {
            DrawCircle(pixels, x, y, 5, 120, 30);
        }

        return CreateFrame(pixels);
    }

    public static ImageFrame CreateInspectionFrame()
    {
        var pixels = new byte[Width * Height * BytesPerPixel];
        FillNoise(pixels, 20, 15);
        FillRectangle(pixels, 80, 60, 240, 180, 60, 20);

        foreach (var (x, y) in BoltCentres)
        {
            DrawCircle(pixels, x, y, 6, 140, 30);
        }

        return CreateFrame(pixels);
    }

    private static void FillNoise(byte[] pixels, byte minimum, int variation)
    {
        for (var index = 0; index < pixels.Length; index += BytesPerPixel)
        {
            var value = (byte)(minimum + Random.Shared.Next(variation));
            pixels[index] = value;
            pixels[index + 1] = value;
            pixels[index + 2] = value;
        }
    }

    private static void FillRectangle(
        byte[] pixels,
        int left,
        int top,
        int right,
        int bottom,
        byte minimum,
        int variation)
    {
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var value = (byte)(minimum + Random.Shared.Next(variation));
                SetPixel(pixels, x, y, value, value, value);
            }
        }
    }

    private static void DrawCrosshair(byte[] pixels)
    {
        var centreX = Width / 2;
        var centreY = Height / 2;

        for (var x = 0; x < Width; x++)
        {
            SetPixel(pixels, x, centreY, 80, 160, 0);
        }

        for (var y = 0; y < Height; y++)
        {
            SetPixel(pixels, centreX, y, 80, 160, 0);
        }
    }

    private static void DrawCircle(
        byte[] pixels,
        int centreX,
        int centreY,
        int radius,
        byte minimum,
        int variation)
    {
        for (var y = -radius; y <= radius; y++)
        {
            for (var x = -radius; x <= radius; x++)
            {
                if ((x * x) + (y * y) <= radius * radius)
                {
                    var value = (byte)(minimum + Random.Shared.Next(variation));
                    SetPixel(pixels, centreX + x, centreY + y, value, value, value);
                }
            }
        }
    }

    private static void SetPixel(byte[] pixels, int x, int y, byte blue, byte green, byte red)
    {
        var index = ((y * Width) + x) * BytesPerPixel;
        pixels[index] = blue;
        pixels[index + 1] = green;
        pixels[index + 2] = red;
    }

    private static ImageFrame CreateFrame(byte[] pixels) =>
        new(Width, Height, Width * BytesPerPixel, pixels);
}
