using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Device;

namespace IBTM.UI;

public sealed partial class TeachingOutputRow(
    IoOutputStatus io,
    TeachingOutput? output,
    MachineController machine) : ObservableObject
{
    public IoOutputStatus Io { get; } = io;
    public TeachingOutput? Output { get; } = output;
    internal CancellationToken ViewCancellation { get; set; }

    private bool CanToggleOutput()
    {
        return !ViewCancellation.IsCancellationRequested
            && Output is not null
            && machine.CanSetTeachingOutput(Output, live: false);
    }

    [RelayCommand(CanExecute = nameof(CanToggleOutput))]
    private Task ToggleOutputAsync(CancellationToken cancellationToken)
    {
        if (Output is null)
            return Task.CompletedTask;
        return machine.ToggleTeachingOutputAsync(Output, cancellationToken, ViewCancellation);
    }
}
