using System.Text;

namespace IBTM.Presentation.ViewModels;

public sealed class ProcessStageTracker
{
    private readonly Dictionary<ProcessStage, StageStatus> _statuses =
        ProcessStageCatalog.All
            .ToDictionary(definition => definition.Stage, _ => StageStatus.Idle);

    public StageStatus this[ProcessStage stage] => _statuses[stage];

    public void Set(ProcessStage stage, StageStatus status) => _statuses[stage] = status;

    public void Reset(int zone)
    {
        foreach (var stage in ProcessStageCatalog.GetProgressStages(zone))
        {
            _statuses[stage.Stage] = StageStatus.Idle;
        }
    }

    public string Progress(int zone)
    {
        var progress = new StringBuilder();
        foreach (var definition in ProcessStageCatalog.GetProgressStages(zone))
        {
            var status = _statuses[definition.Stage];
            if (status == StageStatus.Skipped)
            {
                continue;
            }

            progress.Append(status switch
            {
                StageStatus.Done => "●",
                StageStatus.Running => "◉",
                StageStatus.Error => "✕",
                StageStatus.Warning => "▲",
                StageStatus.Idle => "○",
                _ => throw new ArgumentOutOfRangeException(nameof(status)),
            });
        }

        return progress.ToString();
    }
}
