namespace IBTM.Device;

public sealed class MachineOptions
{
    public bool UseEmergencyStop { get; set; } = true;
    public bool UseResetButton { get; set; } = true;
    public bool UseDoorInterlock { get; set; } = true;
    public bool UseAirPressureInterlock { get; set; } = true;
}
