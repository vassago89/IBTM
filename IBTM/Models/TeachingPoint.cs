using CommunityToolkit.Mvvm.ComponentModel;

namespace IBTM.Models;

/// <summary>
/// 티칭 포인트 — 레시피의 각 위치를 UI에서 편집할 수 있는 래퍼
/// </summary>
public partial class TeachingPoint : ObservableObject
{
    /// <summary>포인트 이름 (예: "B1", "Zone1_PickPos1")</summary>
    [ObservableProperty] private string _name = string.Empty;

    /// <summary>카테고리 (예: "Zone2_Bolt", "Zone3_Inspect")</summary>
    [ObservableProperty] private string _category = string.Empty;

    /// <summary>해당 Zone 번호 (1, 2, 3)</summary>
    public int Zone { get; set; }

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

    /// <summary>현재 좌표로 티칭</summary>
    public void Teach(double x, double y, double z)
    {
        X = x; Y = y; Z = z;
        IsTaught = true;
    }
}
