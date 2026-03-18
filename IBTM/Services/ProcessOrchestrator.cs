using IBTM.Device;
using IBTM.Models;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM.Services;

/// <summary>
/// 3-zone pipeline orchestrator
///
///   Zone 1 (zone1Motion): Pick 2 PCBs from previous equipment shuttle → place on our shuttle
///   Zone 2 (zone2Motion): Bolt tightening (Fiducial correction → N-point tightening)
///   Zone 3 (zone3Motion): Camera inspection → NG stack / Good SMEMA discharge
///
///   Each zone has independent XYZ motion axes; shuttles move on a shared conveyor.
///   When a shuttle arrives: Stopper → Align → Lift up → Work → Lift down → Stopper release.
///   All 3 zones operate concurrently (each on a different shuttle).
/// </summary>
public class ProcessOrchestrator
{
    private readonly IMotionService _zone1Motion;   // Zone 1: Pick XYZ
    private readonly IMotionService _zone2Motion;   // Zone 2: Bolt tighten XYZ
    private readonly IMotionService _zone3Motion;   // Zone 3: Inspect XYZ
    private readonly IIOService _ioService;
    private readonly IFiducialService _fiducialService;
    private readonly IBoltService _boltService;

    private CancellationTokenSource? _cts;

    // ── Events ──────────────────────────────────────────────────────────────
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

    // ── State ────────────────────────────────────────────────────────────────
    public bool IsRunning => _cts is { IsCancellationRequested: false };
    public ProductionStats Stats { get; } = new();
    public int NgStackCount { get; private set; }

    // ── Config ────────────────────────────────────────────────────────────────
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

    // ── Process control ────────────────────────────────────────────────────────────
    public async Task StartAsync()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        AddLog("Process started", ProcessStage.Idle);

        try { await RunPipelineAsync(_cts.Token); }
        catch (OperationCanceledException)
        {
            AddLog("Process stopped", ProcessStage.Idle);
            Transition(ProcessStage.Idle, StageStatus.Idle);
        }
        catch (Exception ex)
        {
            AddLog($"Process error: {ex.Message}", ProcessStage.Error, LogLevel.Error);
            Transition(ProcessStage.Error, StageStatus.Error);
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _zone1Motion.Stop();
        _zone2Motion.Stop();
        _zone3Motion.Stop();
        AddLog("Stop requested", ProcessStage.Idle, LogLevel.Warning);
    }

    public void EStop()
    {
        _cts?.Cancel();
        _zone1Motion.EStop();
        _zone2Motion.EStop();
        _zone3Motion.EStop();
        _ioService.Off();
        AddLog("Emergency stop (E-STOP)!", ProcessStage.Idle, LogLevel.Error);
        Transition(ProcessStage.Idle, StageStatus.Error);
    }

    public void ResetNgStack()
    {
        NgStackCount = 0;
        NgStackUpdated?.Invoke(this, 0);
        AddLog("NG stack count reset", ProcessStage.Idle);
    }

