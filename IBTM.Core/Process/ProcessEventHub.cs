namespace IBTM.Core.Process;

public sealed class ProcessEventHub
{
    public event EventHandler<StageChangedEventArgs>? StageChanged;
    public event EventHandler<ProductionStats>? StatsUpdated;
    public event EventHandler<ZonePositionEventArgs>? ZonePositionChanged;
    public event EventHandler<FiducialResult>? FiducialDetected;
    public event EventHandler<BoltResult>? BoltCompleted;
    public event EventHandler<BoltProgressEventArgs>? BoltProgress;
    public event EventHandler<InspectionOutcome>? InspectionCompleted;
    public event EventHandler<NgStackState>? NgStackChanged;
    public event EventHandler<(int Zone, bool Active)>? GripperChanged;
    public event EventHandler? PcbPlaced;

    public void Stage(ProcessStage stage, StageStatus status) =>
        StageChanged?.Invoke(this, new StageChangedEventArgs(stage, status));

    public void Stats(ProductionStats stats) => StatsUpdated?.Invoke(this, stats);
    public void Position(ZonePositionEventArgs position) => ZonePositionChanged?.Invoke(this, position);
    public void Fiducial(FiducialResult result) => FiducialDetected?.Invoke(this, result);
    public void Bolt(BoltResult result) => BoltCompleted?.Invoke(this, result);
    public void BoltProgressed(BoltProgressEventArgs progress) => BoltProgress?.Invoke(this, progress);
    public void Inspection(InspectionOutcome outcome) =>
        InspectionCompleted?.Invoke(this, outcome);

    public void NgStack(NgStackState state) => NgStackChanged?.Invoke(this, state);
    public void Gripper((int Zone, bool Active) state) => GripperChanged?.Invoke(this, state);
    public void PcbWasPlaced() => PcbPlaced?.Invoke(this, EventArgs.Empty);
}
