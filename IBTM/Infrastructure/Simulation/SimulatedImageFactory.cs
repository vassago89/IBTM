namespace IBTM.Infrastructure.Simulation;

internal static class SimulatedImageFactory
{
    public const int Width = 320;
    public const int Height = 240;

    private const int BytesPerPixel = 3;

    public static ImageFrame CreateCameraFrame(bool drawCrosshair)
    {
        var pixels = new byte[Width * Height * BytesPerPixel];
        FillNoise(pixels, 18, 12);
        FillRectangle(pixels, 60, 50, 260, 190, 45, 15);

        if (drawCrosshair)
        {
            DrawCrosshair(pixels);
        }

        foreach (var (x, y) in BoltCentres)
        {
            DrawNoisyCircle(pixels, x, y, radius: 5, minimum: 120, variation: 30);
        }

        return CreateBitmap(pixels);
    }

    public static ImageFrame CreateInspectionFrame()
    {
        var pixels = new byte[Width * Height * BytesPerPixel];
        FillNoise(pixels, 20, 15);
        FillRectangle(pixels, 80, 60, 240, 180, 60, 20);

        foreach (var (x, y) in BoltCentres)
        {
            DrawNoisyCircle(pixels, x, y, radius: 6, minimum: 140, variation: 30);
        }

        return CreateBitmap(pixels);
    }

    private static readonly (int X, int Y)[] BoltCentres =
    [
        (120, 90),
        (200, 90),
        (120, 150),
        (200, 150),
    ];

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
            SetPixel(pixels, x, centreY, blue: 80, green: 160, red: 0);
        }

        for (var y = 0; y < Height; y++)
        {
            SetPixel(pixels, centreX, y, blue: 80, green: 160, red: 0);
        }
    }

    private static void DrawNoisyCircle(
        byte[] pixels,
        int centreX,
        int centreY,
        int radius,
        byte minimum,
        int variation)
    {
        var radiusSquared = radius * radius;
        for (var offsetY = -radius; offsetY <= radius; offsetY++)
        {
            for (var offsetX = -radius; offsetX <= radius; offsetX++)
            {
                if ((offsetX * offsetX) + (offsetY * offsetY) > radiusSquared)
                {
                    continue;
                }

                var x = centreX + offsetX;
                var y = centreY + offsetY;
                if (x is < 0 or >= Width || y is < 0 or >= Height)
                {
                    continue;
                }

                var value = (byte)(minimum + Random.Shared.Next(variation));
                SetPixel(pixels, x, y, value, value, value);
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

    private static ImageFrame CreateBitmap(byte[] pixels) =>
        new(Width, Height, Width * BytesPerPixel, pixels);
}
