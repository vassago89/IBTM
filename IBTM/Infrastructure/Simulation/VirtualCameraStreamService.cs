using System.Windows.Media;
using System.Windows.Threading;

namespace IBTM.Infrastructure.Simulation;

/// <summary>Fifteen-frame-per-second camera simulator for teaching screens.</summary>
public sealed class VirtualCameraStreamService : ICameraStreamService, IDisposable
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(66);

    private DispatcherTimer? _timer;

    public event Action<ImageSource>? FrameReady;

    public bool IsLive => _timer?.IsEnabled == true;
    public int ImageWidth => SimulatedImageFactory.Width;
    public int ImageHeight => SimulatedImageFactory.Height;

    public void StartLiveView()
    {
        if (IsLive)
        {
            return;
        }

        _timer ??= CreateTimer();
        _timer.Start();
    }

    public void StopLiveView()
    {
        _timer?.Stop();
    }

    public void Dispose()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _timer = null;
    }

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer { Interval = FrameInterval };
        timer.Tick += OnTimerTick;
        return timer;
    }

    private void OnTimerTick(object? sender, EventArgs e) =>
        FrameReady?.Invoke(SimulatedImageFactory.CreateCameraFrame(drawCrosshair: true));
}
