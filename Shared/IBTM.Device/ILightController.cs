namespace IBTM.Device;

public interface ILightController
{
    void Initialize();
    void SetLevel(int channel, int level);
    void TurnOn(int channel);
    void TurnOff(int channel);
    void TurnOffAll();
}
