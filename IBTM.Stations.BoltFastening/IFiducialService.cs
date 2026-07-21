namespace IBTM.Stations.BoltFastening;

public interface IFiducialService
{
    Task<FiducialResult> DetectFromCameraAsync(CancellationToken cancellationToken = default);
}
