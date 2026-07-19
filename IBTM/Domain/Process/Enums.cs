namespace IBTM.Domain.Process;

/// <summary>
/// 공정 스테이지 - 3구간 파이프라인
/// </summary>
public enum ProcessStage
{
    Idle = 0,

    // ── 구간 1: 픽업 ───────────────────────────────────────────────────
    Zone1_WaitShuttle = 101,        // 셔틀 도착 + 앞장비 뒤쪽 레인 센서 대기
    Zone1_StopAlignLift = 102,      // 스토퍼 → 얼라인 → 리프트 업
    Zone1_PickPlace = 103,          // 앞장비 셔틀에서 PCB 2개 픽업 → 배치
    Zone1_Release = 104,            // 리프트 다운 → 스토퍼 해제

    // ── 구간 2: 볼트 체결 ──────────────────────────────────────────────
    Zone2_WaitShuttle = 201,        // 셔틀 도착 대기
    Zone2_StopAlignLift = 202,      // 스토퍼 → 얼라인 → 리프트 업
    Zone2_Fiducial = 203,           // Fiducial 검출 (볼트 보정용)
    Zone2_BoltTighten = 204,        // 볼트 체결 (레시피 N회 반복)
    Zone2_Release = 205,            // 리프트 다운 → 스토퍼 해제

    // ── 구간 3: 검사 ──────────────────────────────────────────────────
    Zone3_WaitShuttle = 301,        // 셔틀 도착 대기
    Zone3_StopAlignLift = 302,      // 스토퍼 → 얼라인 → 리프트 업
    Zone3_Inspect = 303,            // 카메라 검사 (볼트 유무)
    Zone3_NgTransfer = 304,         // NG → 뒤쪽 적재 (max 3)
    Zone3_SmemaWait = 305,          // Good → SMEMA 대기
    Zone3_Discharge = 306,          // Good → 배출
    Zone3_Release = 307,            // 리프트 다운 → 스토퍼 해제

    Complete = 900,
    Error = -1
}

public enum StageStatus
{
    Idle,
    Ready,
    Running,
    Done,
    Error,
    Warning,
    Skipped
}

public enum InspectionResult
{
    Unknown,
    Good,
    Ng
}

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}
