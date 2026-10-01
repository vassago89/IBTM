using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Device;

namespace IBTM.UI;

public sealed class TeachingOutputRow : ObservableObject
{
    private readonly MachineController _machine;

    public TeachingOutputRow(
        IoOutputStatus io,
        MachineController machine)
    {
        ToggleOutputCommand = new AsyncRelayCommand(ToggleOutputAsync);

        _machine = machine;
        Io = io;
    }

    public IoOutputStatus Io { get; }
    public bool IsSupported => MachineController.IsTeachingOutputSupported(Io.Signal);
    internal CancellationToken ViewCancellation { get; set; }

    public bool IsToggleOutputAllowed
    {
        get
        {
            return !ViewCancellation.IsCancellationRequested
                && _machine.IsSetTeachingOutputAllowed(Io);
        }
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsToggleOutputAllowed));
    }

    public IAsyncRelayCommand ToggleOutputCommand { get; }

    private Task ToggleOutputAsync(CancellationToken cancellationToken)
    {
        return _machine.ToggleTeachingOutputAsync(Io, cancellationToken, ViewCancellation);
    }
}
