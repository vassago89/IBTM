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
    private readonly InputIo _housing1;
    private readonly InputIo _housing2;
    private readonly List<PcbAssembly> _assemblies = [];
    private bool _completed;

    protected StationWork(
        IIoService io,
        InputIo carrier,
        InputIo backupPlateUp,
        InputIo stopperDown,
        InputIo housing1,
        InputIo housing2)
    {
        _io = io;
        _carrier = carrier;
        _backupPlateUp = backupPlateUp;
        _stopperDown = stopperDown;
        _housing1 = housing1;
        _housing2 = housing2;
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
        _assemblies.Any(assembly => assembly.Result == PcbResult.Ng);
    public bool CanTransfer =>
        CarrierPresent
        && Completed
        && _io.GetInput(_stopperDown);

    public bool HousingPresent(HousingSlot housing) =>
        _io.GetInput(housing == HousingSlot.Housing1
            ? _housing1
            : _housing2);

    public PcbAssembly Assembly(HousingSlot housing)
    {
        var assembly = _assemblies.FirstOrDefault(
            item => item.Housing == housing);
        if (assembly is not null)
        {
            return assembly;
        }

        assembly = new PcbAssembly(housing);
        _assemblies.Add(assembly);
        return assembly;
    }

    public void SetAssemblies(IEnumerable<PcbAssembly> assemblies)
    {
        _assemblies.Clear();
        _assemblies.AddRange(assemblies);
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

    protected void NotifyChanged() => Changed?.Invoke();

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input == _carrier)
        {
            Volatile.Write(ref _completed, false);
            _assemblies.Clear();
            CarrierChanged?.Invoke(value);
        }

        if (input == _carrier
            || input == _backupPlateUp
            || input == _stopperDown
            || input == _housing1
            || input == _housing2)
        {
            Changed?.Invoke();
        }
    }
}