    // ── Main pipeline ──────────────────────────────────────────────────────
    /// <summary>
    /// 3-zone parallel pipeline.
    /// Each zone runs an independent loop, performing work when a shuttle arrives.
    /// </summary>
    private async Task RunPipelineAsync(CancellationToken ct)
    {
        var zone1Task = RunZone1LoopAsync(ct);
        var zone2Task = RunZone2LoopAsync(ct);
        var zone3Task = RunZone3LoopAsync(ct);

        await Task.WhenAll(zone1Task, zone2Task, zone3Task);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Zone 1: Pick
    // ══════════════════════════════════════════════════════════════════════════
    private async Task RunZone1LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Wait for shuttle arrival + previous equipment rear lane sensor
            await DoStageAsync(ProcessStage.Zone1_WaitShuttle, ct, async () =>
            {
                AddLog("[Zone1] Waiting for shuttle + prev equip lane sensor", ProcessStage.Zone1_WaitShuttle);
                await WaitForSensorAsync(IoMap.Zone1_Sensor, ct);
                await WaitForSensorAsync(IoMap.PrevRearLaneSensor, ct);
                AddLog("[Zone1] Shuttle + prev equip ready", ProcessStage.Zone1_WaitShuttle);
            });

            // Stopper → Align → Lift up
            await DoStageAsync(ProcessStage.Zone1_StopAlignLift, ct, async () =>
            {
                AddLog("[Zone1] Stopper → Align → Lift up", ProcessStage.Zone1_StopAlignLift);
                await StopAlignLiftAsync(IoMap.Zone1_Stopper, IoMap.Zone1_Align, IoMap.Zone1_Lift, ct);
            });

            // Pick & place 2 PCBs
            await DoStageAsync(ProcessStage.Zone1_PickPlace, ct, async () =>
            {
                AddLog("[Zone1] PCB #1 pick", ProcessStage.Zone1_PickPlace);
                await PickAndPlaceAsync(
                    _zone1Motion, CurrentRecipe.Zone1_PickPos1, CurrentRecipe.Zone1_PlacePos1,
                    IoMap.Zone1_Gripper, 1, ct);

                AddLog("[Zone1] PCB #2 pick", ProcessStage.Zone1_PickPlace);
                await PickAndPlaceAsync(
                    _zone1Motion, CurrentRecipe.Zone1_PickPos2, CurrentRecipe.Zone1_PlacePos2,
                    IoMap.Zone1_Gripper, 1, ct);

                AddLog("[Zone1] 2 PCBs placed", ProcessStage.Zone1_PickPlace);
            });

            // Lift down → Stopper release
            await DoStageAsync(ProcessStage.Zone1_Release, ct, async () =>
            {
                AddLog("[Zone1] Lift down → Stopper release", ProcessStage.Zone1_Release);
                await ReleaseAsync(IoMap.Zone1_Stopper, IoMap.Zone1_Align, IoMap.Zone1_Lift, ct);
            });
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Zone 2: Bolt tightening
    // ══════════════════════════════════════════════════════════════════════════
    private async Task RunZone2LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Wait for shuttle arrival
            await DoStageAsync(ProcessStage.Zone2_WaitShuttle, ct, async () =>
            {
                AddLog("[Zone2] Waiting for shuttle", ProcessStage.Zone2_WaitShuttle);
                await WaitForSensorAsync(IoMap.Zone2_Sensor, ct);
                AddLog("[Zone2] Shuttle arrived", ProcessStage.Zone2_WaitShuttle);
            });

            // Stopper → Align → Lift up
            await DoStageAsync(ProcessStage.Zone2_StopAlignLift, ct, async () =>
            {
                AddLog("[Zone2] Stopper → Align → Lift up", ProcessStage.Zone2_StopAlignLift);
                await StopAlignLiftAsync(IoMap.Zone2_Stopper, IoMap.Zone2_Align, IoMap.Zone2_Lift, ct);
            });

            // Fiducial detection
            FiducialResult? fiducial = null;
            await DoStageAsync(ProcessStage.Zone2_Fiducial, ct, async () =>
            {
                AddLog("[Zone2] Fiducial detection", ProcessStage.Zone2_Fiducial);
                var pos = CurrentRecipe.Zone2_FiducialPos;
                await _zone2Motion.MoveXY(pos.X, pos.Y, 100.0);
                FireZonePos(2);
                await _zone2Motion.MoveZ(pos.Z, 60.0);
                ct.ThrowIfCancellationRequested();
                FireZonePos(2);

                fiducial = await _fiducialService.DetectFromCameraAsync(ct);
                FiducialDetected?.Invoke(this, fiducial);

                if (fiducial.Found)
                    AddLog($"[Zone2] Fiducial  dX:{fiducial.OffsetX:+0.000;-0.000}  dY:{fiducial.OffsetY:+0.000;-0.000}  ({fiducial.Confidence:P0})", ProcessStage.Zone2_Fiducial);
                else
                    AddLog("[Zone2] Fiducial detection failed", ProcessStage.Zone2_Fiducial, LogLevel.Warning);
            });

            // Bolt tightening loop
            await DoStageAsync(ProcessStage.Zone2_BoltTighten, ct, async () =>
            {
                var boltPoints = CurrentRecipe.BoltPoints;
                for (int i = 0; i < boltPoints.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var bp = boltPoints[i];

                    BoltProgress?.Invoke(this, new BoltProgressEventArgs(i + 1, boltPoints.Count, bp.Name));
                    AddLog($"[Zone2] Bolt {i + 1}/{boltPoints.Count} [{bp.Name}] moving", ProcessStage.Zone2_BoltTighten);

                    double corrX = bp.X + (fiducial?.OffsetX ?? 0);
                    double corrY = bp.Y + (fiducial?.OffsetY ?? 0);

                    await _zone2Motion.MoveXY(corrX, corrY, 80.0);
                    FireZonePos(2);
                    await _zone2Motion.MoveZ(bp.Z, 50.0);
                    ct.ThrowIfCancellationRequested();
                    FireZonePos(2);

                    AddLog($"[Zone2] [{bp.Name}] Shooting", ProcessStage.Zone2_BoltTighten);
                    await _boltService.ShootAsync(ct);

                    AddLog($"[Zone2] [{bp.Name}] Tightening  target: {bp.TargetTorqueNm:F1} Nm", ProcessStage.Zone2_BoltTighten);
                    var boltResult = await _boltService.TightenAsync(bp.TargetTorqueNm, ct);
                    BoltCompleted?.Invoke(this, boltResult);

                    var lvl = boltResult.Success ? LogLevel.Info : LogLevel.Warning;
                    AddLog($"[Zone2] [{bp.Name}] Tighten done  actual: {boltResult.Torque:F2} Nm  [{boltResult.Message}]",
                        ProcessStage.Zone2_BoltTighten, lvl);

                    await _zone2Motion.MoveZ(0, 80.0);
                    FireZonePos(2);
                }

                AddLog("[Zone2] All bolts tightened", ProcessStage.Zone2_BoltTighten);
            });

            // Lift down → Stopper release
            await DoStageAsync(ProcessStage.Zone2_Release, ct, async () =>
            {
                AddLog("[Zone2] Lift down → Stopper release", ProcessStage.Zone2_Release);
                await ReleaseAsync(IoMap.Zone2_Stopper, IoMap.Zone2_Align, IoMap.Zone2_Lift, ct);
            });
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Zone 3: Inspection
    // ══════════════════════════════════════════════════════════════════════════
    private async Task RunZone3LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var cycleStart = DateTime.Now;

            // Wait for shuttle arrival
            await DoStageAsync(ProcessStage.Zone3_WaitShuttle, ct, async () =>
            {
                AddLog("[Zone3] Waiting for shuttle", ProcessStage.Zone3_WaitShuttle);
                await WaitForSensorAsync(IoMap.Zone3_Sensor, ct);
                AddLog("[Zone3] Shuttle arrived", ProcessStage.Zone3_WaitShuttle);
            });

            // Stopper → Align → Lift up
            await DoStageAsync(ProcessStage.Zone3_StopAlignLift, ct, async () =>
            {
                AddLog("[Zone3] Stopper → Align → Lift up", ProcessStage.Zone3_StopAlignLift);
                await StopAlignLiftAsync(IoMap.Zone3_Stopper, IoMap.Zone3_Align, IoMap.Zone3_Lift, ct);
            });

            // Camera inspection
            InspectionResult inspectResult = InspectionResult.Unknown;
            await DoStageAsync(ProcessStage.Zone3_Inspect, ct, async () =>
            {
                AddLog("[Zone3] Camera inspection start (bolt presence)", ProcessStage.Zone3_Inspect);
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
                AddLog($"[Zone3] Inspection result: {text}", ProcessStage.Zone3_Inspect, lvl);
            });

            // NG / Good branching
            if (inspectResult != InspectionResult.Good)
            {
                // NG → rear stack
                await DoStageAsync(ProcessStage.Zone3_NgTransfer, ct, async () =>
                {
                    AddLog("[Zone3] NG → rear stack transfer", ProcessStage.Zone3_NgTransfer);

                    // Gripper pick PCB → move to NG stack position
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
                    AddLog($"[Zone3] NG stacked ({NgStackCount}/{CurrentRecipe.NgStackMaxCount})",
                        ProcessStage.Zone3_NgTransfer, LogLevel.Warning);

                    if (NgStackCount >= CurrentRecipe.NgStackMaxCount)
                    {
                        AddLog($"[Zone3] NG stack {NgStackCount} full → Equipment alarm! Operator check required",
                            ProcessStage.Zone3_NgTransfer, LogLevel.Error);
                        NgStackAlarm?.Invoke(this, NgStackCount);
                    }
                });
            }
            else
            {
                // Good → SMEMA wait → discharge
                await DoStageAsync(ProcessStage.Zone3_SmemaWait, ct, async () =>
                {
                    AddLog("[Zone3] Waiting for SMEMA signal...", ProcessStage.Zone3_SmemaWait);
                    await WaitForSensorAsync(IoMap.Smema_MachineReady, ct);
                    AddLog("[Zone3] SMEMA OK → ready to discharge", ProcessStage.Zone3_SmemaWait);
                });

                await DoStageAsync(ProcessStage.Zone3_Release, ct, async () =>
                {
                    AddLog("[Zone3] Lift down → Stopper release (discharge)", ProcessStage.Zone3_Release);
                    await ReleaseAsync(IoMap.Zone3_Stopper, IoMap.Zone3_Align, IoMap.Zone3_Lift, ct);
                });

                await DoStageAsync(ProcessStage.Zone3_Discharge, ct, async () =>
                {
                    AddLog("[Zone3] Discharging (SMEMA BoardAvailable)", ProcessStage.Zone3_Discharge);
                    _ioService.Set(IoMap.Smema_BoardAvailable, true);
                    await Task.Delay(500, ct);
                    _ioService.Set(IoMap.Smema_BoardAvailable, false);
                    AddLog("[Zone3] Discharge complete", ProcessStage.Zone3_Discharge);
                });
            }

            // NG path also needs lift down / stopper release
            if (inspectResult != InspectionResult.Good)
            {
                await DoStageAsync(ProcessStage.Zone3_Release, ct, async () =>
                {
                    AddLog("[Zone3] Lift down → Stopper release", ProcessStage.Zone3_Release);
                    await ReleaseAsync(IoMap.Zone3_Stopper, IoMap.Zone3_Align, IoMap.Zone3_Lift, ct);
                });
            }

            // Cycle stats (aggregated at Zone 3)
            var elapsed = (DateTime.Now - cycleStart).TotalSeconds;
            Stats.TotalCount++;
            if (inspectResult == InspectionResult.Good) Stats.GoodCount++;
            else Stats.NgCount++;
            Stats.LastCycleTimeSeconds = elapsed;
            Stats.AverageCycleTimeSeconds = Stats.AverageCycleTimeSeconds == 0
                ? elapsed : Stats.AverageCycleTimeSeconds * 0.7 + elapsed * 0.3;

            StatsUpdated?.Invoke(this, Stats);
            Transition(ProcessStage.Complete, StageStatus.Done);

            AddLog($"[Zone3] Cycle complete  CT:{elapsed:F1}s  total:{Stats.TotalCount:N0}",
                ProcessStage.Complete);

            await Task.Delay(300, ct);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Common operations
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Wait for sensor input (sim: auto-ON after 400ms)</summary>
    private async Task WaitForSensorAsync(int sensorIndex, CancellationToken ct)
    {
        // TODO: while (!_ioService.GetIn(sensorIndex)) { await Task.Delay(50, ct); }
        await Task.Delay(400, ct);
    }

    /// <summary>Stopper ON → Align ON → Lift UP</summary>
    private async Task StopAlignLiftAsync(int stopper, int align, int lift, CancellationToken ct)
    {
        _ioService.Set(stopper, true);
        await Task.Delay(200, ct);
        _ioService.Set(align, true);
        await Task.Delay(300, ct);
        _ioService.Set(lift, true);
        await Task.Delay(500, ct);
    }

    /// <summary>Lift DOWN → Align OFF → Stopper OFF</summary>
    private async Task ReleaseAsync(int stopper, int align, int lift, CancellationToken ct)
    {
        _ioService.Set(lift, false);
        await Task.Delay(500, ct);
        _ioService.Set(align, false);
        await Task.Delay(200, ct);
        _ioService.Set(stopper, false);
        await Task.Delay(200, ct);
    }

    /// <summary>Move to pick position → close gripper → move to place position → open gripper</summary>
    private async Task PickAndPlaceAsync(
        IMotionService motion, AxisPos pick, AxisPos place,
        int gripperIo, int zone, CancellationToken ct)
    {
        // Move to pick position
        await motion.MoveXY(pick.X, pick.Y, 120.0);
        FireZonePos(zone);
        await motion.MoveZ(pick.Z, 60.0);
        ct.ThrowIfCancellationRequested();
        FireZonePos(zone);

        // Close gripper (PCB pick)
        _ioService.Set(gripperIo, true);
        GripperChanged?.Invoke(this, (zone, true));
        await Task.Delay(200, ct);

        // Z return → move to place position
        await motion.MoveZ(0, 80.0);
        FireZonePos(zone);
        await motion.MoveXY(place.X, place.Y, 100.0);
        FireZonePos(zone);
        await motion.MoveZ(place.Z, 50.0);
        ct.ThrowIfCancellationRequested();
        FireZonePos(zone);

        // Open gripper (PCB place)
        _ioService.Set(gripperIo, false);
        GripperChanged?.Invoke(this, (zone, false));
        await Task.Delay(150, ct);

        // Z return
        await motion.MoveZ(0, 80.0);
        FireZonePos(zone);
    }

    /// <summary>Camera vision inspection (bolt presence)</summary>
    private async Task<InspectionResult> VisionInspectAsync(CancellationToken ct)
    {
        // TODO: Inspect bolt presence via BaslerService camera
        await Task.Delay(500, ct);
        return Random.Shared.NextDouble() > 0.15
            ? InspectionResult.Good
            : InspectionResult.Ng;
    }

    // ── Stage execution wrapper ─────────────────────────────────────────────────
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
            AddLog($"[{stage}] Error: {ex.Message}", stage, LogLevel.Error);
            Transition(stage, StageStatus.Error);
            throw;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────
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
