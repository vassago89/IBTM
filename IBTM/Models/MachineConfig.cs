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

    // ── 모션 설정 ─────────────────────────────────────────────────
    /// <summary>Zone별 XYZ 이동속도 (mm/s)</summary>
    public ZoneMotionParams Zone1Motion { get; set; } = new();
    public ZoneMotionParams Zone2Motion { get; set; } = new();
    public ZoneMotionParams Zone3Motion { get; set; } = new();

    // ── 볼트 체결 설정 ────────────────────────────────────────────
    /// <summary>기본 목표 토크 (Nm)</summary>
    public double DefaultTorqueNm { get; set; } = 15.0;

    /// <summary>기본 토크 허용오차 (Nm)</summary>
    public double DefaultTorqueToleranceNm { get; set; } = 0.5;

    /// <summary>볼트 드라이버 RPM</summary>
    public int DriverRpm { get; set; } = 300;

    /// <summary>토크 미달 시 재체결 횟수</summary>
    public int BoltRetryCount { get; set; } = 2;

    // ── 비전 설정 ─────────────────────────────────────────────────
    /// <summary>카메라 픽셀 당 mm 비율</summary>
    public double PixelsPerMm { get; set; } = 50.0;

    /// <summary>카메라 노출 시간 (μs)</summary>
    public double CameraExposureUs { get; set; } = 5000.0;

    /// <summary>카메라 게인 (dB)</summary>
    public double CameraGainDb { get; set; } = 0.0;

    /// <summary>피듀셜 템플릿 매칭 임계값 (0.0~1.0)</summary>
    public double FiducialMatchThreshold { get; set; } = 0.7;

    // ── IO / 컨베이어 설정 ────────────────────────────────────────
    /// <summary>SMEMA 타임아웃 (초)</summary>
    public int SmemaTimeoutSec { get; set; } = 30;

    /// <summary>리프트 동작 후 안정화 지연 (ms)</summary>
    public int LiftSettleDelayMs { get; set; } = 500;

    /// <summary>정렬 동작 후 안정화 지연 (ms)</summary>
    public int AlignSettleDelayMs { get; set; } = 300;

    /// <summary>NG 스택 최대 수량 (초과 시 알람)</summary>
    public int NgStackMaxCount { get; set; } = 3;

    // ── 시스템 설정 ───────────────────────────────────────────────
    /// <summary>언어 (en / ko)</summary>
    public string Language { get; set; } = "en";

    /// <summary>로그 레벨 (0=Debug, 1=Info, 2=Warn, 3=Error)</summary>
    public int LogLevel { get; set; } = 1;

    /// <summary>로그 보관 일수</summary>
    public int LogRetentionDays { get; set; } = 30;

    /// <summary>시뮬레이션 모드</summary>
    public bool SimulationMode { get; set; } = true;

    // ── 메서드 ────────────────────────────────────────────────────

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

    /// <summary>선택된 Zone의 모션 파라미터 반환</summary>
    public ZoneMotionParams GetMotionParams(int zone) => zone switch
    {
        1 => Zone1Motion,
        2 => Zone2Motion,
        3 => Zone3Motion,
        _ => Zone3Motion,
    };
}

/// <summary>Zone별 모션 파라미터</summary>
public class ZoneMotionParams
{
    /// <summary>XY 이동속도 (mm/s)</summary>
    public double SpeedXY { get; set; } = 100.0;

    /// <summary>Z 이동속도 (mm/s)</summary>
    public double SpeedZ { get; set; } = 50.0;

    /// <summary>가속도 (mm/s²)</summary>
    public double Acceleration { get; set; } = 500.0;

    /// <summary>감속도 (mm/s²)</summary>
    public double Deceleration { get; set; } = 500.0;

    /// <summary>X축 소프트 리밋 (+)</summary>
    public double SoftLimitXPlus { get; set; } = 300.0;

    /// <summary>X축 소프트 리밋 (-)</summary>
    public double SoftLimitXMinus { get; set; } = 0.0;

    /// <summary>Y축 소프트 리밋 (+)</summary>
    public double SoftLimitYPlus { get; set; } = 200.0;

    /// <summary>Y축 소프트 리밋 (-)</summary>
    public double SoftLimitYMinus { get; set; } = 0.0;

    /// <summary>Z축 소프트 리밋 (+)</summary>
    public double SoftLimitZPlus { get; set; } = 100.0;

    /// <summary>Z축 소프트 리밋 (-)</summary>
    public double SoftLimitZMinus { get; set; } = 0.0;

    /// <summary>홈 오프셋 X</summary>
    public double HomeOffsetX { get; set; }

    /// <summary>홈 오프셋 Y</summary>
    public double HomeOffsetY { get; set; }

    /// <summary>홈 오프셋 Z</summary>
    public double HomeOffsetZ { get; set; }
}
