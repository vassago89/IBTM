using System.ComponentModel;
using System.Text.Json.Serialization;

namespace IBTM.Device;

// Persisted IDs: never renumber or reuse; these are not hardware channel numbers.
[JsonConverter(typeof(SignalIdJsonConverter<InputIo>))]
public enum InputIo
{
    [Description("Available From Front 2 (Heat Sink)")]
    MainConveyorAvailableFromFront2 = 0,

    [Description("Unused")]
    [JsonStringEnumMemberName("PcbBufferPcbPresent")]
    Unused1 = 1,

    [Description("PCB Placement Stopper Up")]
    PcbPlacementStopperUp = 2,

    [Description("PCB Placement Stopper Down")]
    PcbPlacementStopperDown = 3,

    [Description("PCB Placement Heat Sink 1 Present")]
    PcbPlacementHeatSink1Present = 4,

    [Description("PCB Placement Backup Plate Up")]
    PcbPlacementBackupPlateUp = 5,

    [Description("PCB Placement Backup Plate Down")]
    PcbPlacementBackupPlateDown = 6,

    [Description("Unused")]
    [JsonStringEnumMemberName("PcbPlacementIpmGripperClosed")]
    Unused7 = 7,

    [Description("Unused")]
    [JsonStringEnumMemberName("PcbPlacementIpmGripperOpen")]
    Unused8 = 8,

    [Description("PCB Placement Heat Sink 2 Present")]
    PcbPlacementHeatSink2Present = 9,

    [Description("Unused (former Station 1 carrier sensor)")]
    PcbPlacementCarrierPresent = 10,

    [Description("Available From Front 1 (PCB)")]
    PcbSupplyAvailableFromFront1 = 11,

    [Description("Supply Handler Unrotated")]
    PcbSupplyUnrotated = 12,

    [Description("Supply Handler Rotated")]
    PcbSupplyRotated = 13,

    [Description("Supply Gripper Closed")]
    PcbSupplyGripperClosed = 14,

    [Description("Supply Gripper Open")]
    PcbSupplyGripperOpen = 15,

    [Description("Supply IPM Fixer Forward")]
    PcbSupplyIpmFixerForward = 16,

    [Description("Unused (former Supply IPM backward sensor)")]
    PcbSupplyIpmFixerBackward = 17,

    [Description("Bolt Fastening Stopper Up")]
    BoltFasteningStopperUp = 18,

    [Description("Bolt Fastening Stopper Down")]
    BoltFasteningStopperDown = 19,

    [Description("Bolt Fastening Heat Sink 1 Present")]
    BoltFasteningHeatSink1Present = 20,

    [Description("Bolt Fastening Backup Plate Up")]
    BoltFasteningBackupPlateUp = 21,

    [Description("Bolt Fastening Backup Plate Down")]
    BoltFasteningBackupPlateDown = 22,

    [Description("Bolt Fastening Heat Sink 2 Present")]
    BoltFasteningHeatSink2Present = 23,

    [Description("Unused (former Station 2 carrier sensor)")]
    BoltFasteningCarrierPresent = 24,

    [Description("Shooting Head Vacuum Detected (Head 2)")]
    ShootingHeadVacuumDetected = 25,

    [Description("Placement Handler Down")]
    PcbPlacementHandlerDown = 26,

    [Description("Placement Handler Up")]
    PcbPlacementHandlerUp = 27,

    [Description("Placement Handler Rotated")]
    PcbPlacementHandlerRotated = 28,

    [Description("Placement Handler Unrotated")]
    PcbPlacementHandlerUnrotated = 29,

    [Description("Placement IPM Down")]
    PcbPlacementIpmDown = 30,

    [Description("Placement IPM Up")]
    PcbPlacementIpmUp = 31,

    [Description("Placement Vacuum Detected")]
    PcbPlacementVacuumDetected = 32,

    [Description("Pickup Head Down (Head 1)")]
    PickupHeadDown = 33,

    [Description("Pickup Head Up (Head 1)")]
    PickupHeadUp = 34,

    [Description("Shooting Head Down (Head 2)")]
    ShootingHeadDown = 35,

