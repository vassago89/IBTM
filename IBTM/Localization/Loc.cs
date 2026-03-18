using CommunityToolkit.Mvvm.ComponentModel;

namespace IBTM.Localization;

/// <summary>
/// 다국어 문자열 싱글턴.
/// XAML 바인딩: {Binding [Key], Source={x:Static loc:Loc.Instance}}
/// C# 코드:   Loc.S("Key")  또는  Loc.Instance["Key"]
/// </summary>
public partial class Loc : ObservableObject
{
    public static Loc Instance { get; } = new();

    private Dictionary<string, string> _current;
    private string _currentLanguage = "en";

    private Loc() => _current = En;

    // ── 공개 API ──────────────────────────────────────────────────────────

    public string this[string key] =>
        _current.TryGetValue(key, out var val) ? val : key;

    public static string S(string key) => Instance[key];

    public static string S(string key, params object[] args) =>
        string.Format(Instance[key], args);

    [ObservableProperty] private string _language = "EN";

    public void SetLanguage(string lang)
    {
        _currentLanguage = lang.ToLowerInvariant();
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
        ["Btn_Start"]       = "Start",
        ["Btn_Stop"]        = "Stop",
        ["Btn_EStop"]       = "E-STOP",
        ["Btn_NgReset"]     = "NG Reset",
        ["Nav_Process"]     = "Process",

        // ── Stage titles ─────────────────────────────────────────────────
        ["Stage_WaitShuttle"]   = "Wait Shuttle",
        ["Stage_StopAlignLift"] = "Align·Lift",
        ["Stage_PickPlace"]     = "PCB Pick",
        ["Stage_Release"]       = "Release",
        ["Stage_Fiducial"]      = "Fiducial",
        ["Stage_BoltTighten"]   = "Bolt Tighten",
        ["Stage_Inspect"]       = "Inspect",
        ["Stage_NgTransfer"]    = "NG Transfer",
        ["Stage_SmemaWait"]     = "SMEMA",
        ["Stage_Discharge"]     = "Discharge",

        // ── Stage subtitles ──────────────────────────────────────────────
        ["Sub_SensorDetect"]    = "Sensor detect",
        ["Sub_StpAlnLift"]      = "STP·ALN·LIFT",
        ["Sub_Sequential2Pcb"]  = "2 PCB sequential",
        ["Sub_LiftDown"]        = "Lift down",
        ["Sub_CorrectionDetect"]= "Correction detect",
        ["Sub_NRepeat"]         = "N repeats",
        ["Sub_BoltPresence"]    = "Bolt presence",
        ["Sub_RearTransfer"]    = "Rear transfer",
        ["Sub_NextEquipWait"]   = "Next equip wait",
        ["Sub_ConveyorOut"]     = "Conveyor OUT",

        // ── Activity labels (canvas overlay) ─────────────────────────────
        ["Act_WaitShuttle"]     = "WAIT SHUTTLE",
        ["Act_AlignLift"]       = "ALIGN·LIFT",
        ["Act_PcbPick"]         = "PCB PICK",
        ["Act_Release"]         = "RELEASE",
        ["Act_Fiducial"]        = "FIDUCIAL",
        ["Act_BoltTighten"]     = "BOLT TIGHTEN",
        ["Act_BoltProgress"]    = "BOLT {0}/{1}",
        ["Act_Inspecting"]      = "INSPECTING",
        ["Act_NgTransfer"]      = "NG TRANSFER",
        ["Act_SmemaWait"]       = "SMEMA WAIT",
        ["Act_Discharging"]     = "DISCHARGE",
        ["Act_Error"]           = "ERROR",

        // ── Status messages (top bar) ────────────────────────────────────
        ["Stat_Waiting"]        = "Waiting",
        ["Stat_CycleComplete"]  = "Cycle complete",
        ["Stat_Error"]          = "Error! [{0}]",
        ["Stat_Z1_WaitShuttle"] = "[Zone1] Wait shuttle...",
        ["Stat_Z1_AlignLift"]   = "[Zone1] Align/Lift...",
        ["Stat_Z1_PickPlace"]   = "[Zone1] PCB picking...",
        ["Stat_Z1_Release"]     = "[Zone1] Release...",
        ["Stat_Z2_WaitShuttle"] = "[Zone2] Wait shuttle...",
        ["Stat_Z2_AlignLift"]   = "[Zone2] Align/Lift...",
        ["Stat_Z2_Fiducial"]    = "[Zone2] Fiducial...",
        ["Stat_Z2_BoltTighten"] = "[Zone2] Bolt tightening...",
        ["Stat_Z2_Release"]     = "[Zone2] Release...",
        ["Stat_Z3_WaitShuttle"] = "[Zone3] Wait shuttle...",
        ["Stat_Z3_AlignLift"]   = "[Zone3] Align/Lift...",
        ["Stat_Z3_Inspect"]     = "[Zone3] Inspecting...",
        ["Stat_Z3_NgTransfer"]  = "[Zone3] NG transfer...",
        ["Stat_Z3_SmemaWait"]   = "[Zone3] SMEMA...",
        ["Stat_Z3_Discharge"]   = "[Zone3] Discharge...",
        ["Stat_Z3_Release"]     = "[Zone3] Release...",

        // ── Stage card status text ───────────────────────────────────────
        ["Card_Running"]  = "Running",
        ["Card_Done"]     = "Done",
        ["Card_Error"]    = "Error",
        ["Card_Warning"]  = "Warning",
        ["Card_Skipped"]  = "Skipped",
        ["Card_Idle"]     = "Idle",

        // ── NG alarm overlay ─────────────────────────────────────────────
        ["NgAlarm_Loaded"]      = "Loaded ",
        ["NgAlarm_ResetRestart"]= "  —  Reset and restart",

        // ── Fiducial / bolt ──────────────────────────────────────────────
        ["Fiducial_Failed"]     = "Detection failed",
        ["Bolt_Format"]         = "Bolt {0}/{1}  [{2}]",
        ["Torque_Format"]       = "Torque: {0:F2} Nm  {1}",
    };

