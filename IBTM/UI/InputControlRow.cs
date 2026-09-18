using CommunityToolkit.Mvvm.Input;
using IBTM.Device;
using IBTM.Virtual;

namespace IBTM.UI;

public sealed class InputControlRow
{
    private readonly VirtualIoService? _virtualIo;

    public InputControlRow(IoInputStatus io, VirtualIoService? virtualIo)
    {
        ToggleCommand = new RelayCommand(Toggle, () => IsVirtual);

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

    public IRelayCommand ToggleCommand { get; }

    private void Toggle()
    {
        _virtualIo?.SetInput(Io.Signal, Io.IsOn != true);
    }
}
