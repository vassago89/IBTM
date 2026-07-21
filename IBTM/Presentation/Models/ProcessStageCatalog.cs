using System.Linq;
using IBTM.Stations.BoltFastening;
using IBTM.Stations.Inspection;
using IBTM.Stations.PcbPlacement;

namespace IBTM.Presentation.Models;

internal sealed record ProcessStageDefinition(
    string Stage,
    int Zone,
    string ActivityText,
    string StatusText);

internal static class ProcessStageCatalog
{
    private static readonly ProcessStageDefinition[] Definitions =
    [
        new(PcbPlacementStages.WaitShuttle, 1, "WAIT SHUTTLE", "[Zone 1] Waiting for shuttle"),
        new(PcbPlacementStages.StopAlignLift, 1, "ALIGN · LIFT", "[Zone 1] Aligning and lifting"),
        new(PcbPlacementStages.PickPlace, 1, "PCB PICK", "[Zone 1] Picking PCB"),
        new(PcbPlacementStages.Release, 1, "RELEASE", "[Zone 1] Releasing shuttle"),

        new(BoltFasteningStages.WaitShuttle, 2, "WAIT SHUTTLE", "[Zone 2] Waiting for shuttle"),
        new(BoltFasteningStages.StopAlignLift, 2, "ALIGN · LIFT", "[Zone 2] Aligning and lifting"),
        new(BoltFasteningStages.Fiducial, 2, "FIDUCIAL", "[Zone 2] Detecting fiducial"),
        new(BoltFasteningStages.Tighten, 2, "BOLT TIGHTEN", "[Zone 2] Tightening bolts"),
        new(BoltFasteningStages.Release, 2, "RELEASE", "[Zone 2] Releasing shuttle"),

        new(InspectionStages.WaitShuttle, 3, "WAIT SHUTTLE", "[Zone 3] Waiting for shuttle"),
        new(InspectionStages.StopAlignLift, 3, "ALIGN · LIFT", "[Zone 3] Aligning and lifting"),
        new(InspectionStages.Inspect, 3, "INSPECTING", "[Zone 3] Inspecting"),
        new(InspectionStages.NgTransfer, 3, "NG TRANSFER", "[Zone 3] Transferring NG"),
        new(InspectionStages.SmemaWait, 3, "SMEMA WAIT", "[Zone 3] Waiting for SMEMA"),
        new(InspectionStages.Discharge, 3, "DISCHARGE", "[Zone 3] Discharging"),
        new(InspectionStages.Release, 3, "RELEASE", "[Zone 3] Releasing shuttle"),
    ];

    public static ProcessStageDefinition Get(string stage) =>
        Definitions.Single(definition => definition.Stage == stage);
}
