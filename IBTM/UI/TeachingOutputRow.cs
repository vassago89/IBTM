using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using IBTM.Device;

namespace IBTM.UI;

public sealed class TeachingOutputRow
{
    private readonly MachineController _machine;

    public TeachingOutputRow(
        IoOutputStatus io,
        MachineController machine)
    {
        ToggleOutputCommand = new AsyncRelayCommand(ToggleOutputAsync, () => IsToggleOutputAllowed);

        _machine = machine;
        Io = io;
    }

    public IoOutputStatus Io { get; }
    public bool IsSupported => MachineController.IsTeachingOutputSupported(Io.Signal);
    internal CancellationToken ViewCancellation { get; set; }

    private bool IsToggleOutputAllowed
    {
        get
        {
            return !ViewCancellation.IsCancellationRequested
                && _machine.IsSetTeachingOutputAllowed(Io, live: false);
        }
    }

    public IAsyncRelayCommand ToggleOutputCommand { get; }

    private Task ToggleOutputAsync(CancellationToken cancellationToken)
    {
        return _machine.ToggleTeachingOutputAsync(Io, cancellationToken, ViewCancellation);
    }
}