    [Description("Shooting Head Up (Head 2)")]
    ShootingHeadUp = 36,

    [Description("Pickup Head Vacuum Detected (Head 1)")]
    PickupHeadVacuumDetected = 37,

    [Description("Shooting Feeder Bolt Detected (Linear)")]
    ShootingFeederBoltDetected = 38,

    [Description("Shooting Tube Bolt Detected")]
    ShootingTubeBoltDetected = 39,

    [Description("Shooting Escape Forward")]
    ShootingEscapeForward = 40,

    [Description("Shooting Escape Backward")]
    ShootingEscapeBackward = 41,

    [Description("Pickup Feeder Bolt Detected")]
    PickupFeederBoltDetected = 42,

    [Description("Inspection Stopper Up")]
    InspectionStopperUp = 43,

    [Description("Inspection Stopper Down")]
    InspectionStopperDown = 44,

    [Description("Inspection Heat Sink 1 Present")]
    InspectionHeatSink1Present = 45,

    [Description("Inspection Backup Plate Up")]
    InspectionBackupPlateUp = 46,

    [Description("Inspection Backup Plate Down")]
    InspectionBackupPlateDown = 47,

    [Description("NG Carrier Gripper Closed")]
    NgCarrierGripperClosed = 48,

    [Description("NG Carrier Gripper Open")]
    NgCarrierGripperOpen = 49,

    [Description("Inspection Heat Sink 2 Present")]
    InspectionHeatSink2Present = 50,

    [Description("Unused (former Station 3 carrier sensor)")]
    InspectionCarrierPresent = 51,

    [Description("NG Carrier Pickup Down")]
    NgCarrierPickupDown = 52,

    [Description("NG Carrier Pickup Up")]
    NgCarrierPickupUp = 53,

    [Description("NG Carrier Detected")]
    NgCarrierDetected = 54,

    [Description("NG Shuttle Down")]
    NgShuttleDown = 55,

    [Description("NG Shuttle Up")]
    NgShuttleUp = 56,

    [Description("NG Shuttle Carrier Detected (P3)")]
    NgShuttleCarrierDetected = 57,

    [Description("NG Conveyor Position 1 Occupied")]
    NgConveyorPosition1Occupied = 58,

    [Description("NG Conveyor Position 2 Occupied")]
    NgConveyorPosition2Occupied = 59,

    [Description("NG Conveyor Stopper Up")]
    NgConveyorStopperUp = 60,

    [Description("NG Conveyor Stopper Down")]
    NgConveyorStopperDown = 61,

    [Description("NG Carrier Eject Button")]
    NgCarrierEjectButton = 62,

    [Description("NG Carrier Eject Complete Button")]
    NgCarrierEjectCompleteButton = 63,

    [Description("Ready From Rear")]
    MainConveyorReadyFromRear = 64,

    [Description("Emergency Stop 1 Pressed")]
    EmergencyStop1Pressed = 65,

    [Description("Emergency Stop 2 Pressed")]
    EmergencyStop2Pressed = 66,
    // Keep the persisted mapping key; the physical contact is ON in MANUAL.
    [Description("Auto / Manual Selector")]
    AutoMode = 67,

    [Description("Reset Button")]
    ResetButton = 68,
    // Preserve existing database mapping keys; ON means the door is CLOSED.
    [Description("Door 1 Closed")]
    Door1Open = 69,

    [Description("Door 2 Closed")]
    Door2Open = 70,

    [Description("Door 3 Closed")]
    Door3Open = 71,

    [Description("Door 4 Closed")]
    Door4Open = 72,

    [Description("Door 5 Closed")]
    Door5Open = 73,

    [Description("Door 6 Closed")]
    Door6Open = 74,

    [Description("Servo Main Contactor On")]
    ServoMainContactorOn = 75,

    [Description("Air Pressure High")]
    AirPressureHigh = 76,

    [Description("Placement Handler PCB Detected")]
    PcbPlacementPcbDetected = 77,

    [Description("Supply Handler PCB Detected")]
    PcbSupplyPcbDetected = 78,

    [Description("Main Conveyor Entry Carrier Detected")]
    MainConveyorEntryCarrierDetected = 79,

