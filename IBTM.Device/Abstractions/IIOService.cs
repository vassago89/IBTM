namespace IBTM.Device.Abstractions;

public interface IIOService
{
    void Initialize();
    void SetOutput(int channel, bool value);
    void TurnOffAll();
}
