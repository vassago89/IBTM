namespace IBTM.Application.Process;

/// <summary>
/// IO 인덱스 매핑 - 3구간 공용 컨베이어 + 구간별 스토퍼/센서/얼라인/리프트
/// </summary>
public static class IoMap
{
    // ── 공용 ─────────────────────────────────────────────────────────
    public const int Conveyor = 0;                // 공용 컨베이어 구동

    // ── 앞장비 연동 ──────────────────────────────────────────────────
    public const int PrevRearLaneSensor = 1;      // 앞장비 뒤쪽 레인 센서 (입력)

    // ── 구간 1 (픽업) ────────────────────────────────────────────────
    public const int Zone1_Sensor = 10;           // 셔틀 도착 센서 (입력)
    public const int Zone1_Stopper = 11;          // 스토퍼 (출력)
    public const int Zone1_Align = 12;            // 얼라인 (출력)
    public const int Zone1_Lift = 13;             // 리프트 (출력)
    public const int Zone1_Gripper = 14;          // 그리퍼 (출력)

    // ── 구간 2 (볼트 체결) ───────────────────────────────────────────
    public const int Zone2_Sensor = 20;           // 셔틀 도착 센서 (입력)
    public const int Zone2_Stopper = 21;          // 스토퍼 (출력)
    public const int Zone2_Align = 22;            // 얼라인 (출력)
    public const int Zone2_Lift = 23;             // 리프트 (출력)

    // ── 구간 3 (검사) ────────────────────────────────────────────────
    public const int Zone3_Sensor = 30;           // 셔틀 도착 센서 (입력)
    public const int Zone3_Stopper = 31;          // 스토퍼 (출력)
    public const int Zone3_Align = 32;            // 얼라인 (출력)
    public const int Zone3_Lift = 33;             // 리프트 (출력)
    public const int Zone3_Gripper = 34;          // NG 픽업 그리퍼 (출력)

    // ── SMEMA ────────────────────────────────────────────────────────
    public const int Smema_MachineReady = 40;     // 뒤 설비 준비 신호 (입력)
    public const int Smema_BoardAvailable = 41;   // 배출 준비 신호 (출력)

    // ── 레이저 포인터 (티칭용) ──────────────────────────────────────
    public const int Zone1_Laser = 15;            // 구간1 레이저 포인터 (출력)
    public const int Zone2_Laser = 25;            // 구간2 레이저 포인터 (출력)
    public const int Zone3_Laser = 35;            // 구간3 레이저 포인터 (출력)
}