    [Description("Unused")]
    [JsonStringEnumMemberName("MainConveyorExitCarrierDetected")]
    Unused80 = 80,

    [Description("Main Conveyor Manual Input")]
    [JsonStringEnumMemberName("MainConveyorAutoMode")]
    MainConveyorManualMode = 81,

    [Description("NG Conveyor Manual Input")]
    [JsonStringEnumMemberName("NgConveyorAutoMode")]
    NgConveyorManualMode = 82,


    [Description("Pickup Table Down (Head 1)")]
    PickupTableDown = 89,

    [Description("Pickup Table Up (Head 1)")]
    PickupTableUp = 90,
}

// Persisted IDs: never renumber or reuse; these are not hardware channel numbers.
[JsonConverter(typeof(SignalIdJsonConverter<OutputIo>))]
public enum OutputIo
{
    [Description("Ready To Front 2 (Heat Sink)")]
    MainConveyorReadyToFront2 = 0,

    [Description("PCB Placement Stopper Up")]
    [JsonStringEnumMemberName("PcbPlacementStopperDown")]
    PcbPlacementStopperUp = 1,

    [Description("PCB Placement Backup Plate Up")]
    [JsonStringEnumMemberName("PcbPlacementBackupPlateDown")]
    PcbPlacementBackupPlateUp = 2,

    [Description("Unused")]
    [JsonStringEnumMemberName("PcbPlacementIpmGripperClose")]
    Unused3 = 3,

    [Description("Ready To Front 1 (PCB)")]
    PcbSupplyReadyToFront1 = 4,

    [Description("Supply Handler Rotate")]
    PcbSupplyRotate = 5,

    [Description("Supply Gripper Closed")]
    PcbSupplyGripperClosed = 6,

    [Description("Supply IPM Fixer Forward")]
    PcbSupplyIpmFixerForward = 7,

    [Description("Placement Handler Down")]
    PcbPlacementHandlerDown = 8,

    [Description("Placement Handler Rotate")]
    PcbPlacementHandlerRotate = 9,

    [Description("Placement IPM Down")]
    PcbPlacementIpmDown = 10,

    [Description("Placement Vacuum Ejector")]
    PcbPlacementVacuumEjector = 11,

    [Description("Pickup Head Down (Head 1)")]
    [JsonStringEnumMemberName("PickupHeadUp")]
    PickupHeadDown = 12,

    [Description("Shooting Head Down (Head 2)")]
    [JsonStringEnumMemberName("ShootingHeadUp")]
    ShootingHeadDown = 13,

    [Description("Pickup Head Vacuum Pump (Head 1)")]
    PickupHeadVacuumPump = 14,

    [Description("Bolt Fastening Stopper Up")]
    [JsonStringEnumMemberName("BoltFasteningStopperDown")]
    BoltFasteningStopperUp = 15,

    [Description("Bolt Fastening Backup Plate Up")]
    [JsonStringEnumMemberName("BoltFasteningBackupPlateDown")]
    BoltFasteningBackupPlateUp = 16,

    [Description("Shooting Head Vacuum Pump (Head 2)")]
    ShootingHeadVacuumPump = 17,

    [Description("Inspection Stopper Up")]
    [JsonStringEnumMemberName("InspectionStopperDown")]
    InspectionStopperUp = 18,

    [Description("Inspection Backup Plate Up")]
    [JsonStringEnumMemberName("InspectionBackupPlateDown")]
    InspectionBackupPlateUp = 19,

    [Description("NG Carrier Pickup Down")]
    [JsonStringEnumMemberName("NgCarrierPickupUp")]
    NgCarrierPickupDown = 20,

    [Description("NG Carrier Gripper Close")]
    [JsonStringEnumMemberName("NgCarrierGripperOpen")]
    NgCarrierGripperClose = 21,

    [Description("NG Shuttle Down")]
    [JsonStringEnumMemberName("NgShuttleUp")]
    NgShuttleDown = 22,

    [Description("Shooting Feeder OFF (Linear)")]
    [JsonStringEnumMemberName("ShootingFeederRunSignal")]
    ShootingFeederOff = 23,

    [Description("Shooting Escape Forward")]
    ShootingEscapeForward = 24,

