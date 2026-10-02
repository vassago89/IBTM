using System;
using IBTM.Core;

namespace IBTM.Device;

public sealed class MachineOptions : Setting
{
    public UiLanguage Language { get; set; }
    public int TimeoutMilliseconds { get; set; } = 3_000;
    public int MotionPollMilliseconds
    {
        get;
        set
        {
            if (value is < 10 or > 1_000)
                throw new ArgumentOutOfRangeException(nameof(value), "Use a motion refresh interval from 10 to 1000 ms.");
            field = value;
        }
    } = 50;

    public bool UseEmergencyStop { get; set; } = true;
    public bool UseResetButton { get; set; } = true;
    public bool UseDoorInterlock { get; set; } = true;
    public bool UseAirPressureInterlock { get; set; } = true;
}
