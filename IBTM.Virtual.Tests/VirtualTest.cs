using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Virtual;
using IBTM.Inspection;
using IBTM.Storage;
using Microsoft.EntityFrameworkCore;

namespace IBTM.Virtual.Tests;

internal static class VirtualTest
{
    public static MachineStore OpenMachineStore(string? file = null)
    {
        var store = new MachineStore(
            file ?? Path.Combine(Path.GetTempPath(), $"IBTM-test-{Guid.NewGuid():N}.db"));
        // Schema setup belongs to the test fixture, not the application's startup policy.
        var type = typeof(MachineStore).Assembly.GetType("IBTM.Storage.MachineDb", throwOnError: true)!;
        var options = typeof(MachineStore).GetField(
            "_options",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store);
        using var db = (DbContext)Activator.CreateInstance(type, options)!;
        db.Database.Migrate();
        return store;
    }

    public static PcbLayout TaughtPcbLayout()
    {
        return new()
        {
            Width = 18,
            Height = 26,
            Origins = new()
            {
                [HeatSinkSlot.HeatSink1] = new(),
                [HeatSinkSlot.HeatSink2] = new() { X = 18 },
            },
            DataMatrix = new(9, 11, 4, 4),
        };
    }

    public static VirtualMotionService Motion(MotionSettings settings, OperationCancellation operations)
    {
        return new(
            settings,
            xRange: (0, 100),
            yRange: (0, 100),
            zRange: (0, 100),
            horizontalZ: () => 0,
            operationCancellation: operations);
    }

    public static IReadOnlyDictionary<OutputIo, OutputHardware> Outputs(
        params IoHardwareSettings[] settings)
    {
        return settings.SelectMany(section => section.Outputs).ToDictionary();
    }

    public static async Task HomeAsync(VirtualMotionService motion, double speed)
    {
        await motion.HomeAsync(MotionAxis.Z, speed);
        await motion.HomeAsync(MotionAxis.X, speed);
        await motion.HomeAsync(MotionAxis.Y, speed);
    }

    public static async Task WaitForOutputAsync(IIoService io, OutputIo output, bool value)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (io.GetOutput(output) != value)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
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
