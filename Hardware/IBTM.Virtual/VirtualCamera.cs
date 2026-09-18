using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Virtual;

public sealed record VirtualDataMatrix(AxisPosition Center, double Width, double Height, string Text);

public sealed class VirtualCamera : ICamera
{
    private readonly Func<(double X, double Y, double Z)> _getPosition;
    private readonly Func<IEnumerable<AxisPosition>> _getBoltPositions;
    private readonly Func<IEnumerable<VirtualDataMatrix>>? _getDataMatrices;

    public VirtualCamera(
        Func<(double X, double Y, double Z)> getPosition,
        Func<IEnumerable<AxisPosition>> getBoltPositions,
        Func<IEnumerable<VirtualDataMatrix>>? getDataMatrices = null)
    {
        _getPosition = getPosition;
        _getBoltPositions = getBoltPositions;
        _getDataMatrices = getDataMatrices;
        BoltsPresent = true;
    }

    public event Action<ImageFrame>? FrameReady;
    public event Action<Exception>? LiveViewFailed;

    public bool IsLiveView { get; private set; }
    public ImageFrame? SourceImage { get; set; }

    public (int Width, int Height) FrameSize
    {
        get
        {
            return SourceImage is { } image
                ? (image.Width, image.Height)
                : (VirtualImageFactory.Width, VirtualImageFactory.Height);
        }
    }

    public bool BoltsPresent { get; set; }

    public void Initialize()
    {
        StopLiveView();
    }

    public async Task<ImageFrame> CaptureAsync(
        double exposureMicroseconds,
        double gain,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(CreateFrame, cancellationToken).ConfigureAwait(false);
    }

    private ImageFrame CreateFrame()
    {
        return SourceImage ?? VirtualImageFactory.CreateInspection(
            _getPosition(),
            BoltsPresent ? _getBoltPositions() : [],
            _getDataMatrices?.Invoke() ?? []);
    }

    public void StartLiveView(double exposureMicroseconds, double gain)
    {
        IsLiveView = true;
        try
        {
            FrameReady?.Invoke(CreateFrame());
        }
        catch (Exception exception)
        {
            IsLiveView = false;
            LiveViewFailed?.Invoke(exception);
        }
    }

    public void StopLiveView()
    {
        IsLiveView = false;
    }
}
