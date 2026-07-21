using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM.Presentation.ViewModels;

/// <summary>
/// 장비 설정 ViewModel — 탭 기반 전체 설정
/// 캘리브레이션, 모션, 볼트, 비전, IO, 시스템 설정
/// </summary>
public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly IMotionService[] _motions;
    private readonly IIOService _ioService;
    private readonly RecipeService _recipeService;

    // ── 장비 설정 ───────────────────────────────────────────────────
    [ObservableProperty] private MachineConfig _config;

    // ── Zone 선택 (캘리브레이션/모션 탭 공용) ─────────────────────────
    [ObservableProperty] private int _selectedZone = 1;

    // ── 모션 탭: 선택된 Zone의 파라미터 ──────────────────────────────
    [ObservableProperty] private ZoneMotionParams _currentMotionParams = new();

    // ── 현재 좌표 (폴링) ────────────────────────────────────────────
    [ObservableProperty] private double _currentX;
    [ObservableProperty] private double _currentY;
    [ObservableProperty] private double _currentZ;

    // ── 조그 ────────────────────────────────────────────────────────
    [ObservableProperty] private int _jogSpeedIndex = 1;
    private static readonly double[] JogSpeeds = [1.0, 10.0, 50.0];
    private static readonly int[] LaserChannels =
        [PcbPlacementChannels.Laser, BoltFasteningChannels.Laser, InspectionChannels.Laser];
    public double JogSpeed => JogSpeeds[JogSpeedIndex];

    // ── 레이저 ──────────────────────────────────────────────────────
    [ObservableProperty] private bool _laserOn;

    // ── 기준점 기록 상태 ────────────────────────────────────────────
    [ObservableProperty] private bool _zone1RefRecorded;
    [ObservableProperty] private bool _zone2RefRecorded;
    [ObservableProperty] private bool _zone3RefRecorded;

    // ── 오프셋 결과 텍스트 ──────────────────────────────────────────
    [ObservableProperty] private string _offsetResultText = "──";

    // ── 상태 메시지 ─────────────────────────────────────────────────
    [ObservableProperty] private string _statusMessage = "";

    // ── 시스템: 언어 선택 ───────────────────────────────────────────
    [ObservableProperty] private bool _isKorean;

    public SettingsViewModel(
        [FromKeyedServices(PcbPlacementModule.ServiceKey)] IMotionService zone1,
        [FromKeyedServices(BoltFasteningModule.ServiceKey)] IMotionService zone2,
        [FromKeyedServices(InspectionModule.ServiceKey)] IMotionService zone3,
        IIOService ioService,
        RecipeService recipeService,
        MachineConfig machineConfig)
    {
        _motions = [zone1, zone2, zone3];
        _ioService = ioService;
        _recipeService = recipeService;
        _config = machineConfig;
        zone1.PositionChanged += OnZone1PositionChanged;
        zone2.PositionChanged += OnZone2PositionChanged;
        zone3.PositionChanged += OnZone3PositionChanged;
    }

    private IMotionService CurrentMotion => GetMotion(SelectedZone);

    private IMotionService GetMotion(int zone) => _motions[zone - 1];

    private int LaserIoIndex => GetLaserChannel(SelectedZone);

    private static int GetLaserChannel(int zone) => LaserChannels[zone - 1];

    // ── 라이프사이클 ────────────────────────────────────────────────

    public void Activate()
    {
        OnPropertyChanged(nameof(Config));
        UpdateRefStatus();
        UpdateOffsetText();
        SyncMotionParams();
        IsKorean = Loc.Instance.Language == "KO";
        RefreshPosition();
        StatusMessage = Loc.S("Settings_Loaded");
    }

    [RelayCommand]
    private async Task SaveConfigAsync()
    {
        try
        {
            await _recipeService.SaveConfigAsync(Config);
            StatusMessage = Loc.S("Settings_Saved");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Config save failed: {exception.Message}";
        }
    }

    // ── Zone 선택 ───────────────────────────────────────────────────

    partial void OnSelectedZoneChanged(int oldValue, int newValue)
    {
        if (LaserOn)
        {
            _ioService.SetOutput(GetLaserChannel(oldValue), false);
            LaserOn = false;
        }
        GetMotion(oldValue).Stop();
        SyncMotionParams();
        RefreshPosition();
    }

    // ── 모션 탭: Zone 모션 파라미터 동기화 ───────────────────────────

    private void SyncMotionParams()
    {
        CurrentMotionParams = Config.GetMotionParams(SelectedZone);
        OnPropertyChanged(nameof(CurrentMotionParams));
    }

    // ── 조그 ────────────────────────────────────────────────────────

    [RelayCommand] private void JogXPlus() => CurrentMotion.JogX(JogSpeed);
    [RelayCommand] private void JogXMinus() => CurrentMotion.JogX(-JogSpeed);
    [RelayCommand] private void JogYPlus() => CurrentMotion.JogY(JogSpeed);
    [RelayCommand] private void JogYMinus() => CurrentMotion.JogY(-JogSpeed);
    [RelayCommand] private void JogZPlus() => CurrentMotion.JogZ(JogSpeed);
    [RelayCommand] private void JogZMinus() => CurrentMotion.JogZ(-JogSpeed);
    [RelayCommand] private void JogStop() => CurrentMotion.Stop();

    // ── 레이저 ──────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleLaser()
    {
        LaserOn = !LaserOn;
        _ioService.SetOutput(LaserIoIndex, LaserOn);
    }

    // ── 캘리브레이션: 기준점 기록 ───────────────────────────────────

    [RelayCommand]
    private void RecordRef()
    {
        var current = CurrentMotion.GetPosition();
        var pos = new AxisPos
        {
            X = current.X!.Value,
            Y = current.Y!.Value,
            Z = current.Z!.Value,
        };

        switch (SelectedZone)
        {
            case 1: Config.Calibration.Zone1Ref = pos; Zone1RefRecorded = true; break;
            case 2: Config.Calibration.Zone2Ref = pos; Zone2RefRecorded = true; break;
            case 3: Config.Calibration.Zone3Ref = pos; Zone3RefRecorded = true; break;
            default: throw new ArgumentOutOfRangeException(nameof(SelectedZone));
        }

        StatusMessage = Loc.S("Settings_RefRecorded", SelectedZone);
    }

    [RelayCommand]
    private void ComputeOffsets()
    {
        if (!Zone1RefRecorded || !Zone2RefRecorded || !Zone3RefRecorded)
        {
            StatusMessage = Loc.S("Settings_NeedAllRefs");
            return;
        }

        Config.Calibration.ComputeOffsets();
        UpdateOffsetText();
        StatusMessage = Loc.S("Settings_OffsetsComputed");
    }

    // ── 시스템: 언어 전환 ───────────────────────────────────────────

    [RelayCommand]
    private void ToggleLanguage()
    {
        Loc.Instance.ToggleLanguage();
        IsKorean = Loc.Instance.Language == "KO";
        Config.System.Language = IsKorean ? "ko" : "en";
        StatusMessage = Loc.S("Settings_LangChanged");
    }

    // ── 헬퍼 ────────────────────────────────────────────────────────

    private void RefreshPosition()
    {
        var current = CurrentMotion.GetPosition();
        CurrentX = current.X!.Value;
        CurrentY = current.Y!.Value;
        CurrentZ = current.Z!.Value;
    }

    private void UpdateRefStatus()
    {
        Zone1RefRecorded = Config.Calibration.Zone1Ref.X != 0 || Config.Calibration.Zone1Ref.Y != 0;
        Zone2RefRecorded = Config.Calibration.Zone2Ref.X != 0 || Config.Calibration.Zone2Ref.Y != 0;
        Zone3RefRecorded = Config.Calibration.Zone3Ref.X != 0 || Config.Calibration.Zone3Ref.Y != 0;
    }

    private void UpdateOffsetText()
    {
        OffsetResultText = $"3→1: dX={Config.Calibration.Offset3To1.X:F3} dY={Config.Calibration.Offset3To1.Y:F3}  " +
                           $"3→2: dX={Config.Calibration.Offset3To2.X:F3} dY={Config.Calibration.Offset3To2.Y:F3}";
    }

    public void Deactivate()
    {
        if (LaserOn)
        {
            _ioService.SetOutput(LaserIoIndex, false);
            LaserOn = false;
        }
    }

    public void Dispose()
    {
        Deactivate();
        _motions[0].PositionChanged -= OnZone1PositionChanged;
        _motions[1].PositionChanged -= OnZone2PositionChanged;
        _motions[2].PositionChanged -= OnZone3PositionChanged;
    }

    private void OnZone1PositionChanged(object? sender, MotionPositionEventArgs position) =>
        ApplyPosition(1, position);

    private void OnZone2PositionChanged(object? sender, MotionPositionEventArgs position) =>
        ApplyPosition(2, position);

    private void OnZone3PositionChanged(object? sender, MotionPositionEventArgs position) =>
        ApplyPosition(3, position);

    private void ApplyPosition(int zone, MotionPositionEventArgs position) =>
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (SelectedZone != zone)
            {
                return;
            }

            CurrentX = position.X;
            CurrentY = position.Y;
            CurrentZ = position.Z;
        });
}
