using System.ComponentModel;
using System.Text.Json.Serialization;

namespace IBTM.Device;

[JsonConverter(typeof(JsonStringEnumConverter<InputIo>))]
public enum InputIo
{
    [Description("Available From Front 2 (Heat Sink)")]
    MainConveyorAvailableFromFront2,

    [Description("PCB Buffer PCB Present")]
    PcbBufferPcbPresent,

    [Description("PCB Placement Stopper Up")]
    PcbPlacementStopperUp,

    [Description("PCB Placement Stopper Down")]
    PcbPlacementStopperDown,

    [Description("PCB Placement Heat Sink 1 Present")]
    PcbPlacementHeatSink1Present,

    [Description("PCB Placement Backup Plate Up")]
    PcbPlacementBackupPlateUp,

    [Description("PCB Placement Backup Plate Down")]
    PcbPlacementBackupPlateDown,

    [Description("Placement IPM Gripper Closed")]
    PcbPlacementIpmGripperClosed,

    [Description("Placement IPM Gripper Open")]
    PcbPlacementIpmGripperOpen,

    [Description("PCB Placement Heat Sink 2 Present")]
    PcbPlacementHeatSink2Present,

    [Description("PCB Placement Carrier Present")]
    PcbPlacementCarrierPresent,

    [Description("Available From Front 1 (PCB)")]
    PcbSupplyAvailableFromFront1,

    [Description("Supply Handler Unrotated")]
    PcbSupplyUnrotated,

    [Description("Supply Handler Rotated")]
    PcbSupplyRotated,

    [Description("Supply Nest Forward")]
    PcbSupplyNestForward,

    [Description("Supply Nest Backward")]
    PcbSupplyNestBackward,

    [Description("Supply IPM Fixer Forward")]
    PcbSupplyIpmFixerForward,

    [Description("Supply IPM Fixer Backward")]
    PcbSupplyIpmFixerBackward,

    [Description("Bolt Fastening Stopper Up")]
    BoltFasteningStopperUp,

    [Description("Bolt Fastening Stopper Down")]
    BoltFasteningStopperDown,

    [Description("Bolt Fastening Heat Sink 1 Present")]
    BoltFasteningHeatSink1Present,

    [Description("Bolt Fastening Backup Plate Up")]
    BoltFasteningBackupPlateUp,

    [Description("Bolt Fastening Backup Plate Down")]
    BoltFasteningBackupPlateDown,

    [Description("Bolt Fastening Heat Sink 2 Present")]
    BoltFasteningHeatSink2Present,

    [Description("Bolt Fastening Carrier Present")]
    BoltFasteningCarrierPresent,

    [Description("Bolt Head 2 Vacuum Detected")]
    BoltHead2VacuumDetected,

    [Description("Placement Handler Down")]
    PcbPlacementHandlerDown,

    [Description("Placement Handler Up")]
    PcbPlacementHandlerUp,

    [Description("Placement Handler Rotated")]
    PcbPlacementHandlerRotated,

    [Description("Placement Handler Unrotated")]
    PcbPlacementHandlerUnrotated,

    [Description("Placement IPM Down")]
    PcbPlacementIpmDown,

    [Description("Placement IPM Up")]
    PcbPlacementIpmUp,

    [Description("Placement Vacuum Detected")]
    PcbPlacementVacuumDetected,

    [Description("Bolt Table Down")]
    BoltTableDown,

    [Description("Bolt Table Up")]
    BoltTableUp,

    [Description("Bolt Head 1 Down")]
    BoltHead1Down,

    [Description("Bolt Head 1 Up")]
    BoltHead1Up,

    [Description("Bolt Head 2 Down")]
    BoltHead2Down,

    [Description("Bolt Head 2 Up")]
    BoltHead2Up,

    [Description("Bolt Head 1 Vacuum Detected")]
    BoltHead1VacuumDetected,

    [Description("Linear Feeder Bolt Detected")]
    LinearFeederBoltDetected,

    [Description("Shooting Tube Bolt Detected")]
    ShootingTubeBoltDetected,

    [Description("Shooting Escape Forward")]
    ShootingEscapeForward,

