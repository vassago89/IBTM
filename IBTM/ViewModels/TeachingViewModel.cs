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
/// 티칭 ViewModel — Zone별 분리 티칭 + Zone 3 오프셋 기반 XY 자동 변환
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

    // ── 전체 포인트 (내부용) ──────────────────────────────────────
    private readonly List<TeachingPoint> _allPoints = [];

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

    // ── 현재 Zone의 필터된 포인트 ──────────────────────────────────
    public ObservableCollection<TeachingPoint> FilteredPoints { get; } = [];
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

        RefreshFilteredPoints();
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

        double offsetPx = clickPos.X - _cameraService.ImageWidth / 2.0;
        double offsetPy = clickPos.Y - _cameraService.ImageHeight / 2.0;

        double offsetMmX = offsetPx / _machineConfig.PixelsPerMm;
        double offsetMmY = offsetPy / _machineConfig.PixelsPerMm;

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

        await motion.MoveZ(0, 80.0);
        await motion.MoveXY(SelectedPoint.X, SelectedPoint.Y, 50.0);
        await motion.MoveZ(SelectedPoint.Z, 30.0);
        StatusMessage = Loc.S("Teach_MovedTo", SelectedPoint.Name);
    }

    [RelayCommand]
    private void AddBoltPoint()
    {
        int idx = CurrentRecipe.BoltPoints.Count + 1;
        var bp = new BoltPoint { Name = $"B{idx}", TargetTorqueNm = _machineConfig.DefaultTorqueNm };
        CurrentRecipe.BoltPoints.Add(bp);

        // Zone 2 Z-only 포인트 추가
        _allPoints.Add(new TeachingPoint
        {
            Name = bp.Name,
            Category = "Zone2_Bolt_Z",
            Zone = 2,
            TeachMode = TeachMode.ZOnly,
            TargetTorqueNm = bp.TargetTorqueNm,
        });

        // Zone 3 XY 마스터 포인트 추가
        _allPoints.Add(new TeachingPoint
        {
            Name = $"{bp.Name}",
            Category = "Zone3_BoltRef",
            Zone = 3,
            TeachMode = TeachMode.XYOnly,
        });

        RefreshFilteredPoints();
        StatusMessage = Loc.S("Teach_BoltAdded", bp.Name);
    }

    [RelayCommand]
    private void RemoveBoltPoint()
    {
        if (SelectedPoint is not ({ Category: "Zone2_Bolt_Z" } or { Category: "Zone3_BoltRef" }))
            return;

        var bpName = SelectedPoint.Name;
        CurrentRecipe.BoltPoints.RemoveAll(b => b.Name == bpName);
        _allPoints.RemoveAll(p =>
            p.Name == bpName && (p.Category == "Zone2_Bolt_Z" || p.Category == "Zone3_BoltRef"));
        SelectedPoint = null;
        RefreshFilteredPoints();
        StatusMessage = Loc.S("Teach_BoltRemoved", bpName);
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
        _allPoints.Clear();
        var r = CurrentRecipe;

        // ── Zone 1: 픽업 (Full XYZ, 독립) ──────────────────────────
        AddPoint("PickPos1", "Zone1_Pick", 1, r.Zone1_PickPos1, TeachMode.Full);
        AddPoint("PickPos2", "Zone1_Pick", 1, r.Zone1_PickPos2, TeachMode.Full);

        // ── Zone 1: 내려놓기 (Z만, X,Y는 Zone 3 오프셋) ─────────────
        AddPoint("PlacePos1", "Zone1_Place_Z", 1, r.Zone1_PlacePos1, TeachMode.ZOnly);
        AddPoint("PlacePos2", "Zone1_Place_Z", 1, r.Zone1_PlacePos2, TeachMode.ZOnly);

        // ── Zone 2: 피듀셜 (Full XYZ, 독립) ─────────────────────────
        AddPoint("Fiducial", "Zone2_Fiducial", 2, r.Zone2_FiducialPos, TeachMode.Full);

        // ── Zone 2: 볼트 (Z만, X,Y는 Zone 3 오프셋) ─────────────────
        foreach (var bp in r.BoltPoints)
            _allPoints.Add(new TeachingPoint
            {
                Name = bp.Name,
                Category = "Zone2_Bolt_Z",
                Zone = 2,
                TeachMode = TeachMode.ZOnly,
                X = bp.X, Y = bp.Y, Z = bp.Z,
                IsTaught = bp.Z != 0,
                TargetTorqueNm = bp.TargetTorqueNm,
            });

        // ── Zone 3: 검사/NG (Full XYZ) ──────────────────────────────
        AddPoint("InspectPos", "Zone3_Inspect", 3, r.Zone3_InspectPos, TeachMode.Full);
        AddPoint("NgPickup", "Zone3_NgPickup", 3, r.Zone3_NgPickupPos, TeachMode.Full);
        AddPoint("NgPlace", "Zone3_NgPlace", 3, r.Zone3_NgPlacePos, TeachMode.Full);

        // ── Zone 3: Place 마스터 XY (→ offset → Zone 1) ──────────────
        // Zone 1 Place 좌표를 Zone 3 좌표로 역변환하여 표시
        var place1Zone3 = Zone1ToZone3(r.Zone1_PlacePos1);
        var place2Zone3 = Zone1ToZone3(r.Zone1_PlacePos2);
        AddPoint("PlacePos1", "Zone3_PlaceRef", 3, place1Zone3, TeachMode.XYOnly);
        AddPoint("PlacePos2", "Zone3_PlaceRef", 3, place2Zone3, TeachMode.XYOnly);

        // ── Zone 3: Bolt 마스터 XY (→ offset → Zone 2) ───────────────
        foreach (var bp in r.BoltPoints)
        {
            var boltZone3 = Zone2ToZone3(new AxisPos { X = bp.X, Y = bp.Y });
            _allPoints.Add(new TeachingPoint
            {
                Name = bp.Name,
                Category = "Zone3_BoltRef",
                Zone = 3,
                TeachMode = TeachMode.XYOnly,
                X = boltZone3.X, Y = boltZone3.Y,
                IsTaught = bp.X != 0 || bp.Y != 0,
            });
        }

        RefreshFilteredPoints();
    }

    private void AddPoint(string name, string category, int zone, AxisPos pos, TeachMode mode)
    {
        _allPoints.Add(new TeachingPoint
        {
            Name = name,
            Category = category,
            Zone = zone,
            TeachMode = mode,
            X = pos.X, Y = pos.Y, Z = pos.Z,
            IsTaught = pos.X != 0 || pos.Y != 0 || pos.Z != 0,
        });
    }

    /// <summary>현재 Zone에 해당하는 포인트만 필터링</summary>
    private void RefreshFilteredPoints()
    {
        FilteredPoints.Clear();
        foreach (var pt in _allPoints.Where(p => p.Zone == SelectedZone))
            FilteredPoints.Add(pt);
        SelectedPoint = FilteredPoints.FirstOrDefault();
    }

    // ── 좌표 변환 헬퍼 ──────────────────────────────────────────────

    /// <summary>Zone 3 좌표 → Zone 1 좌표 (MachineConfig 오프셋 적용)</summary>
    private AxisPos Zone3ToZone1(AxisPos zone3Pos) => _machineConfig.ToZone1(zone3Pos);

    /// <summary>Zone 3 좌표 → Zone 2 좌표 (MachineConfig 오프셋 적용)</summary>
    private AxisPos Zone3ToZone2(AxisPos zone3Pos) => _machineConfig.ToZone2(zone3Pos);

    /// <summary>Zone 1 좌표 → Zone 3 좌표 (역변환)</summary>
    private AxisPos Zone1ToZone3(AxisPos zone1Pos) => new()
    {
        X = zone1Pos.X + _machineConfig.Offset3To1.X,
        Y = zone1Pos.Y + _machineConfig.Offset3To1.Y,
        Z = zone1Pos.Z + _machineConfig.Offset3To1.Z,
    };

    /// <summary>Zone 2 좌표 → Zone 3 좌표 (역변환)</summary>
    private AxisPos Zone2ToZone3(AxisPos zone2Pos) => new()
    {
        X = zone2Pos.X + _machineConfig.Offset3To2.X,
        Y = zone2Pos.Y + _machineConfig.Offset3To2.Y,
        Z = zone2Pos.Z + _machineConfig.Offset3To2.Z,
    };

    /// <summary>티칭 포인트 좌표를 레시피에 반영 + 오프셋 자동 전파</summary>
    private void ApplyPointToRecipe(TeachingPoint pt)
    {
        var pos = new AxisPos { X = pt.X, Y = pt.Y, Z = pt.Z };

        switch (pt.Category)
        {
            // ── Zone 1 독립 포인트 ──────────────────────────────────
            case "Zone1_Pick":
                if (pt.Name == "PickPos1") CurrentRecipe.Zone1_PickPos1 = pos;
                else CurrentRecipe.Zone1_PickPos2 = pos;
                break;

            // ── Zone 1 Place Z만 (X,Y는 Zone 3에서 설정됨) ──────────
            case "Zone1_Place_Z":
                if (pt.Name == "PlacePos1") CurrentRecipe.Zone1_PlacePos1.Z = pt.Z;
                else CurrentRecipe.Zone1_PlacePos2.Z = pt.Z;
                break;

            // ── Zone 2 독립 포인트 ──────────────────────────────────
            case "Zone2_Fiducial":
                CurrentRecipe.Zone2_FiducialPos = pos;
                break;

            // ── Zone 2 Bolt Z만 ─────────────────────────────────────
            case "Zone2_Bolt_Z":
                var bp = CurrentRecipe.BoltPoints.FirstOrDefault(b => b.Name == pt.Name);
                if (bp != null) bp.Z = pt.Z;
                break;

            // ── Zone 3 자체 포인트 ──────────────────────────────────
            case "Zone3_Inspect":
                CurrentRecipe.Zone3_InspectPos = pos;
                break;
            case "Zone3_NgPickup":
                CurrentRecipe.Zone3_NgPickupPos = pos;
                break;
            case "Zone3_NgPlace":
                CurrentRecipe.Zone3_NgPlacePos = pos;
                break;

            // ── Zone 3 Place 마스터 XY → Zone 1 오프셋 자동 적용 ─────
            case "Zone3_PlaceRef":
            {
                var zone1Pos = Zone3ToZone1(pos);
                if (pt.Name == "PlacePos1")
                {
                    CurrentRecipe.Zone1_PlacePos1.X = zone1Pos.X;
                    CurrentRecipe.Zone1_PlacePos1.Y = zone1Pos.Y;
                    // Zone 1 포인트의 표시 좌표도 갱신
                    var z1Pt = _allPoints.FirstOrDefault(p => p.Name == "PlacePos1" && p.Category == "Zone1_Place_Z");
                    if (z1Pt != null) { z1Pt.X = zone1Pos.X; z1Pt.Y = zone1Pos.Y; }
                }
                else
                {
                    CurrentRecipe.Zone1_PlacePos2.X = zone1Pos.X;
                    CurrentRecipe.Zone1_PlacePos2.Y = zone1Pos.Y;
                    var z1Pt = _allPoints.FirstOrDefault(p => p.Name == "PlacePos2" && p.Category == "Zone1_Place_Z");
                    if (z1Pt != null) { z1Pt.X = zone1Pos.X; z1Pt.Y = zone1Pos.Y; }
                }
                break;
            }

            // ── Zone 3 Bolt 마스터 XY → Zone 2 오프셋 자동 적용 ──────
            case "Zone3_BoltRef":
            {
                var zone2Pos = Zone3ToZone2(pos);
                var boltPt = CurrentRecipe.BoltPoints.FirstOrDefault(b => b.Name == pt.Name);
                if (boltPt != null)
                {
                    boltPt.X = zone2Pos.X;
                    boltPt.Y = zone2Pos.Y;
                    // Zone 2 Z-only 포인트의 표시 좌표도 갱신
                    var z2Pt = _allPoints.FirstOrDefault(p => p.Name == pt.Name && p.Category == "Zone2_Bolt_Z");
                    if (z2Pt != null) { z2Pt.X = zone2Pos.X; z2Pt.Y = zone2Pos.Y; }
                }
                break;
            }
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
