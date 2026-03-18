using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.Localization;
using IBTM.Models;
using System.Timers;

namespace IBTM.ViewModels;

public partial class StageCardViewModel : ObservableObject
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _subtitle = string.Empty;
    [ObservableProperty] private string _info1 = string.Empty;
    [ObservableProperty] private string _info2 = string.Empty;
    [ObservableProperty] private bool _isLastCard;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private StageStatus _status = StageStatus.Idle;

    [ObservableProperty] private double _indicatorOpacity = 1.0;

    // 스테이지 소요시간
    [ObservableProperty] private string _elapsedText = string.Empty;
    private DateTime _startedAt;

    public ProcessStage Stage { get; init; }
    public string Icon { get; init; } = string.Empty;

    public string StatusText => Status switch
    {
        StageStatus.Running => Loc.S("Card_Running"),
        StageStatus.Done    => Loc.S("Card_Done"),
        StageStatus.Error   => Loc.S("Card_Error"),
        StageStatus.Warning => Loc.S("Card_Warning"),
        StageStatus.Skipped => Loc.S("Card_Skipped"),
        _                   => Loc.S("Card_Idle")
    };

    private System.Timers.Timer? _pulseTimer;
    private double _pulseDirection = -0.05;

    partial void OnStatusChanged(StageStatus value)
    {
        if (value == StageStatus.Running)
        {
            _startedAt = DateTime.Now;
            ElapsedText = string.Empty;
            StartPulse();
        }
        else
        {
            StopPulse();
            if (value == StageStatus.Done || value == StageStatus.Error || value == StageStatus.Warning)
            {
                var elapsed = (DateTime.Now - _startedAt).TotalSeconds;
                ElapsedText = elapsed >= 10 ? $"{elapsed:F0}s" : $"{elapsed:F1}s";
            }
        }
    }

    private void StartPulse()
    {
        StopPulse();
        IndicatorOpacity = 1.0;
        _pulseDirection = -0.05;
        _pulseTimer = new System.Timers.Timer(40);
        _pulseTimer.Elapsed += OnPulseTick;
        _pulseTimer.Start();
    }

    private void OnPulseTick(object? sender, ElapsedEventArgs e)
    {
        IndicatorOpacity += _pulseDirection;
        if (IndicatorOpacity <= 0.25) _pulseDirection = 0.05;
        else if (IndicatorOpacity >= 1.0) _pulseDirection = -0.05;
    }

    private void StopPulse()
    {
        _pulseTimer?.Stop();
        _pulseTimer?.Dispose();
        _pulseTimer = null;
        IndicatorOpacity = 1.0;
    }
}
