namespace IBTM.Application.Abstractions;

public record FiducialResult(bool Found, double OffsetX, double OffsetY, double Confidence);

public interface IFiducialService
{
    Task<FiducialResult> DetectFromCameraAsync(CancellationToken ct = default);
}