    [Description("Shooting Escape Backward")]
    ShootingEscapeBackward,

    [Description("Pickup Feeder Bolt Detected")]
    PickupFeederBoltDetected,

    [Description("Inspection Stopper Up")]
    InspectionStopperUp,

    [Description("Inspection Stopper Down")]
    InspectionStopperDown,

    [Description("Inspection Heat Sink 1 Present")]
    InspectionHeatSink1Present,

    [Description("Inspection Backup Plate Up")]
    InspectionBackupPlateUp,

    [Description("Inspection Backup Plate Down")]
    InspectionBackupPlateDown,

    [Description("NG Carrier Gripper Closed")]
    NgCarrierGripperClosed,

    [Description("NG Carrier Gripper Open")]
    NgCarrierGripperOpen,

    [Description("Inspection Heat Sink 2 Present")]
    InspectionHeatSink2Present,

    [Description("Inspection Carrier Present")]
    InspectionCarrierPresent,

    [Description("NG Carrier Pickup Down")]
    NgCarrierPickupDown,

    [Description("NG Carrier Pickup Up")]
    NgCarrierPickupUp,

    [Description("NG Carrier Detected")]
    NgCarrierDetected,

    [Description("NG Shuttle Down")]
    NgShuttleDown,

    [Description("NG Shuttle Up")]
    NgShuttleUp,

    [Description("NG Shuttle Carrier Detected")]
    NgShuttleCarrierDetected,

    [Description("NG Conveyor Position 1 Occupied")]
    NgConveyorPosition1Occupied,

    [Description("NG Conveyor Position 2 Occupied")]
    NgConveyorPosition2Occupied,

    [Description("NG Conveyor Position 3 Occupied")]
    NgConveyorPosition3Occupied,

    [Description("NG Conveyor Stopper Up")]
    NgConveyorStopperUp,

    [Description("NG Conveyor Stopper Down")]
    NgConveyorStopperDown,

    [Description("NG Carrier Eject Button")]
    NgCarrierEjectButton,

    [Description("NG Carrier Eject Complete Button")]
    NgCarrierEjectCompleteButton,

    [Description("Ready From Rear")]
    MainConveyorReadyFromRear,

    [Description("Emergency Stop 1 Pressed")]
    EmergencyStop1Pressed,

    [Description("Emergency Stop 2 Pressed")]
    EmergencyStop2Pressed,

    [Description("Auto Mode")]
    AutoMode,

    [Description("Reset Button")]
    ResetButton,

    [Description("Door 1 Open")]
    Door1Open,

    [Description("Door 2 Open")]
    Door2Open,

    [Description("Door 3 Open")]
    Door3Open,

    [Description("Door 4 Open")]
    Door4Open,

    [Description("Door 5 Open")]
    Door5Open,

    [Description("Door 6 Open")]
    Door6Open,

    [Description("Servo Main Contactor On")]
    ServoMainContactorOn,

    [Description("Air Pressure Low")]
    AirPressureLow,

    [Description("Placement Handler PCB Detected")]
    PcbPlacementPcbDetected,

    [Description("Supply Handler PCB Detected")]
    PcbSupplyPcbDetected,

    [Description("Main Conveyor Entry Carrier Detected")]
    MainConveyorEntryCarrierDetected,

    [Description("Main Conveyor Exit Carrier Detected")]
    MainConveyorExitCarrierDetected,
}

[JsonConverter(typeof(JsonStringEnumConverter<OutputIo>))]
public enum OutputIo
{
    [Description("Ready To Front 2 (Heat Sink)")]
    MainConveyorReadyToFront2,

    [Description("PCB Placement Stopper Up")]
    PcbPlacementStopperUp,

    [Description("PCB Placement Backup Plate Up")]
    PcbPlacementBackupPlateUp,

    [Description("Placement IPM Gripper Close")]
    PcbPlacementIpmGripperClose,

    [Description("Ready To Front 1 (PCB)")]
    PcbSupplyReadyToFront1,

    [Description("Supply Handler Rotate")]
    PcbSupplyRotate,

