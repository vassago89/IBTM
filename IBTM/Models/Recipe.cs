namespace IBTM.Models;

/// <summary>볼트 체결 포인트 1개 정의</summary>
public class BoltPoint
{
    public string Name { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double TargetTorqueNm { get; set; }
    public double ToranceNm { get; set; } = 0.5;
}

/// <summary>축 위치 (X, Y, Z)</summary>
public class AxisPos
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}

/// <summary>
/// 레시피: 설비 동작의 모든 위치/파라미터 정의
/// 레시피별 볼트 수, 위치, 토크가 달라짐
/// </summary>
public class Recipe
{
    public string Name { get; set; } = "Default";

    // ── Fiducial 검출 위치 ─────────────────────────────────────────────────
    /// <summary>픽업용 Fiducial 카메라 이동 위치</summary>
    public AxisPos FiducialForPickPos { get; set; } = new() { X = 50.0, Y = 50.0, Z = 10.0 };

    /// <summary>볼트용 Fiducial 카메라 이동 위치</summary>
    public AxisPos FiducialForBoltPos { get; set; } = new() { X = 50.0, Y = 50.0, Z = 10.0 };

    // ── 픽업 / 방열판 배치 위치 ────────────────────────────────────────────
    /// <summary>PCB 픽업 기준 위치 (Fiducial 보정값 적용 전)</summary>
    public AxisPos PickPos { get; set; } = new() { X = 100.0, Y = 100.0, Z = 30.0 };

    /// <summary>방열판 위 배치 기준 위치 (Fiducial 보정값 적용 전)</summary>
    public AxisPos PlacePos { get; set; } = new() { X = 150.0, Y = 50.0, Z = 30.0 };

    // ── 볼트 체결 포인트 목록 ──────────────────────────────────────────────
    /// <summary>
    /// 볼트 체결 순서 및 위치 목록
    /// 체결 → 비전 검사를 이 순서대로 반복
    /// </summary>
    public List<BoltPoint> BoltPoints { get; set; } =
    [
        new() { Name = "B1", X =  80.0, Y = 60.0, Z = 20.0, TargetTorqueNm = 15.0 },
        new() { Name = "B2", X = 120.0, Y = 60.0, Z = 20.0, TargetTorqueNm = 15.0 },
        new() { Name = "B3", X = 120.0, Y = 90.0, Z = 20.0, TargetTorqueNm = 15.0 },
        new() { Name = "B4", X =  80.0, Y = 90.0, Z = 20.0, TargetTorqueNm = 15.0 },
    ];

    // ── NG 적재 위치 (Y, Z 이동) ───────────────────────────────────────────
    public double NgStackY { get; set; } = 200.0;
    public double NgStackZ { get; set; } = 50.0;
    /// <summary>NG 적재 최대 수량 초과 시 설비 알람</summary>
    public int NgStackAlarmCount { get; set; } = 3;

    // ── SMEMA ──────────────────────────────────────────────────────────────
    /// <summary>뒤 설비 SMEMA 신호 대기 타임아웃 (초)</summary>
    public int SmemaTimeoutSeconds { get; set; } = 30;
}

/// <summary>볼트 1개 체결 결과</summary>
public class BoltStepResult
{
    public BoltPoint Point { get; init; } = new();
    public required double ActualTorque { get; init; }
    public required bool TorqueOk { get; init; }
    public required bool VisionOk { get; init; }
    public bool IsOk => TorqueOk && VisionOk;
}