    // ═══════════════════════════════════════════════════════════════════════
    // Korean (한국어)
    // ═══════════════════════════════════════════════════════════════════════
    private static readonly Dictionary<string, string> Ko = new()
    {
        // ── UI buttons / labels ──────────────────────────────────────────
        ["Btn_Start"]       = "시작",
        ["Btn_Stop"]        = "정지",
        ["Btn_EStop"]       = "E-STOP",
        ["Btn_NgReset"]     = "NG 리셋",
        ["Nav_Process"]     = "공정",

        // ── Stage titles ─────────────────────────────────────────────────
        ["Stage_WaitShuttle"]   = "셔틀 대기",
        ["Stage_StopAlignLift"] = "정렬·리프트",
        ["Stage_PickPlace"]     = "PCB 픽업",
        ["Stage_Release"]       = "릴리즈",
        ["Stage_Fiducial"]      = "Fiducial",
        ["Stage_BoltTighten"]   = "볼트 체결",
        ["Stage_Inspect"]       = "검사",
        ["Stage_NgTransfer"]    = "NG 적재",
        ["Stage_SmemaWait"]     = "SMEMA",
        ["Stage_Discharge"]     = "배출",

        // ── Stage subtitles ──────────────────────────────────────────────
        ["Sub_SensorDetect"]    = "센서 감지",
        ["Sub_StpAlnLift"]      = "STP·ALN·LIFT",
        ["Sub_Sequential2Pcb"]  = "2개 순차 배치",
        ["Sub_LiftDown"]        = "리프트 다운",
        ["Sub_CorrectionDetect"]= "보정 검출",
        ["Sub_NRepeat"]         = "N회 반복",
        ["Sub_BoltPresence"]    = "볼트 유무 확인",
        ["Sub_RearTransfer"]    = "뒤쪽 이송",
        ["Sub_NextEquipWait"]   = "뒤 설비 대기",
        ["Sub_ConveyorOut"]     = "컨베이어 OUT",

        // ── Activity labels (canvas overlay) ─────────────────────────────
        ["Act_WaitShuttle"]     = "셔틀 대기",
        ["Act_AlignLift"]       = "정렬·리프트",
        ["Act_PcbPick"]         = "PCB 픽업",
        ["Act_Release"]         = "릴리즈",
        ["Act_Fiducial"]        = "FIDUCIAL",
        ["Act_BoltTighten"]     = "볼트 체결",
        ["Act_BoltProgress"]    = "볼트 {0}/{1}",
        ["Act_Inspecting"]      = "검사 중",
        ["Act_NgTransfer"]      = "NG 적재",
        ["Act_SmemaWait"]       = "SMEMA 대기",
        ["Act_Discharging"]     = "배출 중",
        ["Act_Error"]           = "오류",

        // ── Status messages (top bar) ────────────────────────────────────
        ["Stat_Waiting"]        = "대기 중",
        ["Stat_CycleComplete"]  = "사이클 완료",
        ["Stat_Error"]          = "오류 발생! [{0}]",
        ["Stat_Z1_WaitShuttle"] = "[구간1] 셔틀 대기...",
        ["Stat_Z1_AlignLift"]   = "[구간1] 정렬/리프트...",
        ["Stat_Z1_PickPlace"]   = "[구간1] PCB 픽업 중...",
        ["Stat_Z1_Release"]     = "[구간1] 릴리즈...",
        ["Stat_Z2_WaitShuttle"] = "[구간2] 셔틀 대기...",
        ["Stat_Z2_AlignLift"]   = "[구간2] 정렬/리프트...",
        ["Stat_Z2_Fiducial"]    = "[구간2] Fiducial...",
        ["Stat_Z2_BoltTighten"] = "[구간2] 볼트 체결 중...",
        ["Stat_Z2_Release"]     = "[구간2] 릴리즈...",
        ["Stat_Z3_WaitShuttle"] = "[구간3] 셔틀 대기...",
        ["Stat_Z3_AlignLift"]   = "[구간3] 정렬/리프트...",
        ["Stat_Z3_Inspect"]     = "[구간3] 검사 중...",
        ["Stat_Z3_NgTransfer"]  = "[구간3] NG 적재...",
        ["Stat_Z3_SmemaWait"]   = "[구간3] SMEMA...",
        ["Stat_Z3_Discharge"]   = "[구간3] 배출...",
        ["Stat_Z3_Release"]     = "[구간3] 릴리즈...",

        // ── Stage card status text ───────────────────────────────────────
        ["Card_Running"]  = "실행 중",
        ["Card_Done"]     = "완료",
        ["Card_Error"]    = "오류",
        ["Card_Warning"]  = "경고",
        ["Card_Skipped"]  = "스킵",
        ["Card_Idle"]     = "대기",

        // ── NG alarm overlay ─────────────────────────────────────────────
        ["NgAlarm_Loaded"]      = "적재 ",
        ["NgAlarm_ResetRestart"]= "  —  리셋 후 재가동",

        // ── Fiducial / bolt ──────────────────────────────────────────────
        ["Fiducial_Failed"]     = "검출 실패",
        ["Bolt_Format"]         = "볼트 {0}/{1}  [{2}]",
        ["Torque_Format"]       = "토크: {0:F2} Nm  {1}",
    };
}
