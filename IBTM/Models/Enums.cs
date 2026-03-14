namespace IBTM.Models;

/// <summary>
/// 공정 스테이지 - SMT Inline 볼트 체결 설비
/// </summary>
public enum ProcessStage
{
    Idle = 0,

    // Step 1: 라인 도착 체크
    LineArrivalCheck = 1,       // 2개 라인 PCB 도착 IO 체크

    // Step 2: Fiducial 체크 (픽업용)
    FiducialForPick = 2,        // 카메라 X,Y,Z 이동 → Fiducial 검출

    // Step 3: PCB 픽업 → 방열판 Transfer
    PickAndTransfer = 3,        // 보정 좌표 적용 → 집어서 방열판 위에 (X,Y,Z)

    // Step 4: 컨베이어 → 볼트 체결 스테이션
    ConveyorToBoltStation = 4,  // 컨베이어 이동

    // Step 5: Fiducial 체크 (볼트용)
    FiducialForBolt = 5,        // 카메라 X,Y,Z 이동 → Fiducial 검출

    // Step 6: 볼트 체결 (레시피 N회 반복)
    BoltTighten = 6,            // 보정 볼트 위치 X,Y,Z → Shoot → Tighten

    // Step 7: 체결 후 비전 검사 (매 볼트마다)
    BoltVisionInspect = 7,      // 체결 위치 비전 검사

    // Step 8: 최종 비전 검사
    FinalVisionInspect = 8,     // 전체 검사 → NG/Good 판정

    // NG 경로
    ConveyorToNg = 9,
    NgTransfer = 10,            // Y, Z 이동 → NG 적재

    // Good 경로
    ConveyorToGood = 11,
    SmemaWait = 12,             // 뒤 설비 SMEMA 신호 대기
    Discharge = 13,             // 배출

    Complete = 14,
    Error = -1
}

public enum StageStatus
{
    Idle,
    Ready,
    Running,
    Done,
    Error,
    Warning
}

public enum InspectionResult
{
    Unknown,
    Good,
    Ng
}

public enum SourceLine
{
    Line1 = 1,
    Line2 = 2
}

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}