    [Description("Shoot Bolt")]
    ShootBolt = 25,

    [Description("Main Conveyor Run")]
    MainConveyorRun = 26,

    [Description("Main Conveyor Forward")]
    MainConveyorForward = 27,

    [Description("NG Conveyor Stopper Up")]
    [JsonStringEnumMemberName("NgConveyorStopperDown")]
    NgConveyorStopperUp = 28,

    [Description("NG Conveyor Run")]
    NgConveyorRun = 29,

    [Description("NG Conveyor Reverse")]
    NgConveyorReverse = 30,

    [Description("NG Carrier Eject Lamp")]
    NgCarrierEjectLamp = 31,

    [Description("NG Carrier Eject Complete Lamp")]
    NgCarrierEjectCompleteLamp = 32,

    [Description("Available To Rear")]
    MainConveyorAvailableToRear = 33,

    [Description("Tower Lamp Green")]
    TowerLampGreen = 34,

    [Description("Tower Lamp Yellow")]
    TowerLampYellow = 35,

    [Description("Tower Lamp Red")]
    TowerLampRed = 36,

    [Description("Buzzer")]
    Buzzer = 37,

    [Description("Machine Light")]
    MachineLight = 38,

    [Description("Pickup Controller Preset 1 (Head 1)")]
    PickupBoltPreset1 = 39,
    [Description("Pickup Controller Preset 2 (Head 1)")]
    PickupBoltPreset2 = 40,
    [Description("Pickup Controller Preset 3 (Head 1)")]
    PickupBoltPreset3 = 41,
    [Description("Pickup Controller Start (Head 1)")]
    PickupBoltStart = 42,
    [Description("Pickup Controller FWD/BWD (Head 1)")]
    PickupBoltDirection = 43,
    [Description("Pickup Controller Lock (Head 1)")]
    PickupBoltLock = 44,
    [Description("Pickup Controller Reset (Head 1)")]
    PickupBoltReset = 45,
    [Description("Shooting Controller Preset 1 (Head 2)")]
    ShootingBoltPreset1 = 46,
    [Description("Shooting Controller Preset 2 (Head 2)")]
    ShootingBoltPreset2 = 47,
    [Description("Shooting Controller Preset 3 (Head 2)")]
    ShootingBoltPreset3 = 48,
    [Description("Shooting Controller Start (Head 2)")]
    ShootingBoltStart = 49,
    [Description("Shooting Controller FWD/BWD (Head 2)")]
    ShootingBoltDirection = 50,
    [Description("Shooting Controller Lock (Head 2)")]
    ShootingBoltLock = 51,
    [Description("Shooting Controller Reset (Head 2)")]
    ShootingBoltReset = 52,

    [Description("Main Conveyor Normal Speed")]
    MainConveyorNormalSpeed = 53,

    [Description("NG Conveyor Normal Speed")]
    NgConveyorNormalSpeed = 54,

    [Description("Pickup Table Down (Head 1)")]
    PickupTableDown = 55,
}

// Persisted IDs: never renumber or reuse; these are not hardware channel numbers.
[JsonConverter(typeof(SignalIdJsonConverter<MachineAxis>))]
public enum MachineAxis
{
    [Description("PCB Supply Handler X")]
    PcbSupplyX = 0,

    [Description("PCB Supply Handler Y")]
    PcbSupplyY = 1,

    [Description("PCB Supply Handler Z")]
    PcbSupplyZ = 2,

    [Description("PCB Placement Handler X")]
    PcbPlacementHandlerX = 3,

    [Description("PCB Placement Handler Y")]
    PcbPlacementHandlerY = 4,

    [Description("PCB Placement Handler Z")]
    PcbPlacementHandlerZ = 5,

    [Description("Bolt Fastening X")]
    BoltFasteningX = 6,

    [Description("Bolt Fastening Y")]
    BoltFasteningY = 7,

    [Description("Bolt Fastening Z")]
    BoltFasteningZ = 8,

    [Description("Inspection Gantry X")]
    InspectionGantryX = 9,

    [Description("Inspection Gantry Y")]
    InspectionGantryY = 10,
}
