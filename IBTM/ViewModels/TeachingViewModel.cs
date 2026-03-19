using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Device;
using IBTM.Localization;
using IBTM.Models;
using IBTM.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace IBTM.ViewModels;

/// <summary>
/// 티칭 ViewModel — Zone 3 카메라 라이브뷰 + 조그 + 볼트 위치 티칭
/// 모델 변경 시 사용, 레시피 JSON 저장/로드
/// </summary>
public partial class TeachingViewModel : ObservableObject
{
    private readonly IMotionService _zone1Motion;
    private readonly IMotionService _zone2Motion;
    private readonly IMotionService _zone3Motion;
    private readonly IIOService _ioService;
    private readonly ICameraStreamService _cameraService;
    private readonly RecipeService _recipeService;
    private readonly MachineConfig _machineConfig;
    private readonly DispatcherTimer _posTimer;

    // ── 레시피 ──────────────────────────────────────────────────────
    [ObservableProperty] private Recipe _currentRecipe = new();
    [ObservableProperty] private string _recipeName = "Default";

    // ── Zone 선택 ───────────────────────────────────────────────────
    [ObservableProperty] private int _selectedZone = 3;

    // ── 현재 좌표 (폴링) ────────────────────────────────────────────
    [ObservableProperty] private double _currentX;
    [ObservableProperty] private double _currentY;
    [ObservableProperty] private double _currentZ;

    // ── 조그 ────────────────────────────────────────────────────────
    [ObservableProperty] private int _jogSpeedIndex = 1;
    private readonly double[] _jogSpeeds = [1.0, 10.0, 50.0];
    public double JogSpeed => _jogSpeeds[JogSpeedIndex];

    // ── 카메라 라이브 뷰 ────────────────────────────────────────────
    [ObservableProperty] private ImageSource? _liveImage;
    [ObservableProperty] private bool _isCameraLive;

    // ── 레이저 ──────────────────────────────────────────────────────
    [ObservableProperty] private bool _laserOn;

    // ── 티칭 포인트 ─────────────────────────────────────────────────
    public ObservableCollection<TeachingPoint> TeachingPoints { get; } = [];
    [ObservableProperty] private TeachingPoint? _selectedPoint;

    // ── 레시피 파일 목록 ────────────────────────────────────────────
    public ObservableCollection<string> RecipeFiles { get; } = [];

    // ── 상태 메시지 ─────────────────────────────────────────────────
    [ObservableProperty] private string _statusMessage = "";

