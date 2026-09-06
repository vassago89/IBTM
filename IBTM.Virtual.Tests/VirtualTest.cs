using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Virtual;

namespace IBTM.Virtual.Tests;

internal static class VirtualTest
{
    public static VirtualMotionService Motion(
        MotionSettings settings,
        OperationCancellation operations) => new(
        settings,
        xRange: (0, 100),
        yRange: (0, 100),
        zRange: (0, 100),
        horizontalZ: () => 0,
        operationCancellation: operations);

    public static IReadOnlyDictionary<OutputIo, OutputHardware> Outputs(
        params IoHardwareSettings[] settings) =>
        settings
            .SelectMany(section => section.Outputs)
            .ToDictionary();

    public static async Task HomeAsync(VirtualMotionService motion, double speed)
    {
        await motion.HomeAsync(MotionAxis.Z, speed);
        await motion.HomeAsync(MotionAxis.X, speed);
        await motion.HomeAsync(MotionAxis.Y, speed);
    }

    public static async Task WaitForOutputAsync(
        IIoService io,
        OutputIo output,
        bool value)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(2));
        while (io.GetOutput(output) != value)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    public static async Task<bool> WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        var started = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - started >= timeout)
            {
                return false;
            }

            await Task.Delay(10);
        }

        return true;
    }
}
