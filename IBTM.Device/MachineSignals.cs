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

    [Description("Supply Gripper Closed")]
    PcbSupplyGripperClosed,

    [Description("Supply Gripper Open")]
    PcbSupplyGripperOpen,

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

    [Description("Shooting Head Vacuum Detected (Head 2)")]
    ShootingHeadVacuumDetected,

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

    [Description("Pickup Head Down (Head 1)")]
    PickupHeadDown,

    [Description("Pickup Head Up (Head 1)")]
    PickupHeadUp,

    [Description("Shooting Head Down (Head 2)")]
    ShootingHeadDown,

    [Description("Shooting Head Up (Head 2)")]
    ShootingHeadUp,

    [Description("Pickup Head Vacuum Detected (Head 1)")]
    PickupHeadVacuumDetected,

    [Description("Shooting Feeder Bolt Detected (Linear)")]
    ShootingFeederBoltDetected,

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

    [Description("NG Shuttle Carrier Detected (P3)")]
    NgShuttleCarrierDetected,

    [Description("NG Conveyor Position 1 Occupied")]
    NgConveyorPosition1Occupied,

    [Description("NG Conveyor Position 2 Occupied")]
    NgConveyorPosition2Occupied,

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
    // Keep the persisted mapping key; the physical contact is ON in MANUAL.
    [Description("Auto / Manual Selector")]
    AutoMode,

    [Description("Reset Button")]
    ResetButton,
    // Preserve existing database mapping keys; ON means the door is CLOSED.
    [Description("Door 1 Closed")]
    Door1Open,

    [Description("Door 2 Closed")]
    Door2Open,

    [Description("Door 3 Closed")]
    Door3Open,

    [Description("Door 4 Closed")]
    Door4Open,

    [Description("Door 5 Closed")]
    Door5Open,

    [Description("Door 6 Closed")]
    Door6Open,

    [Description("Servo Main Contactor On")]
    ServoMainContactorOn,

    [Description("Air Pressure High")]
    AirPressureHigh,

    [Description("Placement Handler PCB Detected")]
    PcbPlacementPcbDetected,

    [Description("Supply Handler PCB Detected")]
    PcbSupplyPcbDetected,

    [Description("Main Conveyor Entry Carrier Detected")]
    MainConveyorEntryCarrierDetected,

    [Description("Main Conveyor Exit Carrier Detected")]
    MainConveyorExitCarrierDetected,

    [Description("Main Conveyor Auto / Manual")]
    MainConveyorAutoMode,

    [Description("NG Conveyor Auto / Manual")]
    NgConveyorAutoMode,
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

    [Description("Supply Gripper Closed")]
    PcbSupplyGripperClosed,

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

    [Description("Pickup Head Down (Head 1)")]
    PickupHeadDown,

    [Description("Shooting Head Down (Head 2)")]
    ShootingHeadDown,

    [Description("Pickup Head Vacuum Pump (Head 1)")]
    PickupHeadVacuumPump,

    [Description("Bolt Fastening Stopper Up")]
    BoltFasteningStopperUp,

    [Description("Bolt Fastening Backup Plate Up")]
    BoltFasteningBackupPlateUp,

    [Description("Shooting Head Vacuum Pump (Head 2)")]
    ShootingHeadVacuumPump,

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

    [Description("Shooting Feeder Run (Linear)")]
    ShootingFeederRunSignal,

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