    public TeachingViewModel(
        [FromKeyedServices("zone1")] IMotionService zone1,
        [FromKeyedServices("zone2")] IMotionService zone2,
        [FromKeyedServices("zone3")] IMotionService zone3,
        IIOService ioService,
        ICameraStreamService cameraService,
        RecipeService recipeService,
        MachineConfig machineConfig)
    {
        _zone1Motion = zone1;
        _zone2Motion = zone2;
        _zone3Motion = zone3;
        _ioService = ioService;
        _cameraService = cameraService;
        _recipeService = recipeService;
        _machineConfig = machineConfig;

        _cameraService.FrameReady += img =>
            Application.Current.Dispatcher.Invoke(() => LiveImage = img);

        _posTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _posTimer.Tick += (_, _) => PollPosition();

        BuildTeachingPoints();
        RefreshRecipeFiles();
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

    // ── Zone 변경 ───────────────────────────────────────────────────

    partial void OnSelectedZoneChanged(int value)
    {
        if (LaserOn) ToggleLaser();
        CurrentMotion.Stop();

        // Zone 3 아니면 카메라 끄기
        if (value != 3 && IsCameraLive)
            ToggleLiveView();
    }

    // ── 조그 ────────────────────────────────────────────────────────

    [RelayCommand] private void JogXPlus() => CurrentMotion.MoveX(JogSpeed);
    [RelayCommand] private void JogXMinus() => CurrentMotion.MoveX(-JogSpeed);
    [RelayCommand] private void JogYPlus() => CurrentMotion.MoveY(JogSpeed);
    [RelayCommand] private void JogYMinus() => CurrentMotion.MoveY(-JogSpeed);
    [RelayCommand] private void JogZPlus() => CurrentMotion.MoveZ(JogSpeed);
    [RelayCommand] private void JogZMinus() => CurrentMotion.MoveZ(-JogSpeed);
    [RelayCommand] private void JogStop() => CurrentMotion.Stop();

    // ── 카메라 ──────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleLiveView()
    {
        if (IsCameraLive)
        {
            _cameraService.StopLiveView();
            IsCameraLive = false;
            LiveImage = null;
        }
        else
        {
            _cameraService.StartLiveView();
            IsCameraLive = true;
        }
    }

    /// <summary>카메라 이미지 클릭 → 픽셀→mm 변환 → 이동</summary>
    [RelayCommand]
    private async Task CameraClickAsync(Point clickPos)
    {
        if (SelectedZone != 3) return;

        // 이미지 중심 기준 픽셀 오프셋
        double offsetPx = clickPos.X - _cameraService.ImageWidth / 2.0;
        double offsetPy = clickPos.Y - _cameraService.ImageHeight / 2.0;

        // mm 변환
        double offsetMmX = offsetPx / _machineConfig.PixelsPerMm;
        double offsetMmY = offsetPy / _machineConfig.PixelsPerMm;

        // 현재 위치 + 오프셋으로 이동
        double targetX = CurrentX + offsetMmX;
        double targetY = CurrentY + offsetMmY;

        await _zone3Motion.MoveXY(targetX, targetY, 50.0);
        StatusMessage = $"Move → X:{targetX:F3} Y:{targetY:F3}";
    }

    // ── 레이저 ──────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleLaser()
    {
        LaserOn = !LaserOn;
        _ioService.Set(LaserIoIndex, LaserOn);
    }

    // ── 티칭 ────────────────────────────────────────────────────────

    [RelayCommand]
    private void TeachCurrentPosition()
    {
        if (SelectedPoint == null) return;

        CurrentMotion.GetPotision(out var x, out var y, out var z);
        SelectedPoint.Teach(x ?? 0, y ?? 0, z ?? 0);
        ApplyPointToRecipe(SelectedPoint);
        StatusMessage = Loc.S("Teach_Recorded", SelectedPoint.Name);
    }

    [RelayCommand]
    private async Task MoveToPointAsync()
    {
        if (SelectedPoint == null || !SelectedPoint.IsTaught) return;

        var motion = SelectedPoint.Zone switch
        {
            1 => _zone1Motion,
            2 => _zone2Motion,
            _ => _zone3Motion,
        };

        await motion.MoveZ(0, 80.0); // Z 안전 높이
        await motion.MoveXY(SelectedPoint.X, SelectedPoint.Y, 50.0);
        await motion.MoveZ(SelectedPoint.Z, 30.0);
        StatusMessage = Loc.S("Teach_MovedTo", SelectedPoint.Name);
    }

    [RelayCommand]
    private void AddBoltPoint()
    {
        int idx = CurrentRecipe.BoltPoints.Count + 1;
        var bp = new BoltPoint { Name = $"B{idx}", TargetTorqueNm = 15.0 };
        CurrentRecipe.BoltPoints.Add(bp);

        TeachingPoints.Add(new TeachingPoint
        {
            Name = bp.Name,
            Category = "Zone2_Bolt",
            Zone = 2,
            TargetTorqueNm = bp.TargetTorqueNm,
        });
        StatusMessage = $"Added {bp.Name}";
    }

    [RelayCommand]
    private void RemoveBoltPoint()
    {
        if (SelectedPoint is not { Category: "Zone2_Bolt" }) return;

        var bpName = SelectedPoint.Name;
        CurrentRecipe.BoltPoints.RemoveAll(b => b.Name == bpName);
        TeachingPoints.Remove(SelectedPoint);
        SelectedPoint = null;
        StatusMessage = $"Removed {bpName}";
    }

    // ── 레시피 저장/로드 ────────────────────────────────────────────

    [RelayCommand]
    private async Task SaveRecipeAsync()
    {
        CurrentRecipe.Name = RecipeName;
        await _recipeService.SaveRecipeAsync(CurrentRecipe);
        RefreshRecipeFiles();
        StatusMessage = Loc.S("Teach_RecipeSaved", RecipeName);
    }

    [RelayCommand]
    private async Task LoadRecipeAsync(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;

        CurrentRecipe = await _recipeService.LoadRecipeAsync(filePath);
        RecipeName = CurrentRecipe.Name;
        BuildTeachingPoints();
        StatusMessage = Loc.S("Teach_RecipeLoaded", RecipeName);
    }

    [RelayCommand]
    private void NewRecipe()
    {
        CurrentRecipe = new Recipe();
        RecipeName = "New";
        BuildTeachingPoints();
        StatusMessage = Loc.S("Teach_NewRecipe");
    }

    // ── 티칭 포인트 빌드 ────────────────────────────────────────────

    private void BuildTeachingPoints()
    {
        TeachingPoints.Clear();
        var r = CurrentRecipe;

        // Zone 1 — 픽/플레이스
        AddPoint("PickPos1", "Zone1_Pick", 1, r.Zone1_PickPos1);
        AddPoint("PickPos2", "Zone1_Pick", 1, r.Zone1_PickPos2);
        AddPoint("PlacePos1", "Zone1_Place", 1, r.Zone1_PlacePos1);
        AddPoint("PlacePos2", "Zone1_Place", 1, r.Zone1_PlacePos2);

        // Zone 2 — 피듀셜 + 볼트
        AddPoint("Fiducial", "Zone2_Fiducial", 2, r.Zone2_FiducialPos);
        foreach (var bp in r.BoltPoints)
            TeachingPoints.Add(new TeachingPoint
            {
                Name = bp.Name,
                Category = "Zone2_Bolt",
                Zone = 2,
                X = bp.X, Y = bp.Y, Z = bp.Z,
                IsTaught = bp.X != 0 || bp.Y != 0,
                TargetTorqueNm = bp.TargetTorqueNm,
            });

        // Zone 3 — 검사
        AddPoint("InspectPos", "Zone3_Inspect", 3, r.Zone3_InspectPos);
    }

    private void AddPoint(string name, string category, int zone, AxisPos pos)
    {
        TeachingPoints.Add(new TeachingPoint
        {
            Name = name,
            Category = category,
            Zone = zone,
            X = pos.X, Y = pos.Y, Z = pos.Z,
            IsTaught = pos.X != 0 || pos.Y != 0,
        });
    }

    /// <summary>티칭 포인트 좌표를 레시피에 반영</summary>
    private void ApplyPointToRecipe(TeachingPoint pt)
    {
        var pos = new AxisPos { X = pt.X, Y = pt.Y, Z = pt.Z };

        // Zone 3 기준 좌표 → 다른 Zone 오프셋 적용
        switch (pt.Category)
        {
            case "Zone1_Pick":
                if (pt.Name == "PickPos1") CurrentRecipe.Zone1_PickPos1 = pos;
                else CurrentRecipe.Zone1_PickPos2 = pos;
                break;
            case "Zone1_Place":
                if (pt.Name == "PlacePos1") CurrentRecipe.Zone1_PlacePos1 = pos;
                else CurrentRecipe.Zone1_PlacePos2 = pos;
                break;
            case "Zone2_Fiducial":
                CurrentRecipe.Zone2_FiducialPos = pos;
                break;
            case "Zone2_Bolt":
                var bp = CurrentRecipe.BoltPoints.FirstOrDefault(b => b.Name == pt.Name);
                if (bp != null) { bp.X = pt.X; bp.Y = pt.Y; bp.Z = pt.Z; }
                break;
            case "Zone3_Inspect":
                CurrentRecipe.Zone3_InspectPos = pos;
                break;
        }
    }

    private void RefreshRecipeFiles()
    {
        RecipeFiles.Clear();
        foreach (var f in _recipeService.GetRecipeFiles())
            RecipeFiles.Add(f);
    }

    // ── 헬퍼 ────────────────────────────────────────────────────────

    private void PollPosition()
    {
        CurrentMotion.GetPotision(out var x, out var y, out var z);
        CurrentX = x ?? 0;
        CurrentY = y ?? 0;
        CurrentZ = z ?? 0;
    }

    public void StartPolling() => _posTimer.Start();

    public void StopPolling()
    {
        _posTimer.Stop();
        if (IsCameraLive) ToggleLiveView();
        if (LaserOn)
        {
            _ioService.Set(LaserIoIndex, false);
            LaserOn = false;
        }
    }
}
