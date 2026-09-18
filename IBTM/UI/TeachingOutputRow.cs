using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Device;

namespace IBTM.UI;

public sealed partial class TeachingOutputRow : ObservableObject
{
    private readonly MachineController _machine;

    public TeachingOutputRow(
        IoOutputStatus io,
        TeachingOutput? output,
        MachineController machine)
    {
        _machine = machine;
        Io = io;
        Output = output;
    }

    public IoOutputStatus Io { get; }
    public TeachingOutput? Output { get; }
    internal CancellationToken ViewCancellation { get; set; }

    private bool CanToggleOutput()
    {
        return !ViewCancellation.IsCancellationRequested
            && Output is not null
            && _machine.CanSetTeachingOutput(Output, live: false);
    }

    [RelayCommand(CanExecute = nameof(CanToggleOutput))]
    private Task ToggleOutputAsync(CancellationToken cancellationToken)
    {
        if (Output is null)
            return Task.CompletedTask;
        return _machine.ToggleTeachingOutputAsync(Output, cancellationToken, ViewCancellation);
    }
}
