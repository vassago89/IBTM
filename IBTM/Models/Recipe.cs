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
/// 레시피: 3구간 설비 동작의 모든 위치/파라미터 정의
/// </summary>
public class Recipe
{
    public string Name { get; set; } = "Default";

    // ── 구간 1: 픽업 위치 ────────────────────────────────────────────
    /// <summary>앞장비 셔틀에서 PCB 1번 픽업 위치</summary>
    public AxisPos Zone1_PickPos1 { get; set; } = new() { X = 50.0, Y = 30.0, Z = 25.0 };

    /// <summary>앞장비 셔틀에서 PCB 2번 픽업 위치</summary>
    public AxisPos Zone1_PickPos2 { get; set; } = new() { X = 50.0, Y = 80.0, Z = 25.0 };

    /// <summary>우리 셔틀에 PCB 1번 배치 위치</summary>
    public AxisPos Zone1_PlacePos1 { get; set; } = new() { X = 150.0, Y = 30.0, Z = 25.0 };

    /// <summary>우리 셔틀에 PCB 2번 배치 위치</summary>
    public AxisPos Zone1_PlacePos2 { get; set; } = new() { X = 150.0, Y = 80.0, Z = 25.0 };

    // ── 구간 2: 볼트 체결 ────────────────────────────────────────────
    /// <summary>볼트용 Fiducial 카메라 이동 위치</summary>
    public AxisPos Zone2_FiducialPos { get; set; } = new() { X = 50.0, Y = 50.0, Z = 10.0 };

    /// <summary>볼트 체결 포인트 목록 (체결 순서대로)</summary>
    public List<BoltPoint> BoltPoints { get; set; } =
    [
        new() { Name = "B1", X =  80.0, Y = 60.0, Z = 20.0, TargetTorqueNm = 15.0 },
        new() { Name = "B2", X = 120.0, Y = 60.0, Z = 20.0, TargetTorqueNm = 15.0 },
        new() { Name = "B3", X = 120.0, Y = 90.0, Z = 20.0, TargetTorqueNm = 15.0 },
        new() { Name = "B4", X =  80.0, Y = 90.0, Z = 20.0, TargetTorqueNm = 15.0 },
    ];

    // ── 구간 3: 검사 ────────────────────────────────────────────────
    /// <summary>검사 카메라 이동 위치</summary>
    public AxisPos Zone3_InspectPos { get; set; } = new() { X = 50.0, Y = 50.0, Z = 10.0 };

    /// <summary>NG 적재 위치 (Y, Z 이동)</summary>
    public double Zone3_NgStackY { get; set; } = 200.0;
    public double Zone3_NgStackZ { get; set; } = 50.0;

    /// <summary>NG 적재 최대 수량 (초과 시 알람)</summary>
    public int NgStackMaxCount { get; set; } = 3;

    // ── SMEMA ────────────────────────────────────────────────────────
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
