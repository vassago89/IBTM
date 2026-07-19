
namespace IBTM.Application.Process;

public sealed class Zone2Workflow : IDisposable
{
    private const int Zone = 2;

    private readonly MachineOperations _machine;
    private readonly ProcessStageRunner _stages;
    private readonly ProcessEventHub _events;
    private readonly IFiducialService _fiducial;
    private readonly BoltTighteningService _boltTightening;

    public Zone2Workflow(
        MachineOperations machine,
        ProcessStageRunner stages,
        ProcessEventHub events,
        IFiducialService fiducial,
        BoltTighteningService boltTightening)
    {
        _machine = machine;
        _stages = stages;
        _events = events;
        _fiducial = fiducial;
        _boltTightening = boltTightening;

        _boltTightening.ProgressChanged += OnBoltProgressChanged;
        _boltTightening.BoltCompleted += OnBoltCompleted;
        _boltTightening.LogAdded += OnBoltLogAdded;
    }

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        _boltTightening.InitializeAsync(cancellationToken);

    public async Task RunAsync(Recipe recipe, CancellationToken cancellationToken)
    {
        await _stages.RunAsync(
            ProcessStage.Zone2_WaitShuttle,
            cancellationToken,
            async token =>
            {
                _events.Log("[Zone2] Waiting for shuttle", ProcessStage.Zone2_WaitShuttle);
                await _machine.WaitForSensorAsync(IoMap.Zone2_Sensor, timeout: null, token);
                _events.Log("[Zone2] Shuttle arrived", ProcessStage.Zone2_WaitShuttle);
            });

        await _stages.RunAsync(
            ProcessStage.Zone2_StopAlignLift,
            cancellationToken,
            token => _machine.StopAlignLiftAsync(
                IoMap.Zone2_Stopper,
                IoMap.Zone2_Align,
                IoMap.Zone2_Lift,
                token));

        var fiducial = await _stages.RunAsync(
            ProcessStage.Zone2_Fiducial,
            cancellationToken,
            token => DetectFiducialAsync(recipe, token));

        await _stages.RunAsync(
            ProcessStage.Zone2_BoltTighten,
            cancellationToken,
            token => _boltTightening.RunAsync(recipe, fiducial, token));

        await _stages.RunAsync(
            ProcessStage.Zone2_Release,
            cancellationToken,
            token => _machine.ReleaseAsync(
                IoMap.Zone2_Stopper,
                IoMap.Zone2_Align,
                IoMap.Zone2_Lift,
                token));
    }

    public void Dispose()
    {
        _boltTightening.ProgressChanged -= OnBoltProgressChanged;
        _boltTightening.BoltCompleted -= OnBoltCompleted;
        _boltTightening.LogAdded -= OnBoltLogAdded;
    }

    private async Task<FiducialResult> DetectFiducialAsync(
        Recipe recipe,
        CancellationToken cancellationToken)
    {
        _events.Log("[Zone2] Fiducial detection", ProcessStage.Zone2_Fiducial);
        await _machine.MoveToPositionAsync(
            Zone,
            recipe.Zone2_FiducialPos,
            cancellationToken);

        var result = await _fiducial.DetectFromCameraAsync(cancellationToken);
        _events.Fiducial(result);
        _events.Log(
            result.Found
                ? $"[Zone2] Fiducial dX:{result.OffsetX:+0.000;-0.000} dY:{result.OffsetY:+0.000;-0.000} ({result.Confidence:P0})"
                : "[Zone2] Fiducial detection failed",
            ProcessStage.Zone2_Fiducial,
            result.Found ? LogLevel.Info : LogLevel.Warning);
        return result;
    }

    private void OnBoltProgressChanged(object? sender, BoltProgressEventArgs progress) =>
        _events.BoltProgressed(progress);

    private void OnBoltCompleted(object? sender, BoltResult result) => _events.Bolt(result);

    private void OnBoltLogAdded(object? sender, BoltProcessLog log) =>
        _events.Log($"[Zone2] {log.Message}", ProcessStage.Zone2_BoltTighten, log.Level);
}
