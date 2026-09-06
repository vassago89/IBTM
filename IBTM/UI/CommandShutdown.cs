using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;

namespace IBTM.UI;

internal static class CommandShutdown
{
    public static async Task StopAsync(
        Action stop,
        params IAsyncRelayCommand[] commands)
    {
        var pending = Capture(commands);
        try
        {
            stop();
        }
        finally
        {
            await WaitAsync(pending);
        }
    }

    public static Task[] Capture(params IAsyncRelayCommand[] commands) =>
        commands.Select(command => command.ExecutionTask)
            .OfType<Task>()
            .Where(task => !task.IsCompleted)
            .ToArray();

    public static async Task WaitAsync(params Task[] tasks)
    {
        var completion = Task.WhenAll(tasks);
        try
        {
            await completion;
        }
        catch (OperationCanceledException) when (completion.IsCanceled)
        {
        }
        catch (OperationCanceledException)
        {
            throw completion.Exception!;
        }
    }
}
