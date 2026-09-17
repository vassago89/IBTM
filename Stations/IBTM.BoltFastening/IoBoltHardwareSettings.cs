using System;
using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class IoBoltHardwareSettings : IoHardwareSettings
{
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
            [OutputIo.PickupBoltPreset1] = Output(80),
            [OutputIo.PickupBoltPreset2] = Output(81),
            [OutputIo.PickupBoltPreset3] = Output(82),
            [OutputIo.PickupBoltStart] = Output(83),
            [OutputIo.PickupBoltDirection] = Output(84),
            [OutputIo.PickupBoltLock] = Output(85),
            [OutputIo.PickupBoltReset] = Output(86),
            [OutputIo.ShootingBoltPreset1] = Output(87),
            [OutputIo.ShootingBoltPreset2] = Output(88),
            [OutputIo.ShootingBoltPreset3] = Output(89),
            [OutputIo.ShootingBoltStart] = Output(90),
            [OutputIo.ShootingBoltDirection] = Output(91),
            [OutputIo.ShootingBoltLock] = Output(92),
            [OutputIo.ShootingBoltReset] = Output(93),
        };
    }
}
