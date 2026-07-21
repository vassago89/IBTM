namespace IBTM.Infrastructure.Simulation;

public sealed class SimulatedFiducialService : IFiducialService
{
    public async Task<FiducialResult> DetectFromCameraAsync(
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(400, cancellationToken);
        var offsetX = (Random.Shared.NextDouble() - 0.5) * 0.4;
        var offsetY = (Random.Shared.NextDouble() - 0.5) * 0.4;
        var confidence = 0.93 + Random.Shared.NextDouble() * 0.06;
        return new FiducialResult(true, offsetX, offsetY, confidence);
    }
}
