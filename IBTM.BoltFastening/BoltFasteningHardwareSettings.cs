using IBTM.Core;
using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningHardwareSettings : MotionHardwareSettings
{
    public override HardwareArea Area => HardwareArea.BoltFastening;

    public override IoSection? GetSection(System.Enum signal) => signal switch
    {
        InputIo.PickupHeadDown or InputIo.PickupHeadUp or InputIo.PickupHeadVacuumDetected
            or OutputIo.PickupHeadDown or OutputIo.PickupHeadVacuumPump =>
            IoSection.BoltFasteningPickupHead,
        InputIo.ShootingHeadDown or InputIo.ShootingHeadUp or InputIo.ShootingHeadVacuumDetected
            or InputIo.ShootingTubeBoltDetected or InputIo.ShootingEscapeForward
            or InputIo.ShootingEscapeBackward or OutputIo.ShootingHeadDown
            or OutputIo.ShootingHeadVacuumPump or OutputIo.ShootingEscapeForward
            or OutputIo.ShootBolt => IoSection.BoltFasteningShootingHead,
        _ => null,
    };

    public BoltFasteningHardwareSettings() : base(
        MotionGroup.BoltFastening,
        (MotionAxis.X, MachineAxis.BoltFasteningX, 6, 200),
        (MotionAxis.Y, MachineAxis.BoltFasteningY, 7, 200),
        (MotionAxis.Z, MachineAxis.BoltFasteningZ, 8, 200))
    {
        Inputs = new()
        {
            [InputIo.PickupHeadDown] = 42,
            [InputIo.PickupHeadUp] = 43,
            [InputIo.ShootingHeadDown] = 44,
            [InputIo.ShootingHeadUp] = 45,
            [InputIo.PickupHeadVacuumDetected] = 46,
            [InputIo.ShootingHeadVacuumDetected] = 47,
            [InputIo.ShootingTubeBoltDetected] = 49,
            [InputIo.ShootingEscapeForward] = 50,
            [InputIo.ShootingEscapeBackward] = 51,
        };
        Outputs = new()
        {
            [OutputIo.PickupHeadDown] = Output(
                39,
                40,
                InputIo.PickupHeadDown,
                InputIo.PickupHeadUp),
            [OutputIo.ShootingHeadDown] = Output(
                41,
                42,
                InputIo.ShootingHeadDown,
                InputIo.ShootingHeadUp),
            [OutputIo.PickupHeadVacuumPump] = Output(43),
            [OutputIo.ShootingHeadVacuumPump] = Output(44),
            [OutputIo.ShootingEscapeForward] = Output(
                46,
                InputIo.ShootingEscapeForward,
                InputIo.ShootingEscapeBackward),
            [OutputIo.ShootBolt] = Output(47),
        };
    }

}
