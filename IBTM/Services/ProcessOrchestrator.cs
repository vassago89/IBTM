using IBTM.Device;
using IBTM.Models;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM.Services;

/// <summary>
/// 3구간 파이프라인 오케스트레이터
///
///   구간1 (zone1Motion): 앞장비 셔틀에서 PCB 2개 픽업 → 우리 셔틀에 배치
///   구간2 (zone2Motion): 볼트 체결 (Fiducial 보정 → N회 체결)
///   구간3 (zone3Motion): 카메라 검사 → NG 적재 / Good SMEMA 배출
///
///   각 구간은 독립 XYZ 모션축을 가지며, 셔틀은 공용 컨베이어로 이동.
///   셔틀이 구간에 도착하면 스토퍼 → 얼라인 → 리프트 업 → 작업 → 리프트 다운 → 스토퍼 해제.
///   3구간 동시 동작 (각각 다른 셔틀 대상).
/// </summary>
public class ProcessOrchestrator
{
    private readonly IMotionService _zone1Motion;   // 구간1: 픽업 XYZ
    private readonly IMotionService _zone2Motion;   // 구간2: 볼트 체결 XYZ
    private readonly IMotionService _zone3Motion;   // 구간3: 검사 XYZ
    private readonly IIOService _ioService;
    private readonly IFiducialService _fiducialService;
    private readonly IBoltService _boltService;

    private CancellationTokenSource? _cts;

    // ── 이벤트 ──────────────────────────────────────────────────────────────
    public event EventHandler<StageChangedEventArgs>? StageChanged;
    public event EventHandler<LogEntry>? LogAdded;
    public event EventHandler<ProductionStats>? StatsUpdated;
    public event EventHandler<ZonePositionEventArgs>? ZonePositionChanged;
    public event EventHandler<FiducialResult>? FiducialDetected;
    public event EventHandler<BoltResult>? BoltCompleted;
    public event EventHandler<BoltProgressEventArgs>? BoltProgress;
    public event EventHandler<InspectionResult>? InspectionDone;
    public event EventHandler<InspectionResult>? RouteDecided;
    public event EventHandler<int>? NgStackUpdated;
    public event EventHandler<int>? NgStackAlarm;
    public event EventHandler<(int Zone, bool Active)>? GripperChanged;

    // ── 상태 ────────────────────────────────────────────────────────────────
    public bool IsRunning => _cts is { IsCancellationRequested: false };
    public ProductionStats Stats { get; } = new();
    public int NgStackCount { get; private set; }

    // ── 설정 ────────────────────────────────────────────────────────────────
    public Recipe CurrentRecipe { get; set; } = new();

    public ProcessOrchestrator(
        [FromKeyedServices("zone1")] IMotionService zone1Motion,
        [FromKeyedServices("zone2")] IMotionService zone2Motion,
        [FromKeyedServices("zone3")] IMotionService zone3Motion,
        IIOService ioService,
        IFiducialService fiducialService,
        IBoltService boltService)
    {
        _zone1Motion = zone1Motion;
        _zone2Motion = zone2Motion;
        _zone3Motion = zone3Motion;
        _ioService = ioService;
        _fiducialService = fiducialService;
        _boltService = boltService;
        HardwareInit();
    }

    private void HardwareInit()
    {
        _zone1Motion.Initialize(0, 1, 2);
        _zone1Motion.On();
        _zone2Motion.Initialize(3, 4, 5);
        _zone2Motion.On();
        _zone3Motion.Initialize(6, 7, 8);
        _zone3Motion.On();
        _ioService.Initiliaze();
        Stats.StartTime = DateTime.Now;
    }

    // ── 공정 제어 ────────────────────────────────────────────────────────────
    public async Task StartAsync()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        AddLog("공정 시작", ProcessStage.Idle);

