using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Device;
using IBTM.Hantas;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Storage;

namespace IBTM.Virtual.Tests;

internal static class VirtualTestSupport
{
    public static Guid BoltId(int number, HeatSinkSlot pcb = HeatSinkSlot.HeatSink1)
    {
        return new Guid(number, (short)pcb, 0, 0x49, 0x42, 0x54, 0x4d, 0x54, 0x45, 0x53, 0x54);
    }

    public static IBTM.UI.TeachingPoint CreateTeachingPoint(
        TeachingPosition definition, MachineSettings settings, Recipe? recipe = null)
    {
        var recipes = new RecipeManager(OpenMachineStore(), new());
        recipes.Current.CopyFrom(recipe ?? new());
        return new(definition, settings, recipes, definition.Bolt?.HeatSink ?? HeatSinkSlot.HeatSink1);
    }

    public static IBTM.UI.RecipeImageItem? RecordedImage(IBTM.UI.TeachingViewModel teaching)
    {
        return teaching.CarrierImages.SingleOrDefault(image => image.Metadata.HeatSink == teaching.SelectedPcb
            && (teaching.SelectedBarcode is not null ? image.Metadata.IsBarcode
                : !image.Metadata.IsBarcode
                    && image.Metadata.BoltId == teaching.SelectedPoint?.Position.Bolt?.Id));
    }

    public static AdcBoltHead CreateAdcHead(
        IAdcBus bus, VirtualIoService io, FasteningHead head, HantasSettings settings,
        byte slave, string port, int baud)
    {
        if (bus is VirtualAdcBus virtualBus)
            virtualBus.BindIo(io, head, slave);
        else if (bus is AdcControllerStub stub)
            stub.BindIo(io, head);
        return new(bus, io, head, settings, slave, port, baud);
    }

    public static PcbSupplier CreateSupplier(IXyMotion motion, IIoService io, PcbSupplySettings settings)
    {
        return new(motion, new(motion), io, settings, new(OpenMachineStore(), new()), new());
    }

    public static PcbPlacer CreatePlacer(IXyMotion motion, IIoService io, PcbPlacementHandlerSettings settings)
    {
        var units = new UnitSettings();
        return new(motion, new(motion), io, settings, new UnavailableSupply(),
            ConveyorStation.CreatePcbPlacement(io), new(OpenMachineStore(), new()), units);
    }

    public static BoltFasteningStation CreateFastening(
        IBoltHead shooting, IBoltHead pickup, IIoService io, IXyMotion motion,
        BoltFasteningSettings settings, CarrierReferenceSettings reference)
    {
        var units = new UnitSettings();
        return new(shooting, pickup, io, motion, new(motion), settings, reference,
            ConveyorStation.CreateBoltFastening(io),
            new(OpenMachineStore(), new()), units, new(io, new(), units));
    }

    public static InspectionStation CreateNgTransfer(
        IIoService io, IXyMotion? motion = null, OperationCancellation? operations = null,
        InspectionGantrySettings? motionSettings = null, NgCarrierTransferSettings? settings = null,
        UnitSettings? units = null)
    {
        operations ??= new();
        motionSettings ??= new();
        motion ??= new VirtualMotionService(motionSettings.Motion, operations, hasZ: false);
        settings ??= new();
        units ??= new();
        var work = ConveyorStation.CreateInspection(io);
        var conveyor = new NgCarrierConveyor(io, new(), units);
        return new(work, motion, new(motion), conveyor, motionSettings, settings, io, units,
            new VirtualCamera(() => motion.Position, () => []), new VirtualLightController(), new(),
            new RecipeManager(OpenMachineStore(), new()));
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

    public static IReadOnlyDictionary<OutputIo, OutputHardware> Outputs(
        params IoHardwareSettings[] settings)
    {
        var outputs = settings.SelectMany(section => section.Outputs).ToDictionary();
        foreach (var (signal, hardware) in new IoBoltHardwareSettings().Outputs)
            outputs.TryAdd(signal, hardware);
        return outputs;
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

    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    public static async Task RunOnStaAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public sealed class PausedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<Action> _pending;
        private int _released;

        public PausedSynchronizationContext()
        {
            _pending = new();
        }

        public bool HasPending => !_pending.IsEmpty;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _pending.Enqueue(() => callback(state));
            if (Volatile.Read(ref _released) != 0)
                Drain();
        }

        public void Release()
        {
            Volatile.Write(ref _released, 1);
            Drain();
        }

        private void Drain()
        {
            while (_pending.TryDequeue(out var callback))
                ThreadPool.QueueUserWorkItem(_ => callback());
        }
    }
}
