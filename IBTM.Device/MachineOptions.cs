namespace IBTM.Device;

public sealed class MachineOptions
{
    public bool UseEmergencyStop { get; set; }
    public bool UseResetButton { get; set; }
    public bool UseDoorInterlock { get; set; }
    public bool UseAirPressureInterlock { get; set; }
    public bool UseTowerLamp { get; set; }
    public bool UseBuzzer { get; set; }
}
