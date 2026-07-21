namespace IBTM.Core.Machine;

public interface IStation
{
    void Initialize();
    void Stop();
    void EmergencyStop();
}
