namespace IBTM.Infrastructure.Simulation;

/// <summary>Fifteen-frame-per-second camera simulator for teaching screens.</summary>
public sealed class VirtualCameraStreamService : ICameraStreamService, IDisposable
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(66);

    private Timer? _timer;
    private bool _isLive;

    public event Action<ImageFrame>? FrameReady;

    public bool IsLive => _isLive;
    public int ImageWidth => SimulatedImageFactory.Width;
    public int ImageHeight => SimulatedImageFactory.Height;

    public void StartLiveView()
    {
        if (IsLive)
        {
            return;
        }

        _timer ??= new Timer(OnTimerTick);
        _timer.Change(TimeSpan.Zero, FrameInterval);
        _isLive = true;
    }

    public void StopLiveView()
    {
        _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _isLive = false;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        _isLive = false;
    }

    private void OnTimerTick(object? state) =>
        FrameReady?.Invoke(SimulatedImageFactory.CreateCameraFrame(drawCrosshair: true));
}
