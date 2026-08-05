namespace IBTM.Core;

public sealed record ImageFrame(
    int Width,
    int Height,
    int Stride,
    byte[] Pixels);
