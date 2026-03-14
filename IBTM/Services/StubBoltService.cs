namespace IBTM.Services;

/// <summary>
/// 볼트 체결기 Stub - 실제 프로토콜 확정 후 교체
/// TODO: 통신 프로토콜(RS232/TCP 등) 확정되면 실제 구현으로 교체
/// </summary>
public class StubBoltService : IBoltService
{
    public async Task<bool> InitializeAsync(CancellationToken ct = default)
    {
        await Task.Delay(200, ct);
        return true;
    }

    public async Task<bool> ShootAsync(CancellationToken ct = default)
    {
        // TODO: 체결기 Shooting 명령 전송
        await Task.Delay(800, ct);
        return true;
    }

    public async Task<BoltResult> TightenAsync(double targetTorqueNm, CancellationToken ct = default)
    {
        // TODO: 체결 명령 전송 및 토크 피드백 수신
        await Task.Delay(1800, ct);

        // 시뮬레이션: 목표 토크 ±5% 내 편차
        var actual = targetTorqueNm + (Random.Shared.NextDouble() - 0.5) * targetTorqueNm * 0.08;
        var tolerance = targetTorqueNm * 0.05;
        var ok = Math.Abs(actual - targetTorqueNm) <= tolerance;

        return new BoltResult(ok, actual, ok ? "OK" : $"NG (허용범위 ±{tolerance:F2}Nm 초과)");
    }
}
