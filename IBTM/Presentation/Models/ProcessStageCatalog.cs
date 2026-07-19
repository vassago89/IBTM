namespace IBTM.Presentation.Models;

public sealed record ProcessStageDefinition(
    ProcessStage Stage,
    int Zone,
    string ActivityKey,
    string StatusKey,
    bool ShowInProgress = true,
    bool StartsZoneTiming = false,
    bool CompletesZoneTiming = false);

public static class ProcessStageCatalog
{
    public static IReadOnlyList<ProcessStageDefinition> All { get; } =
    [
        new(ProcessStage.Zone1_WaitShuttle, 1, "Act_WaitShuttle", "Stat_Z1_WaitShuttle"),
        new(ProcessStage.Zone1_StopAlignLift, 1, "Act_AlignLift", "Stat_Z1_AlignLift", StartsZoneTiming: true),
        new(ProcessStage.Zone1_PickPlace, 1, "Act_PcbPick", "Stat_Z1_PickPlace"),
        new(ProcessStage.Zone1_Release, 1, "Act_Release", "Stat_Z1_Release", CompletesZoneTiming: true),

        new(ProcessStage.Zone2_WaitShuttle, 2, "Act_WaitShuttle", "Stat_Z2_WaitShuttle"),
        new(ProcessStage.Zone2_StopAlignLift, 2, "Act_AlignLift", "Stat_Z2_AlignLift", StartsZoneTiming: true),
        new(ProcessStage.Zone2_Fiducial, 2, "Act_Fiducial", "Stat_Z2_Fiducial"),
        new(ProcessStage.Zone2_BoltTighten, 2, "Act_BoltTighten", "Stat_Z2_BoltTighten"),
        new(ProcessStage.Zone2_Release, 2, "Act_Release", "Stat_Z2_Release", CompletesZoneTiming: true),

        new(ProcessStage.Zone3_WaitShuttle, 3, "Act_WaitShuttle", "Stat_Z3_WaitShuttle"),
        new(ProcessStage.Zone3_StopAlignLift, 3, "Act_AlignLift", "Stat_Z3_AlignLift", StartsZoneTiming: true),
        new(ProcessStage.Zone3_Inspect, 3, "Act_Inspecting", "Stat_Z3_Inspect"),
        new(ProcessStage.Zone3_NgTransfer, 3, "Act_NgTransfer", "Stat_Z3_NgTransfer"),
        new(ProcessStage.Zone3_SmemaWait, 3, "Act_SmemaWait", "Stat_Z3_SmemaWait"),
        new(ProcessStage.Zone3_Discharge, 3, "Act_Discharging", "Stat_Z3_Discharge"),
        new(ProcessStage.Zone3_Release, 3, "Act_Release", "Stat_Z3_Release", ShowInProgress: false, CompletesZoneTiming: true),
    ];

    private static readonly IReadOnlyDictionary<ProcessStage, ProcessStageDefinition> ByStage =
        All.ToDictionary(definition => definition.Stage);

    public static ProcessStageDefinition? Find(ProcessStage stage) =>
        ByStage.GetValueOrDefault(stage);

    public static ProcessStageDefinition Get(ProcessStage stage) => ByStage[stage];

    public static IEnumerable<ProcessStageDefinition> GetProgressStages(int zone) =>
        All.Where(definition => definition.Zone == zone && definition.ShowInProgress);
}
