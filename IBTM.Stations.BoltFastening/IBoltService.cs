namespace IBTM.Stations.BoltFastening;

public interface IBoltService
{
    Task<bool> InitializeAsync(CancellationToken cancellationToken = default);
    Task<bool> ShootAsync(CancellationToken cancellationToken = default);
    Task<BoltResult> TightenAsync(
        double targetTorqueNm,
        CancellationToken cancellationToken = default);
}
