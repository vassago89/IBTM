namespace IBTM.Core;

public sealed record PixelRegion(int X, int Y, int Width, int Height)
{
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
