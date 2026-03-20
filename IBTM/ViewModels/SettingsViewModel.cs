using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Device;
using IBTM.Localization;
using IBTM.Models;
using IBTM.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Windows.Threading;

namespace IBTM.ViewModels;

/// <summary>
/// 장비 설정 ViewModel — 탭 기반 전체 설정
/// 캘리브레이션, 모션, 볼트, 비전, IO, 시스템 설정
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IMotionService _zone1Motion;
    private readonly IMotionService _zone2Motion;
    private readonly IMotionService _zone3Motion;
    private readonly IIOService _ioService;
    private readonly RecipeService _recipeService;
    private readonly DispatcherTimer _posTimer;

    // ── 장비 설정 ───────────────────────────────────────────────────
    [ObservableProperty] private MachineConfig _config = new();

    // ── 탭 선택 ─────────────────────────────────────────────────────
    [ObservableProperty] private int _selectedTab;

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
    private readonly double[] _jogSpeeds = [1.0, 10.0, 50.0];
    public double JogSpeed => _jogSpeeds[JogSpeedIndex];

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
        [FromKeyedServices("zone1")] IMotionService zone1,
        [FromKeyedServices("zone2")] IMotionService zone2,
        [FromKeyedServices("zone3")] IMotionService zone3,
        IIOService ioService,
        RecipeService recipeService)
    {
        _zone1Motion = zone1;
        _zone2Motion = zone2;
        _zone3Motion = zone3;
        _ioService = ioService;
        _recipeService = recipeService;

        // 좌표 폴링 타이머 (100ms)
        _posTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _posTimer.Tick += (_, _) => PollPosition();
    }

    private IMotionService CurrentMotion => SelectedZone switch
    {
        1 => _zone1Motion,
        2 => _zone2Motion,
        3 => _zone3Motion,
        _ => _zone3Motion,
    };

    private int LaserIoIndex => SelectedZone switch
    {
        1 => IoMap.Zone1_Laser,
        2 => IoMap.Zone2_Laser,
        3 => IoMap.Zone3_Laser,
        _ => IoMap.Zone3_Laser,
    };

    // ── 라이프사이클 ────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadConfigAsync()
    {
        Config = await _recipeService.LoadConfigAsync();
        UpdateRefStatus();
        UpdateOffsetText();
        SyncMotionParams();
        IsKorean = Config.Language == "ko";
        _posTimer.Start();
        StatusMessage = Loc.S("Settings_Loaded");
    }

    [RelayCommand]
    private async Task SaveConfigAsync()
    {
        await _recipeService.SaveConfigAsync(Config);
        StatusMessage = Loc.S("Settings_Saved");
    }

    // ── Zone 선택 ───────────────────────────────────────────────────

    partial void OnSelectedZoneChanged(int value)
    {
        // Zone 변경 시 레이저 끄기
        if (LaserOn) ToggleLaser();
        CurrentMotion.Stop();
        SyncMotionParams();
    }

    // ── 모션 탭: Zone 모션 파라미터 동기화 ───────────────────────────

    private void SyncMotionParams()
    {
        CurrentMotionParams = Config.GetMotionParams(SelectedZone);
        OnPropertyChanged(nameof(CurrentMotionParams));
    }

    // ── 조그 ────────────────────────────────────────────────────────

    [RelayCommand] private void JogXPlus() => CurrentMotion.MoveX(JogSpeed);
    [RelayCommand] private void JogXMinus() => CurrentMotion.MoveX(-JogSpeed);
    [RelayCommand] private void JogYPlus() => CurrentMotion.MoveY(JogSpeed);
    [RelayCommand] private void JogYMinus() => CurrentMotion.MoveY(-JogSpeed);
    [RelayCommand] private void JogZPlus() => CurrentMotion.MoveZ(JogSpeed);
    [RelayCommand] private void JogZMinus() => CurrentMotion.MoveZ(-JogSpeed);
    [RelayCommand] private void JogStop() => CurrentMotion.Stop();

    // ── 레이저 ──────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleLaser()
    {
        LaserOn = !LaserOn;
        _ioService.Set(LaserIoIndex, LaserOn);
    }

    // ── 캘리브레이션: 기준점 기록 ───────────────────────────────────

    [RelayCommand]
    private void RecordRef()
    {
        CurrentMotion.GetPotision(out var x, out var y, out var z);
        var pos = new AxisPos { X = x ?? 0, Y = y ?? 0, Z = z ?? 0 };

        switch (SelectedZone)
        {
            case 1: Config.Zone1Ref = pos; Zone1RefRecorded = true; break;
            case 2: Config.Zone2Ref = pos; Zone2RefRecorded = true; break;
            case 3: Config.Zone3Ref = pos; Zone3RefRecorded = true; break;
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

        Config.ComputeOffsets();
        UpdateOffsetText();
        StatusMessage = Loc.S("Settings_OffsetsComputed");
    }

    // ── 시스템: 언어 전환 ───────────────────────────────────────────

    [RelayCommand]
    private void ToggleLanguage()
    {
        Loc.Instance.ToggleLanguage();
        IsKorean = !IsKorean;
        Config.Language = IsKorean ? "ko" : "en";
        StatusMessage = Loc.S("Settings_LangChanged");
    }

    // ── 시스템: 로그 레벨 변경 ──────────────────────────────────────

    partial void OnSelectedTabChanged(int value)
    {
        // 탭 변경 시 모션 파라미터 동기화
        if (value == 1) SyncMotionParams();
    }

    // ── 헬퍼 ────────────────────────────────────────────────────────

    private void PollPosition()
    {
        CurrentMotion.GetPotision(out var x, out var y, out var z);
        CurrentX = x ?? 0;
        CurrentY = y ?? 0;
        CurrentZ = z ?? 0;
    }

    private void UpdateRefStatus()
    {
        Zone1RefRecorded = Config.Zone1Ref.X != 0 || Config.Zone1Ref.Y != 0;
        Zone2RefRecorded = Config.Zone2Ref.X != 0 || Config.Zone2Ref.Y != 0;
        Zone3RefRecorded = Config.Zone3Ref.X != 0 || Config.Zone3Ref.Y != 0;
    }

    private void UpdateOffsetText()
    {
        OffsetResultText = $"3→1: dX={Config.Offset3To1.X:F3} dY={Config.Offset3To1.Y:F3}  " +
                           $"3→2: dX={Config.Offset3To2.X:F3} dY={Config.Offset3To2.Y:F3}";
    }

    public void StopPolling()
    {
        _posTimer.Stop();
        if (LaserOn)
        {
            _ioService.Set(LaserIoIndex, false);
            LaserOn = false;
        }
    }

    public void StartPolling() => _posTimer.Start();
}
