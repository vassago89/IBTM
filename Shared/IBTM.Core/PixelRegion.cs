using System;

namespace IBTM.Core;

public sealed record PixelRegion(int X, int Y, int Width, int Height)
{
    public static PixelRegion CenteredSquare(int imageWidth, int imageHeight, int size)
    {
        size = Math.Clamp(size, 1, Math.Min(imageWidth, imageHeight));
        return new((imageWidth - size) / 2, (imageHeight - size) / 2, size, size);
    }

    public bool IsInside(int imageWidth, int imageHeight)
    {
        return X >= 0
            && Y >= 0
            && Width > 0
            && Height > 0
            && Width <= imageWidth
            && Height <= imageHeight
            && X <= imageWidth - Width
            && Y <= imageHeight - Height;
    }
}
