using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class IoBoltHardwareSettings : IoHardwareSettings
{
    public IoBoltHardwareSettings()
    {
        Inputs = [];
        // Final 260913 map. Keep the existing head assignment: 1=pickup, 2=shooting.
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

    // Ignore retired input mappings in existing settings; preserve the output addresses.
    [JsonIgnore]
    public override Dictionary<InputIo, int> Inputs { get; set; }

    [JsonIgnore]
    public override HardwareArea Area => HardwareArea.BoltFastening;

    public override IoSection? GetSection(Enum signal)
    {
        switch (signal)
        {
            case OutputIo.PickupBoltPreset1 or OutputIo.PickupBoltPreset2 or OutputIo.PickupBoltPreset3
                or OutputIo.PickupBoltStart or OutputIo.PickupBoltDirection
                or OutputIo.PickupBoltLock or OutputIo.PickupBoltReset:
                return IoSection.BoltPickupController;
            case OutputIo.ShootingBoltPreset1 or OutputIo.ShootingBoltPreset2 or OutputIo.ShootingBoltPreset3
                or OutputIo.ShootingBoltStart or OutputIo.ShootingBoltDirection
                or OutputIo.ShootingBoltLock or OutputIo.ShootingBoltReset:
                return IoSection.BoltShootingController;
            default:
                return null;
        }
    }
}
