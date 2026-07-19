using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace IBTM.Presentation.ViewModels;

/// <summary>
/// 티칭 ViewModel — Zone별 분리 티칭 + Zone 3 오프셋 기반 XY 자동 변환
/// </summary>
public partial class TeachingViewModel : ObservableObject, IDisposable
{
    private readonly IMotionService[] _motions;
    private readonly IIOService _ioService;
    private readonly ICameraStreamService?[] _cameras;
    private readonly RecipeService _recipeService;
    private readonly MachineConfig _machineConfig;
    private readonly TeachingPointMapper _pointMapper;
    private readonly ProcessOrchestrator _orchestrator;
    private readonly DispatcherTimer _posTimer;
    private bool _disposed;

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
    private static readonly double[] JogSpeeds = [1.0, 10.0, 50.0];
    private static readonly int[] LaserChannels =
        [IoMap.Zone1_Laser, IoMap.Zone2_Laser, IoMap.Zone3_Laser];
    public double JogSpeed => JogSpeeds[JogSpeedIndex];

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

    /// <summary>현재 Zone의 카메라 (Zone 2 또는 3, Zone 1은 카메라 없음)</summary>
    private ICameraStreamService? CurrentCamera => GetCamera(SelectedZone);

    public TeachingViewModel(
        [FromKeyedServices(ZoneServiceKeys.Zone1)] IMotionService zone1,
        [FromKeyedServices(ZoneServiceKeys.Zone2)] IMotionService zone2,
        [FromKeyedServices(ZoneServiceKeys.Zone3)] IMotionService zone3,
        IIOService ioService,
        [FromKeyedServices(ZoneServiceKeys.Zone2)] ICameraStreamService zone2Camera,
        [FromKeyedServices(ZoneServiceKeys.Zone3)] ICameraStreamService zone3Camera,
        RecipeService recipeService,
        MachineConfig machineConfig,
        TeachingPointMapper pointMapper,
        ProcessOrchestrator orchestrator)
    {
        _motions = [zone1, zone2, zone3];
        _ioService = ioService;
        _cameras = [null, zone2Camera, zone3Camera];
        _recipeService = recipeService;
        _machineConfig = machineConfig;
        _pointMapper = pointMapper;
        _orchestrator = orchestrator;
        CurrentRecipe = orchestrator.CurrentRecipe;
        RecipeName = CurrentRecipe.Name;

        // 두 카메라 모두 프레임 수신 연결
        zone2Camera.FrameReady += OnZone2FrameReady;
        zone3Camera.FrameReady += OnZone3FrameReady;

        _posTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _posTimer.Tick += OnPositionTimerTick;

        BuildTeachingPoints();
        RefreshRecipeFiles();
    }

    private IMotionService CurrentMotion => GetMotion(SelectedZone);

    private int LaserIoIndex => GetLaserChannel(SelectedZone);

    partial void OnCurrentRecipeChanged(Recipe value) =>
        _orchestrator.CurrentRecipe = value;

    private IMotionService GetMotion(int zone) => _motions[zone - 1];

    private ICameraStreamService? GetCamera(int zone) => _cameras[zone - 1];

    private static int GetLaserChannel(int zone) => LaserChannels[zone - 1];

    private void UpdateLiveImage(int zone, ImageSource image)
    {
        void Apply()
        {
            if (SelectedZone == zone)
            {
                LiveImage = image;
            }
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply();
        }
        else
        {
            dispatcher.BeginInvoke(Apply);
        }
    }

    // ── Zone 변경 ───────────────────────────────────────────────────

