using CommunityToolkit.Mvvm.Input;
using IBTM.Device;
using IBTM.Virtual;

namespace IBTM.UI;

public sealed partial class InputControlRow(IoInputStatus io, VirtualIoService? virtualIo)
{
    public IoInputStatus Io { get; } = io;

    public bool IsVirtual
    {
        get
        {
            return virtualIo is not null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsVirtual))]
    private void Toggle()
    {
        virtualIo?.SetInput(Io.Signal, Io.IsOn != true);
    }
}
