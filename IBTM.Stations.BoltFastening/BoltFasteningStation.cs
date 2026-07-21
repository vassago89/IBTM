using Microsoft.Extensions.DependencyInjection;

namespace IBTM.Stations.BoltFastening;

internal sealed class BoltFasteningStation : IBoltFasteningStation, IDisposable
{
    private readonly StationOperations _machine;
    private readonly ProcessStageRunner _stages;
    private readonly ProcessEventHub _events;
    private readonly IFiducialService _fiducial;
    private readonly BoltTighteningService _boltTightening;

    public BoltFasteningStation(
        [FromKeyedServices(BoltFasteningModule.ServiceKey)] IMotionService motion,
        IIOService io,
        BoltFasteningOptions options,
        MachineRuntimeSettings runtime,
        ProcessStageRunner stages,
        ProcessEventHub events,
        IFiducialService fiducial,
        IBoltService boltController)
    {
        _machine = new StationOperations(2, motion, io, options.Motion, runtime, events);
        _stages = stages;
        _events = events;
        _fiducial = fiducial;
        _boltTightening = new BoltTighteningService(_machine, boltController, options);

        _boltTightening.ProgressChanged += OnBoltProgressChanged;
        _boltTightening.BoltCompleted += OnBoltCompleted;
    }

    public void Initialize() => _machine.Initialize(3, 4, 5);

    public Task PrepareAsync(CancellationToken cancellationToken) =>
        _boltTightening.InitializeAsync(cancellationToken);

    public async Task RunAsync(
        BoltFasteningRecipe recipe,
        CancellationToken cancellationToken)
    {
        await _stages.RunAsync(
            BoltFasteningStages.WaitShuttle,
            cancellationToken,
            token => _machine.WaitForSignalAsync(token));

        await _stages.RunAsync(
            BoltFasteningStages.StopAlignLift,
            cancellationToken,
            token => _machine.StopAlignLiftAsync(
                BoltFasteningChannels.Stopper,
                BoltFasteningChannels.Align,
                BoltFasteningChannels.Lift,
                token));

        var fiducial = await _stages.RunAsync(
            BoltFasteningStages.Fiducial,
            cancellationToken,
            token => DetectFiducialAsync(recipe, token));

        await _stages.RunAsync(
            BoltFasteningStages.Tighten,
            cancellationToken,
            token => _boltTightening.RunAsync(recipe, fiducial, token));

        await _stages.RunAsync(
            BoltFasteningStages.Release,
            cancellationToken,
            token => _machine.ReleaseAsync(
                BoltFasteningChannels.Stopper,
                BoltFasteningChannels.Align,
                BoltFasteningChannels.Lift,
                token));
    }

    public void Stop() => _machine.Stop();

    public void EmergencyStop() => _machine.EmergencyStop();

    public void Dispose()
    {
        _boltTightening.ProgressChanged -= OnBoltProgressChanged;
        _boltTightening.BoltCompleted -= OnBoltCompleted;
        _machine.Dispose();
    }

    private async Task<FiducialResult> DetectFiducialAsync(
        BoltFasteningRecipe recipe,
        CancellationToken cancellationToken)
    {
        await _machine.MoveToPositionAsync(recipe.FiducialPosition, cancellationToken);

        var result = await _fiducial.DetectFromCameraAsync(cancellationToken);
        _events.Fiducial(result);

        if (!result.Found)
        {
            throw new InvalidOperationException("Fiducial detection failed.");
        }

        return result;
    }

    private void OnBoltProgressChanged(object? sender, BoltProgressEventArgs progress) =>
        _events.BoltProgressed(progress);

    private void OnBoltCompleted(object? sender, BoltResult result) => _events.Bolt(result);
}
