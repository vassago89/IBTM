using IBTM.Ajin;
using IBTM.AlphaMotion;
using IBTM.BoltFeeder;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Device;
using IBTM.Hantas;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbBuffer;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;

namespace IBTM;

public sealed class MachineSettings
{
    public DriverSettings Drivers { get; set; } = new();
    public UnitSettings Units { get; set; } = new();
    public MachineOptions Options { get; set; } = new();
    public HomeSettings Home { get; set; } = new();
    public AjinSettings Ajin { get; set; } = new();
    public AlphaMotionSettings AlphaMotion { get; set; } = new();
    public HantasSettings Hantas { get; set; } = new();
    public InspectionCameraSettings InspectionCamera { get; set; } = new();
    public BoltInspectionSettings BoltInspection { get; set; } = new();
    public LightingSettings Lighting { get; set; } = new();

    public PcbSupplySettings PcbSupply { get; set; } = new();
    public PcbSupplyHardwareSettings PcbSupplyHardware { get; set; } = new();
    public PcbBufferSettings PcbBuffer { get; set; } = new();
    public PcbBufferHardwareSettings PcbBufferHardware { get; set; } = new();
    public PcbPlacementHandlerSettings PcbPlacementHandler { get; set; } = new();
    public PcbPlacementHandlerHardwareSettings PcbPlacementHandlerHardware { get; set; } = new();
    public PcbPlacementStationHardwareSettings PcbPlacementStationHardware { get; set; } = new();
    public BoltFeederSettings BoltFeeder { get; set; } = new();
    public BoltFeederHardwareSettings BoltFeederHardware { get; set; } = new();
    public BoltFasteningSettings BoltFastening { get; set; } = new();
    public BoltFasteningHardwareSettings BoltFasteningHardware { get; set; } = new();
    public BoltFasteningStationHardwareSettings BoltFasteningStationHardware { get; set; } = new();
    public InspectionGantrySettings InspectionGantry { get; set; } = new();
    public InspectionGantryHardwareSettings InspectionGantryHardware { get; set; } = new();
    public InspectionStationHardwareSettings InspectionStationHardware { get; set; } = new();

    public MachineHardwareSettings MachineHardware { get; set; } = new();
    public ConveyorHardwareSettings ConveyorHardware { get; set; } = new();
    public NgShuttleHardwareSettings NgShuttleHardware { get; set; } = new();
    public NgConveyorHardwareSettings NgConveyorHardware { get; set; } = new();

    public HardwareSettings[] HardwareSections =>
    [
        MachineHardware,
        PcbSupplyHardware,
        PcbBufferHardware,
        PcbPlacementHandlerHardware,
        PcbPlacementStationHardware,
        BoltFeederHardware,
        BoltFasteningHardware,
        BoltFasteningStationHardware,
        InspectionGantryHardware,
        InspectionStationHardware,
        ConveyorHardware,
        NgShuttleHardware,
        NgConveyorHardware,
    ];
}

