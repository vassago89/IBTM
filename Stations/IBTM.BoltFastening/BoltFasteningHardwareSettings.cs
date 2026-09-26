using System.Text.Json.Serialization;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningHardwareSettings : MotionHardwareSettings, IJsonOnDeserialized
{
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
            [InputIo.PickupTableDown] = 40,
            [InputIo.PickupTableUp] = 41,
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
            [OutputIo.PickupTableDown] = CreateOutput(37, 38, InputIo.PickupTableDown, InputIo.PickupTableUp),
            [OutputIo.PickupHeadDown] = CreateOutput(39, 40, InputIo.PickupHeadDown, InputIo.PickupHeadUp),
            [OutputIo.ShootingHeadDown] = CreateOutput(
                41,
                42,
                InputIo.ShootingHeadDown,
                InputIo.ShootingHeadUp),
            [OutputIo.PickupHeadVacuumPump] = CreateOutput(43),
            [OutputIo.ShootingHeadVacuumPump] = CreateOutput(44),
            [OutputIo.ShootingEscapeForward] = CreateOutput(
                46,
                InputIo.ShootingEscapeForward,
                InputIo.ShootingEscapeBackward),
            [OutputIo.ShootBolt] = CreateOutput(47),
        };
    }

    public override HardwareArea Area => HardwareArea.BoltFastening;

    void IJsonOnDeserialized.OnDeserialized()
    {
        Inputs.TryAdd(InputIo.PickupTableDown, 40);
        Inputs.TryAdd(InputIo.PickupTableUp, 41);
        Outputs.TryAdd(
            OutputIo.PickupTableDown,
            CreateOutput(37, 38, InputIo.PickupTableDown, InputIo.PickupTableUp));
    }

    public override IoSection? GetSection(System.Enum signal)
    {
        switch (signal)
        {
            case InputIo.PickupTableDown or InputIo.PickupTableUp or OutputIo.PickupTableDown
                or InputIo.PickupHeadDown or InputIo.PickupHeadUp or InputIo.PickupHeadVacuumDetected
                or OutputIo.PickupHeadDown or OutputIo.PickupHeadVacuumPump:
                return IoSection.BoltFasteningPickupHead;
            case InputIo.ShootingHeadDown or InputIo.ShootingHeadUp or InputIo.ShootingHeadVacuumDetected
                or InputIo.ShootingTubeBoltDetected or InputIo.ShootingEscapeForward or InputIo.ShootingEscapeBackward
                or OutputIo.ShootingHeadDown or OutputIo.ShootingHeadVacuumPump
                or OutputIo.ShootingEscapeForward or OutputIo.ShootBolt:
                return IoSection.BoltFasteningShootingHead;
            default:
                return null;
        }
    }
}
