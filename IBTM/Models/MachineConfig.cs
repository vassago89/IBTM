namespace IBTM.Models;

/// <summary>
/// 장비 고정 설정 (장비 셋업 시 1회 저장, 모델 변경과 무관)
/// MachineConfig.json으로 저장/로드
/// </summary>
public class MachineConfig
{
    // ── Zone 간 오프셋 (캘리브레이션 결과) ──────────────────────────
    /// <summary>Zone 1 레이저 기준점 좌표</summary>
    public AxisPos Zone1Ref { get; set; } = new();

    /// <summary>Zone 2 레이저 기준점 좌표</summary>
    public AxisPos Zone2Ref { get; set; } = new();

    /// <summary>Zone 3 레이저 기준점 좌표</summary>
    public AxisPos Zone3Ref { get; set; } = new();

    /// <summary>Zone 3→1 좌표 오프셋 (Zone3 기준 - Zone1 기준)</summary>
    public AxisPos Offset3To1 { get; set; } = new();

    /// <summary>Zone 3→2 좌표 오프셋 (Zone3 기준 - Zone2 기준)</summary>
    public AxisPos Offset3To2 { get; set; } = new();

    /// <summary>카메라 픽셀 당 mm 비율</summary>
    public double PixelsPerMm { get; set; } = 50.0;

    /// <summary>기준점 3개로 오프셋 자동 계산</summary>
    public void ComputeOffsets()
    {
        Offset3To1 = new AxisPos
        {
            X = Zone3Ref.X - Zone1Ref.X,
            Y = Zone3Ref.Y - Zone1Ref.Y,
            Z = Zone3Ref.Z - Zone1Ref.Z,
        };
        Offset3To2 = new AxisPos
        {
            X = Zone3Ref.X - Zone2Ref.X,
            Y = Zone3Ref.Y - Zone2Ref.Y,
            Z = Zone3Ref.Z - Zone2Ref.Z,
        };
    }

    /// <summary>Zone 3 좌표를 Zone 1 좌표로 변환</summary>
    public AxisPos ToZone1(AxisPos zone3Pos) => new()
    {
        X = zone3Pos.X - Offset3To1.X,
        Y = zone3Pos.Y - Offset3To1.Y,
        Z = zone3Pos.Z - Offset3To1.Z,
    };

    /// <summary>Zone 3 좌표를 Zone 2 좌표로 변환</summary>
    public AxisPos ToZone2(AxisPos zone3Pos) => new()
    {
        X = zone3Pos.X - Offset3To2.X,
        Y = zone3Pos.Y - Offset3To2.Y,
        Z = zone3Pos.Z - Offset3To2.Z,
    };
}
