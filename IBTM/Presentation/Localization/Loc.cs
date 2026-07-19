using CommunityToolkit.Mvvm.ComponentModel;
using System.Globalization;

namespace IBTM.Presentation.Localization;

/// <summary>
/// 다국어 문자열 싱글턴.
/// XAML 바인딩: {Binding [Key], Source={x:Static loc:Loc.Instance}}
/// C# 코드:   Loc.S("Key")  또는  Loc.Instance["Key"]
/// </summary>
public partial class Loc : ObservableObject
{
    private Dictionary<string, string> _current;
    private string _currentLanguage = "en";

    private Loc() => _current = En;

    // ── 공개 API ──────────────────────────────────────────────────────────

    public string this[string key] =>
        _current.TryGetValue(key, out var val) ? val : key;

    public static string S(string key) => Instance[key];

    public static string S(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Instance[key], args);

    [ObservableProperty] private string _language = "EN";

    public void SetLanguage(string lang)
    {
        _currentLanguage = string.Equals(lang, "ko", StringComparison.OrdinalIgnoreCase)
            ? "ko"
            : "en";
        _current = _currentLanguage == "ko" ? Ko : En;
        Language = _currentLanguage == "ko" ? "KO" : "EN";
        OnPropertyChanged("Item[]"); // 전체 인덱서 바인딩 갱신
    }

    public void ToggleLanguage() =>
        SetLanguage(_currentLanguage == "en" ? "ko" : "en");

