using CommunityToolkit.Mvvm.Input;
using IBTM.Device;
using IBTM.Virtual;

namespace IBTM.UI;

public sealed partial class InputControlRow
{
    private readonly VirtualIoService? _virtualIo;

    public InputControlRow(IoInputStatus io, VirtualIoService? virtualIo)
    {
        _virtualIo = virtualIo;
        Io = io;
    }

    public IoInputStatus Io { get; }

    public bool IsVirtual
    {
        get
        {
            return _virtualIo is not null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsVirtual))]
    private void Toggle()
    {
        _virtualIo?.SetInput(Io.Signal, Io.IsOn != true);
    }
}
