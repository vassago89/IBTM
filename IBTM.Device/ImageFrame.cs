namespace IBTM.Device;

public sealed record ImageFrame(
    int Width,
    int Height,
    int Stride,
    byte[] Pixels);
