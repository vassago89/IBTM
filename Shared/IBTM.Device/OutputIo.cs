using System.ComponentModel;
using System.Text.Json.Serialization;

namespace IBTM.Device;

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

    [Description("Pickup Head Down")]
    [JsonStringEnumMemberName("PickupHeadUp")]
    PickupHeadDown = 12,

    [Description("Shooting Head Down")]
    [JsonStringEnumMemberName("ShootingHeadUp")]
    ShootingHeadDown = 13,

    [Description("Pickup Head Vacuum Pump")]
    PickupHeadVacuumPump = 14,

    [Description("Bolt Fastening Stopper Up")]
    [JsonStringEnumMemberName("BoltFasteningStopperDown")]
    BoltFasteningStopperUp = 15,

    [Description("Bolt Fastening Backup Plate Up")]
    [JsonStringEnumMemberName("BoltFasteningBackupPlateDown")]
    BoltFasteningBackupPlateUp = 16,

    [Description("Shooting Head Vacuum Pump")]
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

    [Description("Pickup Controller Preset 1")]
    PickupBoltPreset1 = 39,
    [Description("Pickup Controller Preset 2")]
    PickupBoltPreset2 = 40,
    [Description("Pickup Controller Preset 3")]
    PickupBoltPreset3 = 41,
    [Description("Pickup Controller Start")]
    PickupBoltStart = 42,
    [Description("Pickup Controller FWD/BWD")]
    PickupBoltDirection = 43,
    [Description("Pickup Controller Lock")]
    PickupBoltLock = 44,
    [Description("Pickup Controller Reset")]
    PickupBoltReset = 45,
    [Description("Shooting Controller Preset 1")]
    ShootingBoltPreset1 = 46,
    [Description("Shooting Controller Preset 2")]
    ShootingBoltPreset2 = 47,
    [Description("Shooting Controller Preset 3")]
    ShootingBoltPreset3 = 48,
    [Description("Shooting Controller Start")]
    ShootingBoltStart = 49,
    [Description("Shooting Controller FWD/BWD")]
    ShootingBoltDirection = 50,
    [Description("Shooting Controller Lock")]
    ShootingBoltLock = 51,
    [Description("Shooting Controller Reset")]
    ShootingBoltReset = 52,

    [Description("Main Conveyor Normal Speed")]
    MainConveyorNormalSpeed = 53,

    [Description("NG Conveyor Normal Speed")]
    NgConveyorNormalSpeed = 54,

    [Description("Pickup Table Down")]
    PickupTableDown = 55,
}
