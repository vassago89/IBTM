using IBTM.Device;
using IBTM.Models;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM.Services;

public class StageChangedEventArgs(ProcessStage stage, StageStatus status) : EventArgs
{
    public ProcessStage Stage { get; } = stage;
    public StageStatus Status { get; } = status;
}

public class BoltProgressEventArgs(int current, int total, string boltName) : EventArgs
{
    public int Current { get; } = current;
    public int Total { get; } = total;
    public string BoltName { get; } = boltName;
}

/// <summary>
/// SMT Inline 볼트 체결 설비 공정 오케스트레이터
///
/// 파이프라인 구조 (2번째 사이클부터):
///   [Bolt Station]  FiducialBolt → [BoltTighten→VisionInspect]×N → FinalInspect ─▶ Route
///        ⟋ 병렬 ⟋
///   [Transfer]      LineArrival → FiducialPick → PickAndTransfer → ConveyorToBolt (다음 PCB)
///
/// 병렬 가능 이유:
///   - BoltTighten  : _fiducialMotion 사용
///   - PickAndTransfer : _transferMotion 사용 → 충돌 없음
///   - FiducialForPick : _fiducialMotion 사용 → BoltTighten 완료 후 순차 실행
/// </summary>
public class ProcessOrchestrator
{
    private readonly IMotionService _transferMotion;   // PCB 픽업/배치/NG 적재
    private readonly IMotionService _fiducialMotion;   // 카메라 + 볼트 체결 위치
    private readonly IIOService _ioService;
    private readonly IFiducialService _fiducialService;
    private readonly IBoltService _boltService;

    private CancellationTokenSource? _cts;
    private ProcessStage _currentStage = ProcessStage.Idle;

    // ── 이벤트 ──────────────────────────────────────────────────────────────
    public event EventHandler<StageChangedEventArgs>? StageChanged;
    public event EventHandler<LogEntry>? LogAdded;
    public event EventHandler<ProductionStats>? StatsUpdated;
    public event EventHandler<(double X, double Y, double Z)>? TransferPositionChanged;
    public event EventHandler<(double X, double Y, double Z)>? FiducialPositionChanged;
    public event EventHandler<FiducialResult>? FiducialDetected;
    public event EventHandler<BoltResult>? BoltCompleted;
    public event EventHandler<BoltProgressEventArgs>? BoltProgress;
    public event EventHandler<InspectionResult>? InspectionDone;
    public event EventHandler<InspectionResult>? RouteDecided;  // NG/Good 분기 결정
    public event EventHandler<int>? NgStackUpdated;             // NG 적재 수 변경
    public event EventHandler<int>? NgStackAlarm;               // NG 적재 수량 알람
    public event EventHandler<bool>? Line1SensorChanged;        // Line 1 도착 센서
    public event EventHandler<bool>? Line2SensorChanged;        // Line 2 도착 센서

    // ── 상태 ────────────────────────────────────────────────────────────────
    public ProcessStage CurrentStage => _currentStage;
    public bool IsRunning => _cts is { IsCancellationRequested: false };
    public ProductionStats Stats { get; } = new();
    public int NgStackCount { get; private set; }
    public InspectionResult LastRoute { get; private set; } = InspectionResult.Unknown;

    // ── 설정 ────────────────────────────────────────────────────────────────
    /// <summary>
    /// 업스트림에 2개 라인이 있으며, 두 라인 모두 PCB 도착 확인 후
    /// Line 1의 PCB를 방열판 위로 Transfer.
    /// </summary>
    public Recipe CurrentRecipe { get; set; } = new();

    public ProcessOrchestrator(
        [FromKeyedServices("transfer")] IMotionService transferMotion,
        [FromKeyedServices("fiducial")] IMotionService fiducialMotion,
        IIOService ioService,
        IFiducialService fiducialService,
        IBoltService boltService)
    {
        _transferMotion = transferMotion;
        _fiducialMotion = fiducialMotion;
        _ioService = ioService;
        _fiducialService = fiducialService;
        _boltService = boltService;
        HardwareInit();
    }

    private void HardwareInit()
    {
        _transferMotion.Initialize(0, 1, 2);
        _transferMotion.On();
        _fiducialMotion.Initialize(3, 4, 5);
        _fiducialMotion.On();
        _ioService.Initiliaze();
        Stats.StartTime = DateTime.Now;
    }

    // ── 공정 제어 ────────────────────────────────────────────────────────────
    public async Task StartAsync()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        AddLog("공정 시작", ProcessStage.Idle);

