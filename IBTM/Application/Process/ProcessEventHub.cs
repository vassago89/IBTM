using System.Windows.Media;

namespace IBTM.Application.Process;

public sealed class ProcessEventHub
{
    public event EventHandler<StageChangedEventArgs>? StageChanged;
    public event EventHandler<LogEntry>? LogAdded;
    public event EventHandler<ProductionStats>? StatsUpdated;
    public event EventHandler<ZonePositionEventArgs>? ZonePositionChanged;
    public event EventHandler<FiducialResult>? FiducialDetected;
    public event EventHandler<BoltResult>? BoltCompleted;
    public event EventHandler<BoltProgressEventArgs>? BoltProgress;
    public event EventHandler<InspectionResult>? InspectionDone;
    public event EventHandler<InspectionResult>? RouteDecided;
    public event EventHandler<int>? NgStackUpdated;
    public event EventHandler<int>? NgStackAlarm;
    public event EventHandler<(int Zone, bool Active)>? GripperChanged;
    public event EventHandler? PcbPlaced;
    public event EventHandler<ImageSource>? InspectionImageCaptured;

    public void Stage(ProcessStage stage, StageStatus status) =>
        StageChanged?.Invoke(this, new StageChangedEventArgs(stage, status));

    public void Log(string message, ProcessStage stage, LogLevel level = LogLevel.Info) =>
        LogAdded?.Invoke(
            this,
            new LogEntry { Message = message, Stage = stage.ToString(), Level = level });

    public void Stats(ProductionStats stats) => StatsUpdated?.Invoke(this, stats);
    public void Position(ZonePositionEventArgs position) => ZonePositionChanged?.Invoke(this, position);
    public void Fiducial(FiducialResult result) => FiducialDetected?.Invoke(this, result);
    public void Bolt(BoltResult result) => BoltCompleted?.Invoke(this, result);
    public void BoltProgressed(BoltProgressEventArgs progress) => BoltProgress?.Invoke(this, progress);
    public void Inspection(InspectionOutcome outcome)
    {
        InspectionImageCaptured?.Invoke(this, outcome.Image);
        InspectionDone?.Invoke(this, outcome.Result);
        RouteDecided?.Invoke(this, outcome.Result);
    }

    public void NgStack(int count) => NgStackUpdated?.Invoke(this, count);
    public void NgAlarm(int count) => NgStackAlarm?.Invoke(this, count);
    public void Gripper((int Zone, bool Active) state) => GripperChanged?.Invoke(this, state);
    public void PcbWasPlaced() => PcbPlaced?.Invoke(this, EventArgs.Empty);
}
