using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningHardwareSettings : MotionHardwareSettings
{
    public override HardwareArea Area => HardwareArea.BoltFastening;

    public BoltFasteningHardwareSettings()
    {
        Inputs = new()
        {
            [InputIo.BoltTableDown] = 40,
            [InputIo.BoltTableUp] = 41,
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
            [OutputIo.BoltTableDown] = Output(
                37,
                38,
                InputIo.BoltTableDown,
                InputIo.BoltTableUp),
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
        Axes = new()
        {
            [MachineAxis.BoltFasteningX] = Axis(6),
            [MachineAxis.BoltFasteningY] = Axis(7),
            [MachineAxis.BoltFasteningZ] = Axis(8),
        };
    }

}
