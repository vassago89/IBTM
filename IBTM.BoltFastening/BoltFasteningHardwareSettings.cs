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
            [InputIo.BoltHead1Down] = 42,
            [InputIo.BoltHead1Up] = 43,
            [InputIo.BoltHead2Down] = 44,
            [InputIo.BoltHead2Up] = 45,
            [InputIo.BoltHead1VacuumDetected] = 46,
            [InputIo.BoltHead2VacuumDetected] = 47,
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
            [OutputIo.BoltHead1Down] = Output(
                39,
                40,
                InputIo.BoltHead1Down,
                InputIo.BoltHead1Up),
            [OutputIo.BoltHead2Down] = Output(
                41,
                42,
                InputIo.BoltHead2Down,
                InputIo.BoltHead2Up),
            [OutputIo.BoltHead1VacuumPump] = Output(43),
            [OutputIo.BoltHead2VacuumPump] = Output(44),
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
