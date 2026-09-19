namespace IBTM.Device;

public sealed class MachineHardwareSettings : IoHardwareSettings
{
    public MachineHardwareSettings()
    {
        Inputs = new()
        {
            [InputIo.EmergencyStop1Pressed] = 0,
            [InputIo.EmergencyStop2Pressed] = 1,
            [InputIo.ResetButton] = 2,
            [InputIo.AutoMode] = 3,
            [InputIo.Door1Open] = 4,
            [InputIo.Door2Open] = 5,
            [InputIo.Door3Open] = 6,
            [InputIo.Door4Open] = 7,
            [InputIo.Door5Open] = 8,
            [InputIo.Door6Open] = 9,
            [InputIo.ServoMainContactorOn] = 10,
            [InputIo.AirPressureHigh] = 15,
        };
        Outputs = new()
        {
            [OutputIo.TowerLampRed] = CreateOutput(0),
            [OutputIo.TowerLampYellow] = CreateOutput(1),
            [OutputIo.TowerLampGreen] = CreateOutput(2),
            [OutputIo.Buzzer] = CreateOutput(3),
            [OutputIo.MachineLight] = CreateOutput(4),
        };
    }

    public override HardwareArea Area => HardwareArea.Machine;

    public override IoSection? GetSection(System.Enum signal)
    {
        switch (signal)
        {
            case InputIo.EmergencyStop1Pressed:
            case InputIo.EmergencyStop2Pressed:
            case InputIo.Door1Open:
            case InputIo.Door2Open:
            case InputIo.Door3Open:
            case InputIo.Door4Open:
            case InputIo.Door5Open:
            case InputIo.Door6Open:
            case InputIo.AirPressureHigh:
                return IoSection.MachineSafety;
            case InputIo.ResetButton:
            case InputIo.AutoMode:
            case InputIo.ServoMainContactorOn:
            case OutputIo.TowerLampGreen:
            case OutputIo.TowerLampYellow:
            case OutputIo.TowerLampRed:
            case OutputIo.Buzzer:
            case OutputIo.MachineLight:
                return IoSection.MachineModeUtility;
            default:
                return null;
        }
    }
}
