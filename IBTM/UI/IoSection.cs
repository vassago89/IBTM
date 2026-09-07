using System.ComponentModel;
using IBTM.Device;

namespace IBTM.UI;

public enum IoSection
{
    [Description("Safety")]
    MachineSafety,

    [Description("Mode & Utility")]
    MachineModeUtility,

    [Description("Interface & Drive")]
    MainConveyorInterfaceDrive,

    [Description("Station 1")]
    MainConveyorStation1,

    [Description("Station 2")]
    MainConveyorStation2,

    [Description("Station 3")]
    MainConveyorStation3,

    [Description("Shared Table")]
    BoltFasteningSharedTable,

    [Description("Pickup Head")]
    BoltFasteningPickupHead,

    [Description("Shooting Head")]
    BoltFasteningShootingHead,

    [Description("Storage")]
    NgConveyorStorage,

    [Description("Operator Eject")]
    NgConveyorOperatorEject,
}

public static class IoSections
{
    public static IoSection? GetIoSection(this InputIo input) => input switch
    {
        InputIo.EmergencyStop1Pressed
            or InputIo.EmergencyStop2Pressed
            or InputIo.Door1Open
            or InputIo.Door2Open
            or InputIo.Door3Open
            or InputIo.Door4Open
            or InputIo.Door5Open
            or InputIo.Door6Open
            or InputIo.AirPressureLow => IoSection.MachineSafety,

        InputIo.ResetButton
            or InputIo.AutoMode
            or InputIo.ServoMainContactorOn => IoSection.MachineModeUtility,

        InputIo.MainConveyorAvailableFromFront2
            or InputIo.MainConveyorReadyFromRear
            or InputIo.MainConveyorEntryCarrierDetected
            or InputIo.MainConveyorExitCarrierDetected =>
            IoSection.MainConveyorInterfaceDrive,

        InputIo.PcbPlacementCarrierPresent
            or InputIo.PcbPlacementStopperUp
            or InputIo.PcbPlacementStopperDown
            or InputIo.PcbPlacementBackupPlateUp
            or InputIo.PcbPlacementBackupPlateDown =>
            IoSection.MainConveyorStation1,

        InputIo.BoltFasteningCarrierPresent
            or InputIo.BoltFasteningStopperUp
            or InputIo.BoltFasteningStopperDown
            or InputIo.BoltFasteningBackupPlateUp
            or InputIo.BoltFasteningBackupPlateDown =>
            IoSection.MainConveyorStation2,

        InputIo.InspectionCarrierPresent
            or InputIo.InspectionStopperUp
            or InputIo.InspectionStopperDown
            or InputIo.InspectionBackupPlateUp
            or InputIo.InspectionBackupPlateDown =>
            IoSection.MainConveyorStation3,

        InputIo.BoltTableDown
            or InputIo.BoltTableUp => IoSection.BoltFasteningSharedTable,

        InputIo.PickupHeadDown
            or InputIo.PickupHeadUp
            or InputIo.PickupHeadVacuumDetected =>
            IoSection.BoltFasteningPickupHead,

        InputIo.ShootingHeadDown
            or InputIo.ShootingHeadUp
            or InputIo.ShootingHeadVacuumDetected
            or InputIo.ShootingTubeBoltDetected
            or InputIo.ShootingEscapeForward
            or InputIo.ShootingEscapeBackward =>
            IoSection.BoltFasteningShootingHead,

        InputIo.NgConveyorPosition1Occupied
            or InputIo.NgConveyorPosition2Occupied
            or InputIo.NgConveyorPosition3Occupied
            or InputIo.NgConveyorStopperUp
            or InputIo.NgConveyorStopperDown => IoSection.NgConveyorStorage,

        InputIo.NgCarrierEjectButton
            or InputIo.NgCarrierEjectCompleteButton =>
            IoSection.NgConveyorOperatorEject,

        _ => null,
    };

    public static IoSection? GetIoSection(this OutputIo output) => output switch
    {
        OutputIo.TowerLampGreen
            or OutputIo.TowerLampYellow
            or OutputIo.TowerLampRed
            or OutputIo.Buzzer
            or OutputIo.MachineLight => IoSection.MachineModeUtility,

        OutputIo.MainConveyorReadyToFront2
            or OutputIo.MainConveyorAvailableToRear
            or OutputIo.MainConveyorRun
            or OutputIo.MainConveyorReverse
            or OutputIo.MainConveyorNormalSpeed =>
            IoSection.MainConveyorInterfaceDrive,

        OutputIo.PcbPlacementStopperUp
            or OutputIo.PcbPlacementBackupPlateUp =>
            IoSection.MainConveyorStation1,

        OutputIo.BoltFasteningStopperUp
            or OutputIo.BoltFasteningBackupPlateUp =>
            IoSection.MainConveyorStation2,

        OutputIo.InspectionStopperUp
            or OutputIo.InspectionBackupPlateUp =>
            IoSection.MainConveyorStation3,

        OutputIo.BoltTableDown => IoSection.BoltFasteningSharedTable,

        OutputIo.PickupHeadDown
            or OutputIo.PickupHeadVacuumPump =>
            IoSection.BoltFasteningPickupHead,

        OutputIo.ShootingHeadDown
            or OutputIo.ShootingHeadVacuumPump
            or OutputIo.ShootingEscapeForward
            or OutputIo.ShootBolt => IoSection.BoltFasteningShootingHead,

        OutputIo.NgConveyorStopperUp
            or OutputIo.NgConveyorRun
            or OutputIo.NgConveyorReverse
            or OutputIo.NgConveyorNormalSpeed => IoSection.NgConveyorStorage,

        OutputIo.NgCarrierEjectLamp
            or OutputIo.NgCarrierEjectCompleteLamp =>
            IoSection.NgConveyorOperatorEject,

        _ => null,
    };
}
