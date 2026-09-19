using System;
using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class IoBoltHardwareSettings : IoHardwareSettings
{
    public IoBoltHardwareSettings()
    {
        // Final 260913 map. Keep the existing head assignment: 1=pickup, 2=shooting.
        Inputs = new()
        {
            [InputIo.PickupBoltReady] = 11,
            [InputIo.PickupBoltAlarm] = 12,
            [InputIo.PickupBoltFasten] = 13,
            [InputIo.ShootingBoltReady] = 93,
            [InputIo.ShootingBoltAlarm] = 94,
            [InputIo.ShootingBoltFasten] = 95,
        };
        Outputs = new()
        {
            [OutputIo.PickupBoltPreset1] = CreateOutput(80),
            [OutputIo.PickupBoltPreset2] = CreateOutput(81),
            [OutputIo.PickupBoltPreset3] = CreateOutput(82),
            [OutputIo.PickupBoltStart] = CreateOutput(83),
            [OutputIo.PickupBoltDirection] = CreateOutput(84),
            [OutputIo.PickupBoltLock] = CreateOutput(85),
            [OutputIo.PickupBoltReset] = CreateOutput(86),
            [OutputIo.ShootingBoltPreset1] = CreateOutput(87),
            [OutputIo.ShootingBoltPreset2] = CreateOutput(88),
            [OutputIo.ShootingBoltPreset3] = CreateOutput(89),
            [OutputIo.ShootingBoltStart] = CreateOutput(90),
            [OutputIo.ShootingBoltDirection] = CreateOutput(91),
            [OutputIo.ShootingBoltLock] = CreateOutput(92),
            [OutputIo.ShootingBoltReset] = CreateOutput(93),
        };
    }

    public int FasteningTimeoutMilliseconds { get; set; } = 15_000;

    public override HardwareArea Area => HardwareArea.BoltFastening;

    public override IoSection? GetSection(Enum signal)
    {
        switch (signal)
        {
            case InputIo.PickupBoltReady:
            case InputIo.PickupBoltAlarm:
            case InputIo.PickupBoltFasten:
            case OutputIo.PickupBoltPreset1:
            case OutputIo.PickupBoltPreset2:
            case OutputIo.PickupBoltPreset3:
            case OutputIo.PickupBoltStart:
            case OutputIo.PickupBoltDirection:
            case OutputIo.PickupBoltLock:
            case OutputIo.PickupBoltReset:
                return IoSection.BoltPickupController;
            case InputIo.ShootingBoltReady:
            case InputIo.ShootingBoltAlarm:
            case InputIo.ShootingBoltFasten:
            case OutputIo.ShootingBoltPreset1:
            case OutputIo.ShootingBoltPreset2:
            case OutputIo.ShootingBoltPreset3:
            case OutputIo.ShootingBoltStart:
            case OutputIo.ShootingBoltDirection:
            case OutputIo.ShootingBoltLock:
            case OutputIo.ShootingBoltReset:
                return IoSection.BoltShootingController;
            default:
                return null;
        }
    }
}
