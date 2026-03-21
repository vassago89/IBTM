using CommunityToolkit.Mvvm.ComponentModel;

namespace IBTM.Models;

/// <summary>티칭 모드</summary>
public enum TeachMode
{
    /// <summary>XYZ 전체 티칭</summary>
    Full,
    /// <summary>XY만 티칭 (Zone 3 마스터 → 다른 Zone 오프셋 적용)</summary>
    XYOnly,
    /// <summary>Z만 티칭 (X,Y는 Zone 3 오프셋에서 자동 계산됨)</summary>
    ZOnly,
}

/// <summary>
/// 티칭 포인트 — 레시피의 각 위치를 UI에서 편집할 수 있는 래퍼
/// </summary>
public partial class TeachingPoint : ObservableObject
{
    /// <summary>포인트 이름 (예: "B1", "PickPos1")</summary>
    [ObservableProperty] private string _name = string.Empty;

    /// <summary>카테고리 (예: "Zone2_Bolt_Z", "Zone3_BoltRef")</summary>
    [ObservableProperty] private string _category = string.Empty;

    /// <summary>해당 Zone 번호 (1, 2, 3)</summary>
    public int Zone { get; set; }

    /// <summary>티칭 모드 (Full, XYOnly, ZOnly)</summary>
    public TeachMode TeachMode { get; set; } = TeachMode.Full;

    /// <summary>티칭된 좌표</summary>
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _z;

    /// <summary>티칭 완료 여부</summary>
    [ObservableProperty] private bool _isTaught;

    /// <summary>볼트 포인트일 때 목표 토크</summary>
    public double? TargetTorqueNm { get; set; }

    /// <summary>볼트 포인트일 때 토크 허용 오차</summary>
    public double? TorqueTolerance { get; set; }

    /// <summary>TeachMode 표시 텍스트</summary>
    public string ModeLabel => TeachMode switch
    {
        TeachMode.XYOnly => "XY",
        TeachMode.ZOnly => "Z",
        _ => "XYZ",
    };

    /// <summary>현재 좌표로 티칭 (TeachMode에 따라 부분 업데이트)</summary>
    public void Teach(double x, double y, double z)
    {
        switch (TeachMode)
        {
            case TeachMode.XYOnly:
                X = x; Y = y;
                break;
            case TeachMode.ZOnly:
                Z = z;
                break;
            default:
                X = x; Y = y; Z = z;
                break;
        }
        IsTaught = true;
    }
}
