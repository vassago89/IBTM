using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using IBTM.Core;

namespace IBTM.Device;

public abstract class StationWork
{
    private readonly IIoService _io;
    private readonly InputIo _carrier;
    private readonly InputIo _backupPlateUp;
    private readonly InputIo _stopperDown;
    private readonly InputIo _heatSink1;
    private readonly InputIo _heatSink2;
    private readonly List<PcbAssembly> _assemblies = [];
    private bool _completed;

    protected StationWork(
        IIoService io,
        InputIo carrier,
        InputIo backupPlateUp,
        InputIo stopperDown,
        InputIo heatSink1,
        InputIo heatSink2)
    {
        _io = io;
        _carrier = carrier;
        _backupPlateUp = backupPlateUp;
        _stopperDown = stopperDown;
        _heatSink1 = heatSink1;
        _heatSink2 = heatSink2;
        io.InputChanged += OnInputChanged;
    }

    public event Action? Changed;
    public event Action<bool>? CarrierChanged;

    public bool CarrierPresent => _io.GetInput(_carrier);
    public bool Ready =>
        CarrierPresent
        && _io.GetInput(_backupPlateUp)
        && _io.GetInput(_stopperDown);
    public bool Completed => Volatile.Read(ref _completed);
    public IReadOnlyList<PcbAssembly> Assemblies => _assemblies;
    public virtual bool CanReceive => !CarrierPresent;
    public virtual bool HasNg =>
        _assemblies.Any(assembly =>
            HeatSinkPresent(assembly.HeatSink)
            && assembly.Result == PcbResult.Ng);
    public bool CanTransfer =>
        CarrierPresent
        && Completed
        && _io.GetInput(_stopperDown);

    public bool HeatSinkPresent(HeatSinkSlot heatSink) =>
        _io.GetInput(heatSink == HeatSinkSlot.HeatSink1
            ? _heatSink1
            : _heatSink2);

    public PcbAssembly Assembly(HeatSinkSlot heatSink)
    {
        var assembly = _assemblies.FirstOrDefault(
            item => item.HeatSink == heatSink);
        if (assembly is not null)
        {
            return assembly;
        }

        assembly = new PcbAssembly(heatSink);
        _assemblies.Add(assembly);
        return assembly;
    }

    public void SetAssemblies(IEnumerable<PcbAssembly> assemblies)
    {
        _assemblies.Clear();
        _assemblies.AddRange(assemblies);
        Volatile.Write(ref _completed, false);
        Changed?.Invoke();
    }

    public void Complete()
    {
        if (Completed)
        {
            return;
        }

        Volatile.Write(ref _completed, true);
        Changed?.Invoke();
    }

    protected void Restart()
    {
        Volatile.Write(ref _completed, false);
        Changed?.Invoke();
    }

    protected void NotifyChanged() => Changed?.Invoke();

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input == _carrier)
        {
            if (value)
            {
                Volatile.Write(ref _completed, false);
                _assemblies.Clear();
            }

            CarrierChanged?.Invoke(value);
        }
        else if (input == _heatSink1 || input == _heatSink2)
        {
            Volatile.Write(ref _completed, false);
        }

        if (input == _carrier
            || input == _backupPlateUp
            || input == _stopperDown
            || input == _heatSink1
            || input == _heatSink2)
        {
            Changed?.Invoke();
        }
    }
}