    // ═══════════════════════════════════════════════════════════════════════
    // English (default)
    // ═══════════════════════════════════════════════════════════════════════
    private static readonly Dictionary<string, string> En = new()
    {
        // ── UI buttons / labels ──────────────────────────────────────────
        ["Btn_Start"] = "Start",
        ["Btn_Stop"] = "Stop",
        ["Btn_EStop"] = "E-STOP",
        ["Btn_NgReset"] = "NG Reset",
        ["Nav_Process"] = "Process",

        // ── Stage titles ─────────────────────────────────────────────────
        ["Stage_WaitShuttle"] = "Wait Shuttle",
        ["Stage_StopAlignLift"] = "Align·Lift",
        ["Stage_PickPlace"] = "PCB Pick",
        ["Stage_Release"] = "Release",
        ["Stage_Fiducial"] = "Fiducial",
        ["Stage_BoltTighten"] = "Bolt Tighten",
        ["Stage_Inspect"] = "Inspect",
        ["Stage_NgTransfer"] = "NG Transfer",
        ["Stage_SmemaWait"] = "SMEMA",
        ["Stage_Discharge"] = "Discharge",

        // ── Stage subtitles ──────────────────────────────────────────────
        ["Sub_SensorDetect"] = "Sensor detect",
        ["Sub_StpAlnLift"] = "STP·ALN·LIFT",
        ["Sub_Sequential2Pcb"] = "2 PCB sequential",
        ["Sub_LiftDown"] = "Lift down",
        ["Sub_CorrectionDetect"] = "Correction detect",
        ["Sub_NRepeat"] = "N repeats",
        ["Sub_BoltPresence"] = "Bolt presence",
        ["Sub_RearTransfer"] = "Rear transfer",
        ["Sub_NextEquipWait"] = "Next equip wait",
        ["Sub_ConveyorOut"] = "Conveyor OUT",

        // ── Activity labels (canvas overlay) ─────────────────────────────
        ["Act_WaitShuttle"] = "WAIT SHUTTLE",
        ["Act_AlignLift"] = "ALIGN·LIFT",
        ["Act_PcbPick"] = "PCB PICK",
        ["Act_Release"] = "RELEASE",
        ["Act_Fiducial"] = "FIDUCIAL",
        ["Act_BoltTighten"] = "BOLT TIGHTEN",
        ["Act_BoltProgress"] = "BOLT {0}/{1}",
        ["Act_Inspecting"] = "INSPECTING",
        ["Act_NgTransfer"] = "NG TRANSFER",
        ["Act_SmemaWait"] = "SMEMA WAIT",
        ["Act_Discharging"] = "DISCHARGE",
        ["Act_Error"] = "ERROR",

        // ── Status messages (top bar) ────────────────────────────────────
        ["Stat_Waiting"] = "Waiting",
        ["Stat_CycleComplete"] = "Cycle complete",
        ["Stat_Error"] = "Error! [{0}]",
        ["Stat_Z1_WaitShuttle"] = "[Zone1] Wait shuttle...",
        ["Stat_Z1_AlignLift"] = "[Zone1] Align/Lift...",
        ["Stat_Z1_PickPlace"] = "[Zone1] PCB picking...",
        ["Stat_Z1_Release"] = "[Zone1] Release...",
        ["Stat_Z2_WaitShuttle"] = "[Zone2] Wait shuttle...",
        ["Stat_Z2_AlignLift"] = "[Zone2] Align/Lift...",
        ["Stat_Z2_Fiducial"] = "[Zone2] Fiducial...",
        ["Stat_Z2_BoltTighten"] = "[Zone2] Bolt tightening...",
        ["Stat_Z2_Release"] = "[Zone2] Release...",
        ["Stat_Z3_WaitShuttle"] = "[Zone3] Wait shuttle...",
        ["Stat_Z3_AlignLift"] = "[Zone3] Align/Lift...",
        ["Stat_Z3_Inspect"] = "[Zone3] Inspecting...",
        ["Stat_Z3_NgTransfer"] = "[Zone3] NG transfer...",
        ["Stat_Z3_SmemaWait"] = "[Zone3] SMEMA...",
        ["Stat_Z3_Discharge"] = "[Zone3] Discharge...",
        ["Stat_Z3_Release"] = "[Zone3] Release...",

        // ── Stage card status text ───────────────────────────────────────
        ["Card_Running"] = "Running",
        ["Card_Done"] = "Done",
        ["Card_Error"] = "Error",
        ["Card_Warning"] = "Warning",
        ["Card_Skipped"] = "Skipped",
        ["Card_Idle"] = "Idle",

        // ── NG alarm overlay ─────────────────────────────────────────────
        ["NgAlarm_Loaded"] = "Loaded ",
        ["NgAlarm_ResetRestart"] = "  —  Reset and restart",

        // ── Fiducial / bolt ──────────────────────────────────────────────
        ["Fiducial_Failed"] = "Detection failed",
        ["Bolt_Format"] = "Bolt {0}/{1}  [{2}]",
        ["Torque_Format"] = "Torque: {0:F2} Nm  {1}",

        // ── Teaching ────────────────────────────────────────────────
        ["Teach_Title"] = "Teaching",
        ["Teach_Zone"] = "Zone",
        ["Teach_CurrentPos"] = "Current Position",
        ["Teach_Laser"] = "Laser",
        ["Teach_Points"] = "Teaching Points",
        ["Teach_Record"] = "Teach",
        ["Teach_MoveTo"] = "Move To",
        ["Teach_AddBolt"] = "Add Bolt",
        ["Teach_RemoveBolt"] = "Remove",
        ["Teach_Recipe"] = "Recipe:",
        ["Teach_New"] = "New",
        ["Teach_Save"] = "Save",
        ["Teach_Load"] = "Load",
        ["Teach_Recorded"] = "Taught: {0}",
        ["Teach_MovedTo"] = "Moved to: {0}",
        ["Teach_RecipeSaved"] = "Recipe saved: {0}",
        ["Teach_RecipeLoaded"] = "Recipe loaded: {0}",
        ["Teach_NewRecipe"] = "New recipe created",
        ["Teach_BoltAdded"] = "Bolt added: {0}",
        ["Teach_BoltRemoved"] = "Bolt removed: {0}",

        // ── Settings ────────────────────────────────────────────────
        ["Settings_Title"] = "Settings",
        ["Settings_Subtitle"] = "Machine setup & configuration",
        ["Settings_Save"] = "Save Config",
        ["Settings_Loaded"] = "Config loaded",
        ["Settings_Saved"] = "Config saved",

        // Settings 탭 이름
        ["Settings_Tab_Calibration"] = "Calibration",
        ["Settings_Tab_Motion"] = "Motion",
        ["Settings_Tab_Bolt"] = "Bolt",
        ["Settings_Tab_Vision"] = "Vision",
        ["Settings_Tab_IO"] = "IO / Conveyor",
        ["Settings_Tab_System"] = "System",

        // 캘리브레이션 탭
        ["Settings_ZoneOffset"] = "Zone Offset Calibration",
        ["Settings_ZoneSelect"] = "Zone",
        ["Settings_CurrentPos"] = "Current Position",
        ["Settings_Laser"] = "Laser",
        ["Settings_RecordRef"] = "Record Ref",
        ["Settings_ComputeOffset"] = "Compute Offsets",
        ["Settings_RefRecorded"] = "Zone {0} ref recorded",
        ["Settings_NeedAllRefs"] = "Record all 3 zone references first",
        ["Settings_OffsetsComputed"] = "Offsets computed",

        // 모션 탭
        ["Settings_MotionZone"] = "Zone Motion Parameters",
        ["Settings_SpeedAccel"] = "Speed / Acceleration",
        ["Settings_SpeedXY"] = "XY Speed (mm/s)",
        ["Settings_SpeedZ"] = "Z Speed (mm/s)",
        ["Settings_Accel"] = "Accel (mm/s²)",
        ["Settings_Decel"] = "Decel (mm/s²)",
        ["Settings_SoftLimit"] = "Soft Limits",
        ["Settings_HomeOffset"] = "Home Offset",

        // 볼트 탭
        ["Settings_BoltParams"] = "Bolt Tightening Parameters",
        ["Settings_DefaultTorque"] = "Default Torque (Nm)",
        ["Settings_TorqueTolerance"] = "Tolerance (Nm)",
        ["Settings_DriverRpm"] = "Driver RPM",
        ["Settings_RetryCount"] = "Retry Count",

        // 비전 탭
        ["Settings_CameraParams"] = "Camera Parameters",
        ["Settings_Exposure"] = "Exposure (μs)",
        ["Settings_Gain"] = "Gain (dB)",
        ["Settings_PixelsPerMm"] = "Pixels/mm",
        ["Settings_MatchThreshold"] = "Match Threshold",

        // IO 탭
        ["Settings_IOParams"] = "IO / Conveyor Parameters",
        ["Settings_SmemaTimeout"] = "SMEMA Timeout (s)",
        ["Settings_NgStackMax"] = "NG Stack Max",
        ["Settings_LiftDelay"] = "Lift Delay (ms)",
        ["Settings_AlignDelay"] = "Align Delay (ms)",

        // 시스템 탭
        ["Settings_Language"] = "Language",
        ["Settings_ToggleLang"] = "Toggle EN ↔ KO",
        ["Settings_LangChanged"] = "Language changed",
        ["Settings_LogSettings"] = "Log Settings",
        ["Settings_LogLevel"] = "Log Level",
        ["Settings_LogRetention"] = "Retention (days)",
        ["Settings_SimMode"] = "Simulation Mode",
        ["Settings_SimModeDesc"] = "Use virtual devices instead of real hardware",
    };

