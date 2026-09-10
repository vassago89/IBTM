namespace IBTM.Device;

public sealed class MachineHardwareSettings : IoHardwareSettings
{
    public override HardwareArea Area
    {
        get
        {
            return HardwareArea.Machine;
        }
    }

    public override IoSection? GetSection(System.Enum signal)
    {
        return signal switch
        {
            InputIo.EmergencyStop1Pressed
                or InputIo.EmergencyStop2Pressed
                or InputIo.Door1Open
                or InputIo.Door2Open
                or InputIo.Door3Open
                or InputIo.Door4Open
                or InputIo.Door5Open
                or InputIo.Door6Open
                or InputIo.AirPressureHigh

                => IoSection.MachineSafety,
            InputIo.ResetButton
                or InputIo.AutoMode
                or InputIo.ServoMainContactorOn
                or OutputIo.TowerLampGreen
                or OutputIo.TowerLampYellow
                or OutputIo.TowerLampRed
                or OutputIo.Buzzer
                or OutputIo.MachineLight

                => IoSection.MachineModeUtility,
            _ => null,
        };
    }

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
            [OutputIo.TowerLampRed] = Output(0),
            [OutputIo.TowerLampYellow] = Output(1),
            [OutputIo.TowerLampGreen] = Output(2),
            [OutputIo.Buzzer] = Output(3),
            [OutputIo.MachineLight] = Output(4),
        };
    }
}
