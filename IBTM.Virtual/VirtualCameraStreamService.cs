using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.Virtual;

public sealed class VirtualCameraStreamService(bool inspection)
    : ICameraStreamService, IDisposable
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(66);

    private CancellationTokenSource? _stream;

    public event Action<ImageFrame>? FrameReady;

    public int ImageWidth => VirtualImageFactory.Width;
    public int ImageHeight => VirtualImageFactory.Height;
    public int CaptureCount { get; private set; }
    public bool FiducialPresent { get; set; } = true;

    public void Initialize()
    {
    }

    public ImageFrame Capture()
    {
        CaptureCount++;
        return inspection
            ? VirtualImageFactory.CreateInspectionFrame()
            : FiducialPresent
                ? VirtualImageFactory.CreateCameraFrame()
                : VirtualImageFactory.CreateEmptyCameraFrame();
    }

    public void StartLiveView()
    {
        if (_stream is not null)
        {
            return;
        }

        _stream = new CancellationTokenSource();
        _ = StreamAsync(_stream);
    }

    public void StopLiveView()
    {
        _stream?.Cancel();
        _stream = null;
    }

    public void Dispose() => StopLiveView();

    private async Task StreamAsync(CancellationTokenSource stream)
    {
        try
        {
            while (true)
            {
                FrameReady?.Invoke(Capture());
                await Task.Delay(FrameInterval, stream.Token);
            }
        }
        catch (OperationCanceledException) when (stream.IsCancellationRequested)
        {
        }
        finally
        {
            stream.Dispose();
        }
    }
}
