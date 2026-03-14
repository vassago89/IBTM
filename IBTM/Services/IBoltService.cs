namespace IBTM.Services;

public record BoltResult(bool Success, double Torque, string Message);

public interface IBoltService
{
    Task<bool> InitializeAsync(CancellationToken ct = default);

    /// <summary>볼트 위치로 Shooting (임팩트 드라이버 하강)</summary>
    Task<bool> ShootAsync(CancellationToken ct = default);

    /// <summary>목표 토크로 Tightening 수행</summary>
    Task<BoltResult> TightenAsync(double targetTorqueNm, CancellationToken ct = default);
}
