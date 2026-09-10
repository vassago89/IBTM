using CommunityToolkit.Mvvm.Input;
using IBTM.Device;
using IBTM.Virtual;

namespace IBTM.UI;

public sealed partial class InputControlRow
{
    private readonly VirtualIoService? _virtualIo;

    public InputControlRow(IoInputStatus io, VirtualIoService? virtualIo)
    {
        Io = io;
        _virtualIo = virtualIo;
    }

    public IoInputStatus Io { get; }
    public bool IsVirtual => _virtualIo is not null;

    [RelayCommand(CanExecute = nameof(IsVirtual))]
    private void Toggle() => _virtualIo?.SetInput(Io.Signal, Io.IsOn != true);
}