    // ═══════════════════════════════════════════════════════════════════════
    // Korean (한국어)
    // ═══════════════════════════════════════════════════════════════════════
    private static readonly Dictionary<string, string> Ko = new()
    {
        // ── UI buttons / labels ──────────────────────────────────────────
        ["Btn_Start"] = "시작",
        ["Btn_Stop"] = "정지",
        ["Btn_EStop"] = "E-STOP",
        ["Btn_NgReset"] = "NG 리셋",
        ["Nav_Process"] = "공정",

        // ── Stage titles ─────────────────────────────────────────────────
        ["Stage_WaitShuttle"] = "셔틀 대기",
        ["Stage_StopAlignLift"] = "정렬·리프트",
        ["Stage_PickPlace"] = "PCB 픽업",
        ["Stage_Release"] = "릴리즈",
        ["Stage_Fiducial"] = "Fiducial",
        ["Stage_BoltTighten"] = "볼트 체결",
        ["Stage_Inspect"] = "검사",
        ["Stage_NgTransfer"] = "NG 적재",
        ["Stage_SmemaWait"] = "SMEMA",
        ["Stage_Discharge"] = "배출",

        // ── Stage subtitles ──────────────────────────────────────────────
        ["Sub_SensorDetect"] = "센서 감지",
        ["Sub_StpAlnLift"] = "STP·ALN·LIFT",
        ["Sub_Sequential2Pcb"] = "2개 순차 배치",
        ["Sub_LiftDown"] = "리프트 다운",
        ["Sub_CorrectionDetect"] = "보정 검출",
        ["Sub_NRepeat"] = "N회 반복",
        ["Sub_BoltPresence"] = "볼트 유무 확인",
        ["Sub_RearTransfer"] = "뒤쪽 이송",
        ["Sub_NextEquipWait"] = "뒤 설비 대기",
        ["Sub_ConveyorOut"] = "컨베이어 OUT",

        // ── Activity labels (canvas overlay) ─────────────────────────────
        ["Act_WaitShuttle"] = "셔틀 대기",
        ["Act_AlignLift"] = "정렬·리프트",
        ["Act_PcbPick"] = "PCB 픽업",
        ["Act_Release"] = "릴리즈",
        ["Act_Fiducial"] = "FIDUCIAL",
        ["Act_BoltTighten"] = "볼트 체결",
        ["Act_BoltProgress"] = "볼트 {0}/{1}",
        ["Act_Inspecting"] = "검사 중",
        ["Act_NgTransfer"] = "NG 적재",
        ["Act_SmemaWait"] = "SMEMA 대기",
        ["Act_Discharging"] = "배출 중",
        ["Act_Error"] = "오류",

        // ── Status messages (top bar) ────────────────────────────────────
        ["Stat_Waiting"] = "대기 중",
        ["Stat_CycleComplete"] = "사이클 완료",
        ["Stat_Error"] = "오류 발생! [{0}]",
        ["Stat_Z1_WaitShuttle"] = "[구간1] 셔틀 대기...",
        ["Stat_Z1_AlignLift"] = "[구간1] 정렬/리프트...",
        ["Stat_Z1_PickPlace"] = "[구간1] PCB 픽업 중...",
        ["Stat_Z1_Release"] = "[구간1] 릴리즈...",
        ["Stat_Z2_WaitShuttle"] = "[구간2] 셔틀 대기...",
        ["Stat_Z2_AlignLift"] = "[구간2] 정렬/리프트...",
        ["Stat_Z2_Fiducial"] = "[구간2] Fiducial...",
        ["Stat_Z2_BoltTighten"] = "[구간2] 볼트 체결 중...",
        ["Stat_Z2_Release"] = "[구간2] 릴리즈...",
        ["Stat_Z3_WaitShuttle"] = "[구간3] 셔틀 대기...",
        ["Stat_Z3_AlignLift"] = "[구간3] 정렬/리프트...",
        ["Stat_Z3_Inspect"] = "[구간3] 검사 중...",
        ["Stat_Z3_NgTransfer"] = "[구간3] NG 적재...",
        ["Stat_Z3_SmemaWait"] = "[구간3] SMEMA...",
        ["Stat_Z3_Discharge"] = "[구간3] 배출...",
        ["Stat_Z3_Release"] = "[구간3] 릴리즈...",

        // ── Stage card status text ───────────────────────────────────────
        ["Card_Running"] = "실행 중",
        ["Card_Done"] = "완료",
        ["Card_Error"] = "오류",
        ["Card_Warning"] = "경고",
        ["Card_Skipped"] = "스킵",
        ["Card_Idle"] = "대기",

        // ── NG alarm overlay ─────────────────────────────────────────────
        ["NgAlarm_Loaded"] = "적재 ",
        ["NgAlarm_ResetRestart"] = "  —  리셋 후 재가동",

        // ── Fiducial / bolt ──────────────────────────────────────────────
        ["Fiducial_Failed"] = "검출 실패",
        ["Bolt_Format"] = "볼트 {0}/{1}  [{2}]",
        ["Torque_Format"] = "토크: {0:F2} Nm  {1}",

        // ── Teaching ────────────────────────────────────────────────
        ["Teach_Title"] = "티칭",
        ["Teach_Zone"] = "구간",
        ["Teach_CurrentPos"] = "현재 좌표",
        ["Teach_Laser"] = "레이저",
        ["Teach_Points"] = "티칭 포인트",
        ["Teach_Record"] = "티칭",
        ["Teach_MoveTo"] = "이동",
        ["Teach_AddBolt"] = "볼트 추가",
        ["Teach_RemoveBolt"] = "삭제",
        ["Teach_Recipe"] = "레시피:",
        ["Teach_New"] = "신규",
        ["Teach_Save"] = "저장",
        ["Teach_Load"] = "불러오기",
        ["Teach_Recorded"] = "티칭 완료: {0}",
        ["Teach_MovedTo"] = "이동 완료: {0}",
        ["Teach_RecipeSaved"] = "레시피 저장: {0}",
        ["Teach_RecipeLoaded"] = "레시피 로드: {0}",
        ["Teach_NewRecipe"] = "신규 레시피 생성",
        ["Teach_BoltAdded"] = "볼트 추가: {0}",
        ["Teach_BoltRemoved"] = "볼트 삭제: {0}",

        // ── Settings ────────────────────────────────────────────────
        ["Settings_Title"] = "설정",
        ["Settings_Subtitle"] = "장비 셋업 및 환경설정",
        ["Settings_Save"] = "설정 저장",
        ["Settings_Loaded"] = "설정 로드 완료",
        ["Settings_Saved"] = "설정 저장 완료",

        // Settings 탭 이름
        ["Settings_Tab_Calibration"] = "캘리브레이션",
        ["Settings_Tab_Motion"] = "모션",
        ["Settings_Tab_Bolt"] = "볼트",
        ["Settings_Tab_Vision"] = "비전",
        ["Settings_Tab_IO"] = "IO / 컨베이어",
        ["Settings_Tab_System"] = "시스템",

        // 캘리브레이션 탭
        ["Settings_ZoneOffset"] = "구간 오프셋 캘리브레이션",
        ["Settings_ZoneSelect"] = "구간",
        ["Settings_CurrentPos"] = "현재 좌표",
        ["Settings_Laser"] = "레이저",
        ["Settings_RecordRef"] = "기준점 기록",
        ["Settings_ComputeOffset"] = "오프셋 계산",
        ["Settings_RefRecorded"] = "구간 {0} 기준점 기록 완료",
        ["Settings_NeedAllRefs"] = "3개 구간 기준점 모두 기록 필요",
        ["Settings_OffsetsComputed"] = "오프셋 계산 완료",

        // 모션 탭
        ["Settings_MotionZone"] = "구간 모션 파라미터",
        ["Settings_SpeedAccel"] = "속도 / 가감속",
        ["Settings_SpeedXY"] = "XY 속도 (mm/s)",
        ["Settings_SpeedZ"] = "Z 속도 (mm/s)",
        ["Settings_Accel"] = "가속도 (mm/s²)",
        ["Settings_Decel"] = "감속도 (mm/s²)",
        ["Settings_SoftLimit"] = "소프트 리밋",
        ["Settings_HomeOffset"] = "홈 오프셋",

        // 볼트 탭
        ["Settings_BoltParams"] = "볼트 체결 파라미터",
        ["Settings_DefaultTorque"] = "기본 토크 (Nm)",
        ["Settings_TorqueTolerance"] = "허용오차 (Nm)",
        ["Settings_DriverRpm"] = "드라이버 RPM",
        ["Settings_RetryCount"] = "재시도 횟수",

        // 비전 탭
        ["Settings_CameraParams"] = "카메라 파라미터",
        ["Settings_Exposure"] = "노출 (μs)",
        ["Settings_Gain"] = "게인 (dB)",
        ["Settings_PixelsPerMm"] = "픽셀/mm",
        ["Settings_MatchThreshold"] = "매칭 임계값",

        // IO 탭
        ["Settings_IOParams"] = "IO / 컨베이어 파라미터",
        ["Settings_SmemaTimeout"] = "SMEMA 타임아웃 (초)",
        ["Settings_NgStackMax"] = "NG 스택 최대",
        ["Settings_LiftDelay"] = "리프트 지연 (ms)",
        ["Settings_AlignDelay"] = "정렬 지연 (ms)",

        // 시스템 탭
        ["Settings_Language"] = "언어",
        ["Settings_ToggleLang"] = "EN ↔ KO 전환",
        ["Settings_LangChanged"] = "언어 변경 완료",
        ["Settings_LogSettings"] = "로그 설정",
        ["Settings_LogLevel"] = "로그 레벨",
        ["Settings_LogRetention"] = "보관 기간 (일)",
        ["Settings_SimMode"] = "시뮬레이션 모드",
        ["Settings_SimModeDesc"] = "실제 장비 대신 가상 디바이스 사용",
    };

    // Instance는 En/Ko 딕셔너리 초기화 이후에 생성해야 함 (정적 필드 초기화 순서)
    public static Loc Instance { get; } = new();
}
