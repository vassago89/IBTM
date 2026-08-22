namespace IBTM.Device;

public sealed class MachineHardwareSettings : IoHardwareSettings
{
    public override HardwareArea Area => HardwareArea.Machine;

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
            [InputIo.AirPressureLow] = 15,
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