    [Description("Supply Nest Forward")]
    PcbSupplyNestForward,

    [Description("Supply IPM Fixer Forward")]
    PcbSupplyIpmFixerForward,

    [Description("Placement Handler Down")]
    PcbPlacementHandlerDown,

    [Description("Placement Handler Rotate")]
    PcbPlacementHandlerRotate,

    [Description("Placement IPM Down")]
    PcbPlacementIpmDown,

    [Description("Placement Vacuum Ejector")]
    PcbPlacementVacuumEjector,

    [Description("Bolt Table Down")]
    BoltTableDown,

    [Description("Bolt Head 1 Down")]
    BoltHead1Down,

    [Description("Bolt Head 2 Down")]
    BoltHead2Down,

    [Description("Bolt Head 1 Vacuum Pump")]
    BoltHead1VacuumPump,

    [Description("Bolt Fastening Stopper Up")]
    BoltFasteningStopperUp,

    [Description("Bolt Fastening Backup Plate Up")]
    BoltFasteningBackupPlateUp,

    [Description("Bolt Head 2 Vacuum Pump")]
    BoltHead2VacuumPump,

    [Description("Inspection Stopper Up")]
    InspectionStopperUp,

    [Description("Inspection Backup Plate Up")]
    InspectionBackupPlateUp,

    [Description("NG Carrier Pickup Down")]
    NgCarrierPickupDown,

    [Description("NG Carrier Gripper Close")]
    NgCarrierGripperClose,

    [Description("NG Shuttle Down")]
    NgShuttleDown,

    [Description("Linear Feeder Run Signal")]
    LinearFeederRunSignal,

    [Description("Shooting Escape Forward")]
    ShootingEscapeForward,

    [Description("Shoot Bolt")]
    ShootBolt,

    [Description("Main Conveyor Run")]
    MainConveyorRun,

    [Description("Main Conveyor Reverse")]
    MainConveyorReverse,

    [Description("Main Conveyor Normal Speed")]
    MainConveyorNormalSpeed,

    [Description("NG Conveyor Stopper Up")]
    NgConveyorStopperUp,

    [Description("NG Conveyor Run")]
    NgConveyorRun,

    [Description("NG Conveyor Reverse")]
    NgConveyorReverse,

    [Description("NG Conveyor Normal Speed")]
    NgConveyorNormalSpeed,

    [Description("NG Carrier Eject Lamp")]
    NgCarrierEjectLamp,

    [Description("NG Carrier Eject Complete Lamp")]
    NgCarrierEjectCompleteLamp,

    [Description("Available To Rear")]
    MainConveyorAvailableToRear,

    [Description("Tower Lamp Green")]
    TowerLampGreen,

    [Description("Tower Lamp Yellow")]
    TowerLampYellow,

    [Description("Tower Lamp Red")]
    TowerLampRed,

    [Description("Buzzer")]
    Buzzer,

    [Description("Machine Light")]
    MachineLight,
}

[JsonConverter(typeof(JsonStringEnumConverter<MachineAxis>))]
public enum MachineAxis
{
    [Description("PCB Supply Handler X")]
    PcbSupplyX,

    [Description("PCB Supply Handler Y")]
    PcbSupplyY,

    [Description("PCB Supply Handler Z")]
    PcbSupplyZ,

    [Description("PCB Placement Handler X")]
    PcbPlacementHandlerX,

    [Description("PCB Placement Handler Y")]
    PcbPlacementHandlerY,

    [Description("PCB Placement Handler Z")]
    PcbPlacementHandlerZ,

    [Description("Bolt Fastening X")]
    BoltFasteningX,

    [Description("Bolt Fastening Y")]
    BoltFasteningY,

    [Description("Bolt Fastening Z")]
    BoltFasteningZ,

    [Description("Inspection Gantry X")]
    InspectionGantryX,

    [Description("Inspection Gantry Y")]
    InspectionGantryY,
}

[JsonConverter(typeof(JsonStringEnumConverter<AxisDirection>))]
public enum AxisDirection
{
    [Description("Positive")]
    Positive = 1,

    [Description("Negative")]
    Negative = -1,
}

