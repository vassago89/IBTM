using IBTM.Core;
using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningHardwareSettings : MotionHardwareSettings
{
    public override HardwareArea Area
    {
        get
        {
            return HardwareArea.BoltFastening;
        }
    }

    public override IoSection? GetSection(System.Enum signal)
    {
        return signal switch
        {
            InputIo.PickupHeadDown
                or InputIo.PickupHeadUp
                or InputIo.PickupHeadVacuumDetected
                or OutputIo.PickupHeadUp
                or OutputIo.PickupHeadVacuumPump
                => IoSection.BoltFasteningPickupHead,
            InputIo.ShootingHeadDown
                or InputIo.ShootingHeadUp
                or InputIo.ShootingHeadVacuumDetected
                or InputIo.ShootingTubeBoltDetected
                or InputIo.ShootingEscapeForward
                or InputIo.ShootingEscapeBackward
                or OutputIo.ShootingHeadUp
                or OutputIo.ShootingHeadVacuumPump
                or OutputIo.ShootingEscapeForward
                or OutputIo.ShootBolt
                => IoSection.BoltFasteningShootingHead,
            _ => null,
        };
    }

    public BoltFasteningHardwareSettings() : base(
        MotionGroup.BoltFastening,
        (
            MotionAxis.X,
            MachineAxis.BoltFasteningX,
            6),
        (
            MotionAxis.Y,
            MachineAxis.BoltFasteningY,
            7),
        (
            MotionAxis.Z,
            MachineAxis.BoltFasteningZ,
            8))
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
            [OutputIo.PickupHeadUp] = Output(39, 40, InputIo.PickupHeadUp, InputIo.PickupHeadDown),
            [OutputIo.ShootingHeadUp] = Output(
                41,
                42,
                InputIo.ShootingHeadUp,
                InputIo.ShootingHeadDown),
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