        try { await RunPipelineAsync(_cts.Token); }
        catch (OperationCanceledException)
        {
            AddLog("공정 중지됨", ProcessStage.Idle);
            Transition(ProcessStage.Idle, StageStatus.Idle);
        }
        catch (Exception ex)
        {
            AddLog($"공정 오류: {ex.Message}", ProcessStage.Error, LogLevel.Error);
            Transition(ProcessStage.Error, StageStatus.Error);
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _zone1Motion.Stop();
        _zone2Motion.Stop();
        _zone3Motion.Stop();
        AddLog("정지 요청", ProcessStage.Idle, LogLevel.Warning);
    }

    public void EStop()
    {
        _cts?.Cancel();
        _zone1Motion.EStop();
        _zone2Motion.EStop();
        _zone3Motion.EStop();
        _ioService.Off();
        AddLog("비상 정지 (E-STOP)!", ProcessStage.Idle, LogLevel.Error);
        Transition(ProcessStage.Idle, StageStatus.Error);
    }

    public void ResetNgStack()
    {
        NgStackCount = 0;
        NgStackUpdated?.Invoke(this, 0);
        AddLog("NG 적재 카운트 초기화", ProcessStage.Idle);
    }

    // ── 메인 파이프라인 ──────────────────────────────────────────────────────
    /// <summary>
    /// 3구간 병렬 파이프라인.
    /// 각 구간은 독립 루프를 돌며 셔틀이 도착하면 작업 수행.
    /// </summary>
    private async Task RunPipelineAsync(CancellationToken ct)
    {
        var zone1Task = RunZone1LoopAsync(ct);
        var zone2Task = RunZone2LoopAsync(ct);
        var zone3Task = RunZone3LoopAsync(ct);

        await Task.WhenAll(zone1Task, zone2Task, zone3Task);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 구간 1: 픽업
    // ══════════════════════════════════════════════════════════════════════════
    private async Task RunZone1LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // 셔틀 도착 + 앞장비 뒤쪽 레인 센서 대기
            await DoStageAsync(ProcessStage.Zone1_WaitShuttle, ct, async () =>
            {
                AddLog("[구간1] 셔틀 도착 + 앞장비 레인 센서 대기", ProcessStage.Zone1_WaitShuttle);
                await WaitForSensorAsync(IoMap.Zone1_Sensor, ct);
                await WaitForSensorAsync(IoMap.PrevRearLaneSensor, ct);
                AddLog("[구간1] 셔틀 + 앞장비 준비 확인", ProcessStage.Zone1_WaitShuttle);
            });

            // 스토퍼 → 얼라인 → 리프트 업
            await DoStageAsync(ProcessStage.Zone1_StopAlignLift, ct, async () =>
            {
                AddLog("[구간1] 스토퍼 → 얼라인 → 리프트 업", ProcessStage.Zone1_StopAlignLift);
                await StopAlignLiftAsync(IoMap.Zone1_Stopper, IoMap.Zone1_Align, IoMap.Zone1_Lift, ct);
            });

            // PCB 2개 픽업 → 배치
            await DoStageAsync(ProcessStage.Zone1_PickPlace, ct, async () =>
            {
                AddLog("[구간1] PCB 1번 픽업", ProcessStage.Zone1_PickPlace);
                await PickAndPlaceAsync(
                    _zone1Motion, CurrentRecipe.Zone1_PickPos1, CurrentRecipe.Zone1_PlacePos1,
                    IoMap.Zone1_Gripper, 1, ct);

                AddLog("[구간1] PCB 2번 픽업", ProcessStage.Zone1_PickPlace);
                await PickAndPlaceAsync(
                    _zone1Motion, CurrentRecipe.Zone1_PickPos2, CurrentRecipe.Zone1_PlacePos2,
                    IoMap.Zone1_Gripper, 1, ct);

                AddLog("[구간1] PCB 2개 배치 완료", ProcessStage.Zone1_PickPlace);
            });

            // 리프트 다운 → 스토퍼 해제
            await DoStageAsync(ProcessStage.Zone1_Release, ct, async () =>
            {
                AddLog("[구간1] 리프트 다운 → 스토퍼 해제", ProcessStage.Zone1_Release);
                await ReleaseAsync(IoMap.Zone1_Stopper, IoMap.Zone1_Align, IoMap.Zone1_Lift, ct);
            });
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 구간 2: 볼트 체결
    // ══════════════════════════════════════════════════════════════════════════
    private async Task RunZone2LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // 셔틀 도착 대기
            await DoStageAsync(ProcessStage.Zone2_WaitShuttle, ct, async () =>
            {
                AddLog("[구간2] 셔틀 도착 대기", ProcessStage.Zone2_WaitShuttle);
                await WaitForSensorAsync(IoMap.Zone2_Sensor, ct);
                AddLog("[구간2] 셔틀 도착 확인", ProcessStage.Zone2_WaitShuttle);
            });

            // 스토퍼 → 얼라인 → 리프트 업
            await DoStageAsync(ProcessStage.Zone2_StopAlignLift, ct, async () =>
            {
                AddLog("[구간2] 스토퍼 → 얼라인 → 리프트 업", ProcessStage.Zone2_StopAlignLift);
                await StopAlignLiftAsync(IoMap.Zone2_Stopper, IoMap.Zone2_Align, IoMap.Zone2_Lift, ct);
            });

            // Fiducial 검출
            FiducialResult? fiducial = null;
            await DoStageAsync(ProcessStage.Zone2_Fiducial, ct, async () =>
            {
                AddLog("[구간2] Fiducial 검출", ProcessStage.Zone2_Fiducial);
                var pos = CurrentRecipe.Zone2_FiducialPos;
                await _zone2Motion.MoveXY(pos.X, pos.Y, 100.0);
                FireZonePos(2);
                await _zone2Motion.MoveZ(pos.Z, 60.0);
                ct.ThrowIfCancellationRequested();
                FireZonePos(2);

                fiducial = await _fiducialService.DetectFromCameraAsync(ct);
                FiducialDetected?.Invoke(this, fiducial);

                if (fiducial.Found)
                    AddLog($"[구간2] Fiducial  dX:{fiducial.OffsetX:+0.000;-0.000}  dY:{fiducial.OffsetY:+0.000;-0.000}  ({fiducial.Confidence:P0})", ProcessStage.Zone2_Fiducial);
                else
                    AddLog("[구간2] Fiducial 검출 실패", ProcessStage.Zone2_Fiducial, LogLevel.Warning);
            });

            // 볼트 체결 반복
            await DoStageAsync(ProcessStage.Zone2_BoltTighten, ct, async () =>
            {
                var boltPoints = CurrentRecipe.BoltPoints;
                for (int i = 0; i < boltPoints.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var bp = boltPoints[i];

                    BoltProgress?.Invoke(this, new BoltProgressEventArgs(i + 1, boltPoints.Count, bp.Name));
                    AddLog($"[구간2] 볼트 {i + 1}/{boltPoints.Count} [{bp.Name}] 이동", ProcessStage.Zone2_BoltTighten);

                    double corrX = bp.X + (fiducial?.OffsetX ?? 0);
                    double corrY = bp.Y + (fiducial?.OffsetY ?? 0);

                    await _zone2Motion.MoveXY(corrX, corrY, 80.0);
                    FireZonePos(2);
                    await _zone2Motion.MoveZ(bp.Z, 50.0);
                    ct.ThrowIfCancellationRequested();
                    FireZonePos(2);

                    AddLog($"[구간2] [{bp.Name}] Shooting", ProcessStage.Zone2_BoltTighten);
                    await _boltService.ShootAsync(ct);

                    AddLog($"[구간2] [{bp.Name}] Tightening  목표: {bp.TargetTorqueNm:F1} Nm", ProcessStage.Zone2_BoltTighten);
                    var boltResult = await _boltService.TightenAsync(bp.TargetTorqueNm, ct);
                    BoltCompleted?.Invoke(this, boltResult);

                    var lvl = boltResult.Success ? LogLevel.Info : LogLevel.Warning;
                    AddLog($"[구간2] [{bp.Name}] 체결 완료  실측: {boltResult.Torque:F2} Nm  [{boltResult.Message}]",
                        ProcessStage.Zone2_BoltTighten, lvl);

                    await _zone2Motion.MoveZ(0, 80.0);
                    FireZonePos(2);
                }

                AddLog("[구간2] 전체 볼트 체결 완료", ProcessStage.Zone2_BoltTighten);
            });

            // 리프트 다운 → 스토퍼 해제
            await DoStageAsync(ProcessStage.Zone2_Release, ct, async () =>
            {
                AddLog("[구간2] 리프트 다운 → 스토퍼 해제", ProcessStage.Zone2_Release);
                await ReleaseAsync(IoMap.Zone2_Stopper, IoMap.Zone2_Align, IoMap.Zone2_Lift, ct);
            });
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 구간 3: 검사
    // ══════════════════════════════════════════════════════════════════════════
    private async Task RunZone3LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var cycleStart = DateTime.Now;

            // 셔틀 도착 대기
            await DoStageAsync(ProcessStage.Zone3_WaitShuttle, ct, async () =>
            {
                AddLog("[구간3] 셔틀 도착 대기", ProcessStage.Zone3_WaitShuttle);
                await WaitForSensorAsync(IoMap.Zone3_Sensor, ct);
                AddLog("[구간3] 셔틀 도착 확인", ProcessStage.Zone3_WaitShuttle);
            });

            // 스토퍼 → 얼라인 → 리프트 업
            await DoStageAsync(ProcessStage.Zone3_StopAlignLift, ct, async () =>
            {
                AddLog("[구간3] 스토퍼 → 얼라인 → 리프트 업", ProcessStage.Zone3_StopAlignLift);
                await StopAlignLiftAsync(IoMap.Zone3_Stopper, IoMap.Zone3_Align, IoMap.Zone3_Lift, ct);
            });

            // 카메라 검사
            InspectionResult inspectResult = InspectionResult.Unknown;
            await DoStageAsync(ProcessStage.Zone3_Inspect, ct, async () =>
            {
                AddLog("[구간3] 카메라 검사 시작 (볼트 유무)", ProcessStage.Zone3_Inspect);
                var pos = CurrentRecipe.Zone3_InspectPos;
                await _zone3Motion.MoveXY(pos.X, pos.Y, 100.0);
                FireZonePos(3);
                await _zone3Motion.MoveZ(pos.Z, 60.0);
                ct.ThrowIfCancellationRequested();
                FireZonePos(3);

                inspectResult = await VisionInspectAsync(ct);
                InspectionDone?.Invoke(this, inspectResult);
                RouteDecided?.Invoke(this, inspectResult);

                var lvl = inspectResult == InspectionResult.Good ? LogLevel.Info : LogLevel.Warning;
                var text = inspectResult == InspectionResult.Good ? "▶ GOOD ◀" : "▶  NG  ◀";
                AddLog($"[구간3] 검사 결과: {text}", ProcessStage.Zone3_Inspect, lvl);
            });

            // NG / Good 분기
            if (inspectResult != InspectionResult.Good)
            {
                // NG → 뒤쪽 적재
                await DoStageAsync(ProcessStage.Zone3_NgTransfer, ct, async () =>
                {
                    AddLog("[구간3] NG → 뒤쪽 적재", ProcessStage.Zone3_NgTransfer);

                    // 그리퍼로 PCB 픽업 → NG 적재 위치로 이동
                    _ioService.Set(IoMap.Zone3_Gripper, true);
                    GripperChanged?.Invoke(this, (3, true));
                    await Task.Delay(200, ct);

                    await _zone3Motion.MoveZ(0, 80.0);
                    FireZonePos(3);
                    await _zone3Motion.MoveY(CurrentRecipe.Zone3_NgStackY, 80.0);
                    FireZonePos(3);
                    await _zone3Motion.MoveZ(CurrentRecipe.Zone3_NgStackZ, 50.0);
                    ct.ThrowIfCancellationRequested();
                    FireZonePos(3);

                    _ioService.Set(IoMap.Zone3_Gripper, false);
                    GripperChanged?.Invoke(this, (3, false));
                    await Task.Delay(150, ct);

                    await _zone3Motion.MoveZ(0, 80.0);
                    FireZonePos(3);

                    NgStackCount++;
                    NgStackUpdated?.Invoke(this, NgStackCount);
                    AddLog($"[구간3] NG 적재 완료 ({NgStackCount}/{CurrentRecipe.NgStackMaxCount})",
                        ProcessStage.Zone3_NgTransfer, LogLevel.Warning);

                    if (NgStackCount >= CurrentRecipe.NgStackMaxCount)
                    {
                        AddLog($"[구간3] NG 적재 {NgStackCount}개 → 설비 알람! 작업자 확인 필요",
                            ProcessStage.Zone3_NgTransfer, LogLevel.Error);
                        NgStackAlarm?.Invoke(this, NgStackCount);
                    }
                });
            }
            else
            {
                // Good → SMEMA 대기 → 배출
                await DoStageAsync(ProcessStage.Zone3_SmemaWait, ct, async () =>
                {
                    AddLog("[구간3] SMEMA 신호 대기...", ProcessStage.Zone3_SmemaWait);
                    await WaitForSensorAsync(IoMap.Smema_MachineReady, ct);
                    AddLog("[구간3] SMEMA OK → 배출 준비", ProcessStage.Zone3_SmemaWait);
                });

                await DoStageAsync(ProcessStage.Zone3_Release, ct, async () =>
                {
                    AddLog("[구간3] 리프트 다운 → 스토퍼 해제 (배출)", ProcessStage.Zone3_Release);
                    await ReleaseAsync(IoMap.Zone3_Stopper, IoMap.Zone3_Align, IoMap.Zone3_Lift, ct);
                });

                await DoStageAsync(ProcessStage.Zone3_Discharge, ct, async () =>
                {
                    AddLog("[구간3] 배출 중 (SMEMA BoardAvailable)", ProcessStage.Zone3_Discharge);
                    _ioService.Set(IoMap.Smema_BoardAvailable, true);
                    await Task.Delay(500, ct);
                    _ioService.Set(IoMap.Smema_BoardAvailable, false);
                    AddLog("[구간3] 배출 완료", ProcessStage.Zone3_Discharge);
                });
            }

            // NG 경로일 때도 리프트 다운/스토퍼 해제 필요
            if (inspectResult != InspectionResult.Good)
            {
                await DoStageAsync(ProcessStage.Zone3_Release, ct, async () =>
                {
                    AddLog("[구간3] 리프트 다운 → 스토퍼 해제", ProcessStage.Zone3_Release);
                    await ReleaseAsync(IoMap.Zone3_Stopper, IoMap.Zone3_Align, IoMap.Zone3_Lift, ct);
                });
            }

            // 사이클 통계 (구간3 기준으로 집계)
            var elapsed = (DateTime.Now - cycleStart).TotalSeconds;
            Stats.TotalCount++;
            if (inspectResult == InspectionResult.Good) Stats.GoodCount++;
            else Stats.NgCount++;
            Stats.LastCycleTimeSeconds = elapsed;
            Stats.AverageCycleTimeSeconds = Stats.AverageCycleTimeSeconds == 0
                ? elapsed : Stats.AverageCycleTimeSeconds * 0.7 + elapsed * 0.3;

            StatsUpdated?.Invoke(this, Stats);
            Transition(ProcessStage.Complete, StageStatus.Done);

            AddLog($"[구간3] 사이클 완료  CT:{elapsed:F1}s  누적:{Stats.TotalCount:N0}",
                ProcessStage.Complete);

            await Task.Delay(300, ct);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 공통 동작
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>센서 입력 대기 (시뮬: 400ms 후 자동 ON)</summary>
    private async Task WaitForSensorAsync(int sensorIndex, CancellationToken ct)
    {
        // TODO: while (!_ioService.GetIn(sensorIndex)) { await Task.Delay(50, ct); }
        await Task.Delay(400, ct);
    }

    /// <summary>스토퍼 ON → 얼라인 ON → 리프트 UP</summary>
    private async Task StopAlignLiftAsync(int stopper, int align, int lift, CancellationToken ct)
    {
        _ioService.Set(stopper, true);
        await Task.Delay(200, ct);
        _ioService.Set(align, true);
        await Task.Delay(300, ct);
        _ioService.Set(lift, true);
        await Task.Delay(500, ct);
    }

    /// <summary>리프트 DOWN → 얼라인 OFF → 스토퍼 OFF</summary>
    private async Task ReleaseAsync(int stopper, int align, int lift, CancellationToken ct)
    {
        _ioService.Set(lift, false);
        await Task.Delay(500, ct);
        _ioService.Set(align, false);
        await Task.Delay(200, ct);
        _ioService.Set(stopper, false);
        await Task.Delay(200, ct);
    }

    /// <summary>픽업 위치로 이동 → 그리퍼 닫기 → 배치 위치로 이동 → 그리퍼 열기</summary>
    private async Task PickAndPlaceAsync(
        IMotionService motion, AxisPos pick, AxisPos place,
        int gripperIo, int zone, CancellationToken ct)
    {
        // 픽업 위치로 이동
        await motion.MoveXY(pick.X, pick.Y, 120.0);
        FireZonePos(zone);
        await motion.MoveZ(pick.Z, 60.0);
        ct.ThrowIfCancellationRequested();
        FireZonePos(zone);

        // 그리퍼 닫기 (PCB 픽업)
        _ioService.Set(gripperIo, true);
        GripperChanged?.Invoke(this, (zone, true));
        await Task.Delay(200, ct);

        // Z 복귀 → 배치 위치로 이동
        await motion.MoveZ(0, 80.0);
        FireZonePos(zone);
        await motion.MoveXY(place.X, place.Y, 100.0);
        FireZonePos(zone);
        await motion.MoveZ(place.Z, 50.0);
        ct.ThrowIfCancellationRequested();
        FireZonePos(zone);

        // 그리퍼 열기 (PCB 배치)
        _ioService.Set(gripperIo, false);
        GripperChanged?.Invoke(this, (zone, false));
        await Task.Delay(150, ct);

        // Z 복귀
        await motion.MoveZ(0, 80.0);
        FireZonePos(zone);
    }

    /// <summary>카메라 비전 검사 (볼트 유무)</summary>
    private async Task<InspectionResult> VisionInspectAsync(CancellationToken ct)
    {
        // TODO: BaslerService 카메라로 볼트 유무 검사
        await Task.Delay(500, ct);
        return Random.Shared.NextDouble() > 0.15
            ? InspectionResult.Good
            : InspectionResult.Ng;
    }

    // ── 스테이지 실행 래퍼 ─────────────────────────────────────────────────
    private async Task DoStageAsync(ProcessStage stage, CancellationToken ct, Func<Task> action)
    {
        ct.ThrowIfCancellationRequested();
        Transition(stage, StageStatus.Running);
        try
        {
            await action();
            Transition(stage, StageStatus.Done);
        }
        catch (OperationCanceledException)
        {
            Transition(stage, StageStatus.Idle);
            throw;
        }
        catch (Exception ex)
        {
            AddLog($"[{stage}] 오류: {ex.Message}", stage, LogLevel.Error);
            Transition(stage, StageStatus.Error);
            throw;
        }
    }

    // ── 헬퍼 ────────────────────────────────────────────────────────────────
    private void Transition(ProcessStage stage, StageStatus status)
    {
        StageChanged?.Invoke(this, new StageChangedEventArgs(stage, status));
    }

    private void FireZonePos(int zone)
    {
        var motion = GetMotion(zone);
        motion.GetPotision(out var x, out var y, out var z);
        ZonePositionChanged?.Invoke(this,
            new ZonePositionEventArgs(zone, (x ?? 0) / 1000.0, (y ?? 0) / 1000.0, (z ?? 0) / 1000.0));
    }

    private IMotionService GetMotion(int zone) => zone switch
    {
        1 => _zone1Motion, 2 => _zone2Motion, 3 => _zone3Motion,
        _ => throw new ArgumentOutOfRangeException(nameof(zone))
    };

    private void AddLog(string message, ProcessStage stage, LogLevel level = LogLevel.Info)
    {
        LogAdded?.Invoke(this, new LogEntry { Message = message, Stage = stage.ToString(), Level = level });
    }
}