    partial void OnSelectedZoneChanged(int oldValue, int newValue)
    {
        if (LaserOn)
        {
            _ioService.SetOutput(GetLaserChannel(oldValue), false);
            LaserOn = false;
        }
        GetMotion(oldValue).Stop();

        // 카메라: 이전 Zone 카메라 끄고, 새 Zone에 카메라 없으면 OFF
        if (IsCameraLive)
        {
            // 이전 Zone 카메라 정지
            var prevCam = GetCamera(oldValue);
            prevCam?.StopLiveView();

            if (CurrentCamera != null)
            {
                // 새 Zone에도 카메라가 있으면 전환
                CurrentCamera.StartLiveView();
            }
            else
            {
                // Zone 1 등 카메라 없는 Zone이면 OFF
                IsCameraLive = false;
                LiveImage = null;
            }
        }

        RefreshFilteredPoints();
    }

    // ── 조그 ────────────────────────────────────────────────────────

    [RelayCommand] private void JogXPlus() => CurrentMotion.JogX(JogSpeed);
    [RelayCommand] private void JogXMinus() => CurrentMotion.JogX(-JogSpeed);
    [RelayCommand] private void JogYPlus() => CurrentMotion.JogY(JogSpeed);
    [RelayCommand] private void JogYMinus() => CurrentMotion.JogY(-JogSpeed);
    [RelayCommand] private void JogZPlus() => CurrentMotion.JogZ(JogSpeed);
    [RelayCommand] private void JogZMinus() => CurrentMotion.JogZ(-JogSpeed);
    [RelayCommand] private void JogStop() => CurrentMotion.Stop();

    // ── 카메라 ──────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleLiveView()
    {
        if (CurrentCamera == null) return;

        if (IsCameraLive)
        {
            CurrentCamera.StopLiveView();
            IsCameraLive = false;
            LiveImage = null;
        }
        else
        {
            CurrentCamera.StartLiveView();
            IsCameraLive = true;
        }
    }

    /// <summary>카메라 이미지 클릭 → 픽셀→mm 변환 → 이동 (Zone 2, 3)</summary>
    [RelayCommand]
    private async Task CameraClickAsync(Point clickPos)
    {
        if (CurrentCamera == null) return;

        double offsetPx = clickPos.X - CurrentCamera.ImageWidth / 2.0;
        double offsetPy = clickPos.Y - CurrentCamera.ImageHeight / 2.0;

        double offsetMmX = offsetPx / _machineConfig.PixelsPerMm;
        double offsetMmY = offsetPy / _machineConfig.PixelsPerMm;

        double targetX = CurrentX + offsetMmX;
        double targetY = CurrentY + offsetMmY;

        await CurrentMotion.MoveToXYAsync(targetX, targetY, 50.0);
        StatusMessage = $"Move → X:{targetX:F3} Y:{targetY:F3}";
    }

    // ── 레이저 ──────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleLaser()
    {
        LaserOn = !LaserOn;
        _ioService.SetOutput(LaserIoIndex, LaserOn);
    }

    // ── 티칭 ────────────────────────────────────────────────────────

    [RelayCommand]
    private void TeachCurrentPosition()
    {
        if (SelectedPoint == null) return;

        var current = CurrentMotion.GetPosition();
        SelectedPoint.Teach(current.X ?? 0, current.Y ?? 0, current.Z ?? 0);
        _pointMapper.Apply(CurrentRecipe, _allPoints, SelectedPoint);
        StatusMessage = Loc.S("Teach_Recorded", SelectedPoint.Name);
    }

