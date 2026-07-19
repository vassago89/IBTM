using System.Text.Json.Serialization;

namespace IBTM.Domain.Recipes;

/// <summary>볼트 체결 포인트 1개 정의</summary>
public sealed class BoltPoint
{
    public string Name { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double TargetTorqueNm { get; set; }
    public double TorqueToleranceNm { get; set; } = 0.5;

    [JsonPropertyName("ToranceNm")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LegacyTorqueToleranceNm
    {
        get => null;
        set
        {
            if (value.HasValue)
            {
                TorqueToleranceNm = value.Value;
            }
        }
    }
}

/// <summary>축 위치 (X, Y, Z)</summary>
public sealed class AxisPos
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }

    public AxisPos Clone() => new() { X = X, Y = Y, Z = Z };
}

/// <summary>
/// 레시피: 3구간 설비 동작의 모든 위치/파라미터 정의
/// </summary>
public sealed class Recipe
{
    public string Name { get; set; } = "Default";

    // ── 구간 1: PCB 픽업/배치 위치 ──────────────────────────────────
    // 셔틀이 캐리어를 싣고 SMEMA로 진입 → 상부 PCB 라인에서 PCB 픽업 → 셔틀 캐리어 위에 배치
    // 셔틀에 2세트: 좌측(X≈75) + 우측(X≈130)

    /// <summary>상부 PCB 라인에서 1번 PCB 픽업</summary>
    public AxisPos Zone1_PcbPick1 { get; set; } = new() { X = 75.0, Y = 25.0, Z = 25.0 };
    /// <summary>셔틀 좌측 캐리어 위에 1번 PCB 배치</summary>
    public AxisPos Zone1_PcbPlace1 { get; set; } = new() { X = 75.0, Y = 95.0, Z = 20.0 };

    /// <summary>상부 PCB 라인에서 2번 PCB 픽업</summary>
    public AxisPos Zone1_PcbPick2 { get; set; } = new() { X = 130.0, Y = 25.0, Z = 25.0 };
    /// <summary>셔틀 우측 캐리어 위에 2번 PCB 배치</summary>
    public AxisPos Zone1_PcbPlace2 { get; set; } = new() { X = 130.0, Y = 95.0, Z = 20.0 };

    // ── 구간 2: 볼트 체결 ────────────────────────────────────────────
    /// <summary>볼트용 Fiducial 카메라 이동 위치</summary>
    public AxisPos Zone2_FiducialPos { get; set; } = new() { X = 70.0, Y = 85.0, Z = 10.0 };

    /// <summary>PCB 중심 좌표 (셔틀 위 2개 PCB의 X위치)</summary>
    public double Zone2_Pcb1CenterX { get; set; } = 75.0;
    public double Zone2_Pcb2CenterX { get; set; } = 130.0;
    public double Zone2_PcbCenterY { get; set; } = 95.0;

    /// <summary>볼트 체결 포인트 (PCB 중심 기준 상대좌표, 체결 순서대로)</summary>
    public List<BoltPoint> BoltPoints { get; set; } =
    [
        new() { Name = "B1", X = -10.0, Y = -10.0, Z = 20.0, TargetTorqueNm = 15.0 },
        new() { Name = "B2", X =  10.0, Y = -10.0, Z = 20.0, TargetTorqueNm = 15.0 },
        new() { Name = "B3", X =  10.0, Y =  10.0, Z = 20.0, TargetTorqueNm = 15.0 },
        new() { Name = "B4", X = -10.0, Y =  10.0, Z = 20.0, TargetTorqueNm = 15.0 },
    ];

    // ── 구간 3: 검사 ────────────────────────────────────────────────
    /// <summary>검사 카메라 이동 위치</summary>
    public AxisPos Zone3_InspectPos { get; set; } = new() { X = 100.0, Y = 95.0, Z = 10.0 };

    /// <summary>NG PCB 픽업 위치 (셔틀 위 PCB를 집는 위치)</summary>
    public AxisPos Zone3_NgPickupPos { get; set; } = new() { X = 100.0, Y = 95.0, Z = 20.0 };

    /// <summary>NG 버퍼 적재 위치 (위쪽 NG 스택에 놓는 위치)</summary>
    public AxisPos Zone3_NgPlacePos { get; set; } = new() { X = 100.0, Y = 25.0, Z = 30.0 };

}
