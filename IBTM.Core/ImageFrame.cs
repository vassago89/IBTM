namespace IBTM.Core;

public sealed record ImageFrame(
    int Width,
    int Height,
    int Stride,
    byte[] Pixels)
{
    public const int ColorChannelCount = 3;
    public const int BlueChannel = 0;
    public const int GreenChannel = 1;
    public const int RedChannel = 2;
}
