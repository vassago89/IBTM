using IBTM.Core;

namespace IBTM.Device;

public sealed class MachineOptions : Setting
{
    public int TimeoutMilliseconds { get; set; } = 3_000;
    public bool UseEmergencyStop { get; set; } = true;
    public bool UseResetButton { get; set; } = true;
    public bool UseDoorInterlock { get; set; } = true;
    public bool UseAirPressureInterlock { get; set; } = true;
}
