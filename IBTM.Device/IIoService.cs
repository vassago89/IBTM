using System.Threading;
using System.Threading.Tasks;

namespace IBTM.Device;

public interface IIoService
{
    void Initialize();
    bool GetInput(int channel);
    bool GetOutput(int channel);
    Task WaitForInputAsync(
        int channel,
        bool value,
        CancellationToken cancellationToken = default);
    void SetOutput(int channel, bool value);
    void TurnOffAll();
}
