namespace IBTM.Presentation.Models;

public sealed record ProcessStageDefinition(
    ProcessStage Stage,
    int Zone,
    string ActivityKey,
    string StatusKey,
    bool ShowInProgress = true,
    bool ArrivesShuttle = false,
    bool ReleasesShuttle = false,
    bool StartsZoneTiming = false,
    bool CompletesZoneTiming = false);

public static class ProcessStageCatalog
{
    public static IReadOnlyList<ProcessStageDefinition> All { get; } =
    [
        new(PcbPlacementStages.WaitShuttle, 1, "Act_WaitShuttle", "Stat_Z1_WaitShuttle"),
        new(PcbPlacementStages.StopAlignLift, 1, "Act_AlignLift", "Stat_Z1_AlignLift", ArrivesShuttle: true, StartsZoneTiming: true),
        new(PcbPlacementStages.PickPlace, 1, "Act_PcbPick", "Stat_Z1_PickPlace"),
        new(PcbPlacementStages.Release, 1, "Act_Release", "Stat_Z1_Release", ReleasesShuttle: true, CompletesZoneTiming: true),

        new(BoltFasteningStages.WaitShuttle, 2, "Act_WaitShuttle", "Stat_Z2_WaitShuttle"),
        new(BoltFasteningStages.StopAlignLift, 2, "Act_AlignLift", "Stat_Z2_AlignLift", ArrivesShuttle: true, StartsZoneTiming: true),
        new(BoltFasteningStages.Fiducial, 2, "Act_Fiducial", "Stat_Z2_Fiducial"),
        new(BoltFasteningStages.Tighten, 2, "Act_BoltTighten", "Stat_Z2_BoltTighten"),
        new(BoltFasteningStages.Release, 2, "Act_Release", "Stat_Z2_Release", ReleasesShuttle: true, CompletesZoneTiming: true),

        new(InspectionStages.WaitShuttle, 3, "Act_WaitShuttle", "Stat_Z3_WaitShuttle"),
        new(InspectionStages.StopAlignLift, 3, "Act_AlignLift", "Stat_Z3_AlignLift", ArrivesShuttle: true, StartsZoneTiming: true),
        new(InspectionStages.Inspect, 3, "Act_Inspecting", "Stat_Z3_Inspect"),
        new(InspectionStages.NgTransfer, 3, "Act_NgTransfer", "Stat_Z3_NgTransfer"),
        new(InspectionStages.SmemaWait, 3, "Act_SmemaWait", "Stat_Z3_SmemaWait"),
        new(InspectionStages.Discharge, 3, "Act_Discharging", "Stat_Z3_Discharge", CompletesZoneTiming: true),
        new(InspectionStages.Release, 3, "Act_Release", "Stat_Z3_Release", ShowInProgress: false, ReleasesShuttle: true, CompletesZoneTiming: true),
    ];

    private static readonly IReadOnlyDictionary<ProcessStage, ProcessStageDefinition> ByStage =
        All.ToDictionary(definition => definition.Stage);

    public static ProcessStageDefinition Get(ProcessStage stage) => ByStage[stage];

    public static IEnumerable<ProcessStageDefinition> GetProgressStages(int zone) =>
        All.Where(definition => definition.Zone == zone && definition.ShowInProgress);
}
