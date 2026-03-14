using CommunityToolkit.Mvvm.ComponentModel;
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

    public ProcessStage Stage { get; init; }
    public string Icon { get; init; } = string.Empty;

    public string StatusText => Status switch
    {
        StageStatus.Running => "실행 중",
        StageStatus.Done    => "완료",
        StageStatus.Error   => "오류",
        StageStatus.Warning => "경고",
        _                   => "대기"
    };

    private System.Timers.Timer? _pulseTimer;
    private double _pulseDirection = -0.05;

    partial void OnStatusChanged(StageStatus value)
    {
        if (value == StageStatus.Running)
            StartPulse();
        else
            StopPulse();
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
