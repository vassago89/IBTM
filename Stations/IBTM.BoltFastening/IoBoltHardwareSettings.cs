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

    public override HardwareArea Area
    {
        get
        {
            return HardwareArea.BoltFastening;
        }
    }

    public override IoSection? GetSection(Enum signal)
    {
        return signal switch
        {
            InputIo.PickupBoltReady or InputIo.PickupBoltAlarm or InputIo.PickupBoltFasten
                or OutputIo.PickupBoltPreset1 or OutputIo.PickupBoltPreset2 or OutputIo.PickupBoltPreset3
                or OutputIo.PickupBoltStart or OutputIo.PickupBoltDirection
                or OutputIo.PickupBoltLock or OutputIo.PickupBoltReset
                => IoSection.BoltPickupController,
            InputIo.ShootingBoltReady or InputIo.ShootingBoltAlarm or InputIo.ShootingBoltFasten
                or OutputIo.ShootingBoltPreset1 or OutputIo.ShootingBoltPreset2 or OutputIo.ShootingBoltPreset3
                or OutputIo.ShootingBoltStart or OutputIo.ShootingBoltDirection
                or OutputIo.ShootingBoltLock or OutputIo.ShootingBoltReset
                => IoSection.BoltShootingController,
            _ => null,
        };
    }
}