    [RelayCommand]
    private async Task MoveToPointAsync()
    {
        if (SelectedPoint == null || !SelectedPoint.IsTaught) return;

        var motion = GetMotion(SelectedPoint.Zone);

        await motion.MoveToZAsync(0, 80.0);
        await motion.MoveToXYAsync(SelectedPoint.X, SelectedPoint.Y, 50.0);
        await motion.MoveToZAsync(SelectedPoint.Z, 30.0);
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
            Kind = TeachingPointKind.Zone2BoltZ,
            Zone = 2,
            TeachMode = TeachMode.ZOnly,
            TargetTorqueNm = bp.TargetTorqueNm,
        });

        // Zone 3 XY 마스터 포인트 추가
        _allPoints.Add(new TeachingPoint
        {
            Name = $"{bp.Name}",
            Kind = TeachingPointKind.Zone3BoltReference,
            Zone = 3,
            TeachMode = TeachMode.XYOnly,
        });

        RefreshFilteredPoints();
        StatusMessage = Loc.S("Teach_BoltAdded", bp.Name);
    }

    [RelayCommand]
    private void RemoveBoltPoint()
    {
        if (SelectedPoint is not ({ Kind: TeachingPointKind.Zone2BoltZ }
            or { Kind: TeachingPointKind.Zone3BoltReference }))
            return;

        var bpName = SelectedPoint.Name;
        CurrentRecipe.BoltPoints.RemoveAll(b => b.Name == bpName);
        _allPoints.RemoveAll(p =>
            p.Name == bpName
            && p.Kind is TeachingPointKind.Zone2BoltZ or TeachingPointKind.Zone3BoltReference);
        SelectedPoint = null;
        RefreshFilteredPoints();
        StatusMessage = Loc.S("Teach_BoltRemoved", bpName);
    }

    // ── 레시피 저장/로드 ────────────────────────────────────────────

    [RelayCommand]
    private async Task SaveRecipeAsync()
    {
        try
        {
            var normalizedName = RecipeName.Trim();
            CurrentRecipe.Name = normalizedName;
            RecipeName = CurrentRecipe.Name;
            await _recipeService.SaveRecipeAsync(CurrentRecipe);
            RefreshRecipeFiles();
            StatusMessage = Loc.S("Teach_RecipeSaved", RecipeName);
        }
        catch (Exception exception)
        {
            StatusMessage = $"Recipe save failed: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task LoadRecipeAsync(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;

        try
        {
            CurrentRecipe = await _recipeService.LoadRecipeAsync(filePath);
            RecipeName = CurrentRecipe.Name;
            BuildTeachingPoints();
            StatusMessage = Loc.S("Teach_RecipeLoaded", RecipeName);
        }
        catch (Exception exception)
        {
            StatusMessage = $"Recipe load failed: {exception.Message}";
        }
    }

    [RelayCommand]
    private void NewRecipe()
    {
        RecipeName = "New";
        CurrentRecipe = new Recipe { Name = RecipeName };
        BuildTeachingPoints();
        StatusMessage = Loc.S("Teach_NewRecipe");
    }

    // ── 티칭 포인트 빌드 ────────────────────────────────────────────

    private void BuildTeachingPoints()
    {
        _allPoints.Clear();
        _allPoints.AddRange(_pointMapper.Build(CurrentRecipe));
        RefreshFilteredPoints();
    }

    /// <summary>현재 Zone에 해당하는 포인트만 필터링</summary>
    private void RefreshFilteredPoints()
    {
        FilteredPoints.Clear();
        foreach (var pt in _allPoints.Where(p => p.Zone == SelectedZone))
            FilteredPoints.Add(pt);
        SelectedPoint = FilteredPoints.FirstOrDefault();
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
        var current = CurrentMotion.GetPosition();
        CurrentX = current.X ?? 0;
        CurrentY = current.Y ?? 0;
        CurrentZ = current.Z ?? 0;
    }

    public void StartPolling() => _posTimer.Start();

    public void StopPolling()
    {
        _posTimer.Stop();
        // 모든 카메라 정리
        if (IsCameraLive)
        {
            _cameras[1]!.StopLiveView();
            _cameras[2]!.StopLiveView();
            IsCameraLive = false;
            LiveImage = null;
        }
        if (LaserOn)
        {
            _ioService.SetOutput(LaserIoIndex, false);
            LaserOn = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopPolling();
        _posTimer.Tick -= OnPositionTimerTick;
        _cameras[1]!.FrameReady -= OnZone2FrameReady;
        _cameras[2]!.FrameReady -= OnZone3FrameReady;
    }

    private void OnPositionTimerTick(object? sender, EventArgs e) => PollPosition();
    private void OnZone2FrameReady(ImageSource image) => UpdateLiveImage(2, image);
    private void OnZone3FrameReady(ImageSource image) => UpdateLiveImage(3, image);
}
