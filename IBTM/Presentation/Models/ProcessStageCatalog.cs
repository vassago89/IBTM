using System.Linq;
using IBTM.Stations.BoltFastening;
using IBTM.Stations.Inspection;
using IBTM.Stations.PcbPlacement;

namespace IBTM.Presentation.Models;

internal sealed record ProcessStageDefinition(
    string Stage,
    int Station,
    string ActivityText,
    string StatusText);

internal static class ProcessStageCatalog
{
    private static readonly ProcessStageDefinition[] Definitions =
    [
        new(PcbPlacementStages.ReceiveCarrierJig, 1, "SMEMA IN", "[PCB Placement] Receiving carrier jig"),
        new(PcbPlacementStages.PositionCarrierJig, 1, "CARRIER JIG", "[PCB Placement] Positioning carrier jig"),
        new(PcbPlacementStages.PickPlace, 1, "PCB PICK", "[PCB Placement] Picking PCB"),

        new(BoltFasteningStages.PositionCarrierJig, 2, "CARRIER JIG", "[Bolt Fastening] Positioning carrier jig"),
        new(BoltFasteningStages.Fiducial, 2, "FIDUCIAL", "[Bolt Fastening] Detecting fiducial"),
        new(BoltFasteningStages.Tighten, 2, "BOLT TIGHTEN", "[Bolt Fastening] Tightening bolts"),

        new(InspectionStages.PositionCarrierJig, 3, "CARRIER JIG", "[Inspection] Positioning carrier jig"),
        new(InspectionStages.Inspect, 3, "INSPECTING", "[Inspection] Inspecting"),
        new(InspectionStages.StackNgCarrierJig, 3, "NG STACK", "[Inspection] Stacking NG carrier jig"),
        new(InspectionStages.SendCarrierJig, 3, "SMEMA OUT", "[Inspection] Sending carrier jig"),
    ];

    public static ProcessStageDefinition Get(string stage) =>
        Definitions.Single(definition => definition.Stage == stage);
}
