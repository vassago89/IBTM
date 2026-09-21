using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Virtual;
using IBTM.Inspection;
using IBTM.Storage;
using IBTM.PcbSupply;
using IBTM.PcbPlacement;
using IBTM.BoltFastening;
using IBTM.BoltFeeder;
using IBTM.NgConveyor;

namespace IBTM.Virtual.Tests;

internal static class VirtualTest
{
    public static PcbSupplier CreateSupplier(IXyMotion motion, IIoService io, PcbSupplySettings settings)
    {
        return new(motion, io, settings, new());
    }

    public static PcbPlacer CreatePlacer(IXyMotion motion, IIoService io, PcbPlacementHandlerSettings settings)
    {
        var units = new UnitSettings();
        return new(motion, io, settings, new UnavailableSupply(),
            new(ConveyorStation.CreatePcbPlacement(io), units), new(OpenMachineStore(), new()), units);
    }

    public static BoltFasteningStation CreateFastening(
        IBoltHead shooting, IBoltHead pickup, IIoService io, IXyMotion motion,
        BoltFasteningSettings settings, CarrierReferenceSettings reference)
    {
        var units = new UnitSettings();
        return new(shooting, pickup, io, motion, settings, reference,
            new(ConveyorStation.CreateBoltFastening(io), units),
            new(OpenMachineStore(), new()), units);
    }

    public static NgCarrierTransfer CreateNgTransfer(
        IIoService io, IXyMotion? motion = null, OperationCancellation? operations = null,
        InspectionGantrySettings? motionSettings = null, NgCarrierTransferSettings? settings = null,
        UnitSettings? units = null)
    {
        operations ??= new();
        motionSettings ??= new();
        motion ??= new VirtualMotionService(motionSettings.Motion, operations, hasZ: false);
        return new(io, motion, operations, motionSettings, settings ?? new(), units ?? new());
    }

    private sealed class UnavailableSupply : IPcbSupplyHandoff
    {
        public event Action? Changed { add { } remove { } }
        public bool PcbSecured => false;
        public PcbSupplyHandoff Handoff => PcbSupplyHandoff.Unavailable;
    }

    public static MachineStore OpenMachineStore(string? file = null)
    {
        return new MachineStore(
            file ?? Path.Combine(Path.GetTempPath(), $"IBTM-test-{Guid.NewGuid():N}.db"));
    }

    public static LogEntry[] Snapshot(this ApplicationLog log)
    {
        lock (log.SyncRoot)
        {
            return log.Entries.ToArray();
        }
    }

    public static VirtualMotionService Motion(MotionSettings settings, OperationCancellation operations)
    {
        return new(
            settings,
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

    // Test material setup: an arriving carrier has at least one heat sink.
    public static void SetCarrier(VirtualIoService io, InputIo heatSink1, bool present)
    {
        var heatSink2 = heatSink1 switch
        {
            InputIo.PcbPlacementHeatSink1Present => InputIo.PcbPlacementHeatSink2Present,
            InputIo.BoltFasteningHeatSink1Present => InputIo.BoltFasteningHeatSink2Present,
            InputIo.InspectionHeatSink1Present => InputIo.InspectionHeatSink2Present,
            _ => throw new ArgumentOutOfRangeException(nameof(heatSink1)),
        };
        if (!present)
            io.SetInputs((heatSink1, false), (heatSink2, false));
        else if (!io.GetInput(heatSink1) && !io.GetInput(heatSink2))
            io.SetInput(heatSink1, true);
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