        try { await RunProcessLoopAsync(_cts.Token); }
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
        _transferMotion.Stop();
        _fiducialMotion.Stop();
        AddLog("정지 요청", ProcessStage.Idle, LogLevel.Warning);
    }

    public void EStop()
    {
        _cts?.Cancel();
        _transferMotion.EStop();
        _fiducialMotion.EStop();
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

    // ── 메인 공정 루프 (파이프라인) ──────────────────────────────────────────
    private async Task RunProcessLoopAsync(CancellationToken ct)
    {
        // 부트스트랩: 첫 PCB를 볼트 스테이션까지 이송
        var currentPickFiducial = await DoPickupPhaseAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            var cycleStart = DateTime.Now;
            var boltResults = new List<BoltStepResult>();

            // ── 병렬 실행 ────────────────────────────────────────────────────
            // A) 볼트 스테이션 (fiducialMotion): FiducialBolt → [Tighten→Vision]×N → FinalInspect
            // B) 다음 PCB 이송 (transferMotion): LineArrival → FiducialPick → PickTransfer → Conveyor
            //
            // 주의: FiducialForPick도 fiducialMotion을 사용하므로 가상 환경에서는 협력적으로 동작.
            //       실 하드웨어에서는 BoltTighten 완료 후 FiducialForPick이 실행되도록 축 잠금 필요.
            var boltTask    = DoBoltStationPhaseAsync(boltResults, ct);
            var pickupTask  = DoPickupPhaseAsync(ct);

            await Task.WhenAll(boltTask, pickupTask);
            ct.ThrowIfCancellationRequested();

            var finalResult = boltTask.Result;
            currentPickFiducial = pickupTask.Result;

            // ── 라우팅 ───────────────────────────────────────────────────────
            LastRoute = finalResult;
            RouteDecided?.Invoke(this, finalResult);

            await DoRoutePhaseAsync(finalResult, boltResults, cycleStart, ct);
        }
    }

    // ── Step 1~4: Transfer 픽업 페이즈 ──────────────────────────────────────
    private async Task<FiducialResult?> DoPickupPhaseAsync(CancellationToken ct)
    {
        FiducialResult? pickFiducial = null;

        await DoStageAsync(ProcessStage.LineArrivalCheck, ct, async () =>
        {
            AddLog("Line 1 / Line 2 PCB 도착 대기...", ProcessStage.LineArrivalCheck);
            await WaitLineArrivalAsync(ct);
            AddLog("Line 1 + Line 2 도착 확인 → Transfer 시작", ProcessStage.LineArrivalCheck);
        });

        await DoStageAsync(ProcessStage.FiducialForPick, ct, async () =>
        {
            AddLog("픽업용 Fiducial 검출 (X,Y,Z 이동)", ProcessStage.FiducialForPick);
            pickFiducial = await FiducialCheckAsync(CurrentRecipe.FiducialForPickPos, ct);
            FiducialDetected?.Invoke(this, pickFiducial);
            if (pickFiducial.Found)
                AddLog($"픽업 Fiducial  dX:{pickFiducial.OffsetX:+0.000;-0.000}  dY:{pickFiducial.OffsetY:+0.000;-0.000}  ({pickFiducial.Confidence:P0})", ProcessStage.FiducialForPick);
            else
                AddLog("픽업 Fiducial 검출 실패", ProcessStage.FiducialForPick, LogLevel.Warning);
        });

        await DoStageAsync(ProcessStage.PickAndTransfer, ct, async () =>
        {
            AddLog("Line 1 PCB 픽업 (보정 좌표 적용, X,Y,Z)", ProcessStage.PickAndTransfer);
            await PickAndTransferAsync(pickFiducial, ct);
            AddLog("우리 라인 방열판 위 PCB 배치 완료", ProcessStage.PickAndTransfer);
        });

        await DoStageAsync(ProcessStage.ConveyorToBoltStation, ct, async () =>
        {
            AddLog("컨베이어 구동 → 볼트 체결 스테이션", ProcessStage.ConveyorToBoltStation);
            await ConveyorMoveAsync(ct);
            AddLog("볼트 체결 스테이션 도달", ProcessStage.ConveyorToBoltStation);
        });

        return pickFiducial;
    }

    // ── Step 5~8: 볼트 스테이션 페이즈 ─────────────────────────────────────
    private async Task<InspectionResult> DoBoltStationPhaseAsync(
        List<BoltStepResult> boltResults, CancellationToken ct)
    {
        FiducialResult? boltFiducial = null;

        await DoStageAsync(ProcessStage.FiducialForBolt, ct, async () =>
        {
            AddLog("볼트용 Fiducial 검출 (X,Y,Z 이동)", ProcessStage.FiducialForBolt);
            boltFiducial = await FiducialCheckAsync(CurrentRecipe.FiducialForBoltPos, ct);
            FiducialDetected?.Invoke(this, boltFiducial);
            if (boltFiducial.Found)
                AddLog($"볼트 Fiducial  dX:{boltFiducial.OffsetX:+0.000;-0.000}  dY:{boltFiducial.OffsetY:+0.000;-0.000}  ({boltFiducial.Confidence:P0})", ProcessStage.FiducialForBolt);
            else
                AddLog("볼트 Fiducial 검출 실패", ProcessStage.FiducialForBolt, LogLevel.Warning);
        });

        // ── [볼트 체결 → 비전 검사] 반복 ──────────────────────────────────
        var boltPoints = CurrentRecipe.BoltPoints;
        for (int i = 0; i < boltPoints.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var bp = boltPoints[i];
            BoltResult? boltResult = null;
            bool visionOk = false;

            await DoStageAsync(ProcessStage.BoltTighten, ct, async () =>
            {
                BoltProgress?.Invoke(this, new BoltProgressEventArgs(i + 1, boltPoints.Count, bp.Name));
                AddLog($"[볼트 {i + 1}/{boltPoints.Count}  {bp.Name}]  보정 위치로 X,Y,Z 이동", ProcessStage.BoltTighten);

                double corrX = bp.X + (boltFiducial?.OffsetX ?? 0);
                double corrY = bp.Y + (boltFiducial?.OffsetY ?? 0);

                await _fiducialMotion.MoveXY(corrX, corrY, 80.0);
                await _fiducialMotion.MoveZ(bp.Z, 50.0);
                ct.ThrowIfCancellationRequested();
                FireFiducialPos();

                AddLog($"[{bp.Name}]  Shooting", ProcessStage.BoltTighten);
                await _boltService.ShootAsync(ct);

                AddLog($"[{bp.Name}]  Tightening  목표: {bp.TargetTorqueNm:F1} Nm", ProcessStage.BoltTighten);
                boltResult = await _boltService.TightenAsync(bp.TargetTorqueNm, ct);
                BoltCompleted?.Invoke(this, boltResult);

                var lvl = boltResult.Success ? LogLevel.Info : LogLevel.Warning;
                AddLog($"[{bp.Name}]  체결 완료  실측: {boltResult.Torque:F2} Nm  [{boltResult.Message}]",
                    ProcessStage.BoltTighten, lvl);
            });

            await DoStageAsync(ProcessStage.BoltVisionInspect, ct, async () =>
            {
                AddLog($"[{bp.Name}]  체결 위치 비전 검사", ProcessStage.BoltVisionInspect);
                visionOk = await BoltVisionInspectAsync(ct);
                var lvl = visionOk ? LogLevel.Info : LogLevel.Warning;
                AddLog($"[{bp.Name}]  비전 검사: {(visionOk ? "OK" : "NG")}", ProcessStage.BoltVisionInspect, lvl);
            });

            boltResults.Add(new BoltStepResult
            {
                Point = bp,
                ActualTorque = boltResult?.Torque ?? 0,
                TorqueOk = boltResult?.Success ?? false,
                VisionOk = visionOk
            });
        }

        // ── 최종 비전 검사 ─────────────────────────────────────────────────
        InspectionResult finalResult = InspectionResult.Unknown;
        await DoStageAsync(ProcessStage.FinalVisionInspect, ct, async () =>
        {
            AddLog("최종 비전 검사 시작", ProcessStage.FinalVisionInspect);
            finalResult = await FinalVisionInspectAsync(boltResults, ct);
            InspectionDone?.Invoke(this, finalResult);

            var lvl  = finalResult == InspectionResult.Good ? LogLevel.Info : LogLevel.Warning;
            var text = finalResult == InspectionResult.Good ? "▶ GOOD ◀" : "▶  NG  ◀";
            AddLog($"최종 검사 결과: {text}", ProcessStage.FinalVisionInspect, lvl);
        });

        return finalResult;
    }

    // ── 라우팅 페이즈 (NG / Good) ────────────────────────────────────────────
    private async Task DoRoutePhaseAsync(
        InspectionResult finalResult, List<BoltStepResult> boltResults,
        DateTime cycleStart, CancellationToken ct)
    {
        if (finalResult != InspectionResult.Good)
        {
            // NG 경로
            await DoStageAsync(ProcessStage.ConveyorToNg, ct, async () =>
            {
                AddLog("NG → 컨베이어 이동 → NG 스테이지", ProcessStage.ConveyorToNg);
                await ConveyorMoveAsync(ct);
            });

            await DoStageAsync(ProcessStage.NgTransfer, ct, async () =>
            {
                AddLog("NG Transfer Y,Z 이동 → NG 적재 위치", ProcessStage.NgTransfer);
                await NgTransferAsync(ct);

                NgStackCount++;
                NgStackUpdated?.Invoke(this, NgStackCount);
                AddLog($"NG 적재 완료  ({NgStackCount}/{CurrentRecipe.NgStackAlarmCount})",
                    ProcessStage.NgTransfer, LogLevel.Warning);

                if (NgStackCount >= CurrentRecipe.NgStackAlarmCount)
                {
                    AddLog($"NG 적재 {NgStackCount}개 → 설비 알람! 작업자 확인 필요",
                        ProcessStage.NgTransfer, LogLevel.Error);
                    NgStackAlarm?.Invoke(this, NgStackCount);
                    // TODO: IO 센서 확인 후 작업자 리셋 대기
                }
            });
        }
        else
        {
            // Good 경로
            await DoStageAsync(ProcessStage.ConveyorToGood, ct, async () =>
            {
                AddLog("GOOD → 컨베이어 이동 → 다음 스테이지", ProcessStage.ConveyorToGood);
                await ConveyorMoveAsync(ct);
            });

            await DoStageAsync(ProcessStage.SmemaWait, ct, async () =>
            {
                AddLog("뒤 설비 SMEMA 신호 대기 (Board Available)...", ProcessStage.SmemaWait);
                await WaitSmemaAsync(ct);
                AddLog("SMEMA OK → 배출 준비", ProcessStage.SmemaWait);
            });

            await DoStageAsync(ProcessStage.Discharge, ct, async () =>
            {
                AddLog("SMEMA에 따라 배출 (컨베이어 이동)", ProcessStage.Discharge);
                await DischargeAsync(ct);
                AddLog("배출 완료", ProcessStage.Discharge);
            });
        }

        // ── 사이클 통계 ─────────────────────────────────────────────────────
        var elapsed = (DateTime.Now - cycleStart).TotalSeconds;
        Stats.TotalCount++;
        if (finalResult == InspectionResult.Good) Stats.GoodCount++;
        else Stats.NgCount++;
        Stats.LastCycleTimeSeconds = elapsed;
        Stats.AverageCycleTimeSeconds = Stats.AverageCycleTimeSeconds == 0
            ? elapsed : Stats.AverageCycleTimeSeconds * 0.7 + elapsed * 0.3;

        StatsUpdated?.Invoke(this, Stats);
        Transition(ProcessStage.Complete, StageStatus.Done);

        var ngFailed = boltResults.Count(r => !r.IsOk);
        AddLog($"사이클 완료  CT:{elapsed:F1}s  누적:{Stats.TotalCount:N0}  볼트NG:{ngFailed}/{boltResults.Count}",
            ProcessStage.Complete);

        await Task.Delay(300, ct);
        Transition(ProcessStage.Idle, StageStatus.Idle);
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

    // ── 개별 스테이지 구현 ──────────────────────────────────────────────────

    private async Task WaitLineArrivalAsync(CancellationToken ct)
    {
        // 업스트림 2개 라인 모두 도착 확인 후 Line 1의 PCB를 Transfer
        // Line 1과 Line 2는 독립 IO 센서로 각각 모니터링

        // Line 1 도착 대기
        // TODO: while (!_ioService.GetIn(IoMap.Line1Sensor)) { await Task.Delay(50, ct); }
        await Task.Delay(400, ct);
        Line1SensorChanged?.Invoke(this, true);
        AddLog("Line 1 PCB 도착 확인", ProcessStage.LineArrivalCheck);

        // Line 2 도착 대기 (Line 1과 거의 동시 or 약간의 지연)
        // TODO: while (!_ioService.GetIn(IoMap.Line2Sensor)) { await Task.Delay(50, ct); }
        await Task.Delay(300, ct);
        Line2SensorChanged?.Invoke(this, true);
        AddLog("Line 2 PCB 도착 확인 → 2개 라인 모두 준비 완료", ProcessStage.LineArrivalCheck);

        // 두 라인 도착 완료 후 센서 리셋 (다음 사이클을 위해)
        // TODO: _ioService.Set(IoMap.Line1ConveyorStop, true); etc.
    }

    private async Task<FiducialResult> FiducialCheckAsync(AxisPos pos, CancellationToken ct)
    {
        await _fiducialMotion.MoveXY(pos.X, pos.Y, 100.0);
        await _fiducialMotion.MoveZ(pos.Z, 60.0);
        ct.ThrowIfCancellationRequested();
        FireFiducialPos();
        return await _fiducialService.DetectFromCameraAsync(ct);
    }

    private async Task PickAndTransferAsync(FiducialResult? fiducial, CancellationToken ct)
    {
        double pickX = CurrentRecipe.PickPos.X + (fiducial?.OffsetX ?? 0);
        double pickY = CurrentRecipe.PickPos.Y + (fiducial?.OffsetY ?? 0);

        await _transferMotion.MoveXY(pickX, pickY, 120.0);
        await _transferMotion.MoveZ(CurrentRecipe.PickPos.Z, 60.0);
        ct.ThrowIfCancellationRequested();
        FireTransferPos();

        // TODO: 그리퍼 닫기 IO (PCB 픽업)
        await Task.Delay(200, ct);

        await _transferMotion.MoveZ(0, 80.0);
        double placeX = CurrentRecipe.PlacePos.X + (fiducial?.OffsetX ?? 0);
        double placeY = CurrentRecipe.PlacePos.Y + (fiducial?.OffsetY ?? 0);
        await _transferMotion.MoveXY(placeX, placeY, 100.0);
        await _transferMotion.MoveZ(CurrentRecipe.PlacePos.Z, 50.0);
        ct.ThrowIfCancellationRequested();
        FireTransferPos();

        // TODO: 그리퍼 열기 IO (PCB 방열판 위에 배치)
        await Task.Delay(150, ct);
        await _transferMotion.MoveZ(0, 80.0);
        FireTransferPos();
    }

    private async Task ConveyorMoveAsync(CancellationToken ct)
    {
        // TODO: IO 확정 후 컨베이어 구동 출력 ON, 도달 센서 대기, 정지
        await Task.Delay(1000, ct);
    }

    private async Task<bool> BoltVisionInspectAsync(CancellationToken ct)
    {
        // TODO: BaslerService 카메라로 체결 위치 비전 검사
        await Task.Delay(300, ct);
        return Random.Shared.NextDouble() > 0.3; // 시뮬: 97% OK
    }

    private async Task<InspectionResult> FinalVisionInspectAsync(
        List<BoltStepResult> results, CancellationToken ct)
    {
        await Task.Delay(500, ct);
        return results.All(r => r.IsOk) ? InspectionResult.Good : InspectionResult.Ng;
    }

    private async Task NgTransferAsync(CancellationToken ct)
    {
        // NG Transfer: Y, Z 이동만 (X 고정)
        await _transferMotion.MoveY(CurrentRecipe.NgStackY, 80.0);
        await _transferMotion.MoveZ(CurrentRecipe.NgStackZ, 50.0);
        ct.ThrowIfCancellationRequested();
        FireTransferPos();

        // TODO: PCB 릴리즈 IO
        await Task.Delay(200, ct);

        // TODO: IO 체크: NG 스택 센서 확인
        await _transferMotion.MoveZ(0, 80.0);
        FireTransferPos();
    }

    private async Task WaitSmemaAsync(CancellationToken ct)
    {
        // TODO: IO 확정 후 SMEMA MachineReady 신호 대기
        await Task.Delay(600, ct);
    }

    private async Task DischargeAsync(CancellationToken ct)
    {
        // TODO: SMEMA BoardAvailable 신호 ON → 컨베이어 배출
        await ConveyorMoveAsync(ct);
    }

    // ── 헬퍼 ────────────────────────────────────────────────────────────────
    private void Transition(ProcessStage stage, StageStatus status)
    {
        _currentStage = stage;
        StageChanged?.Invoke(this, new StageChangedEventArgs(stage, status));
    }

    private void FireTransferPos()
    {
        _transferMotion.GetPotision(out var x, out var y, out var z);
        TransferPositionChanged?.Invoke(this, ((x ?? 0) / 1000.0, (y ?? 0) / 1000.0, (z ?? 0) / 1000.0));
    }

    private void FireFiducialPos()
    {
        _fiducialMotion.GetPotision(out var x, out var y, out var z);
        FiducialPositionChanged?.Invoke(this, ((x ?? 0) / 1000.0, (y ?? 0) / 1000.0, (z ?? 0) / 1000.0));
    }

    private void AddLog(string message, ProcessStage stage, LogLevel level = LogLevel.Info)
    {
        LogAdded?.Invoke(this, new LogEntry { Message = message, Stage = stage.ToString(), Level = level });
    }
}
