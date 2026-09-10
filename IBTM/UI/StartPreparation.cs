using System.Windows;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public abstract class StartPreparation
{
    private volatile bool _prepared;
    private readonly MachineState _state;
    private readonly StationWork _work;
    private bool _automaticRunning;

    protected StartPreparation(MachineState state, StationWork work)
    {
        _state = state;
        _work = work;
        _automaticRunning = state.AutomaticRunning;
        state.Changed += OnMachineStateChanged;
        work.Changed += Invalidate;
    }

    public bool Required
    {
        get
        {
            return _work.Enabled
                && !_state.AutomaticRunning
                && _work.CarrierPresent
                && (_work.HeatSinkPresent(HeatSinkSlot.HeatSink1)
                    || _work.HeatSinkPresent(HeatSinkSlot.HeatSink2));
        }
    }

    public bool Prepare(Window owner)
    {
        return !Required || _prepared || Open(owner);
    }

    public bool Open(Window owner)
    {
        if (!Required || !Show(owner))
        {
            return false;
        }

        _prepared = true;
        return true;
    }

    private void Invalidate()
    {
        _prepared = false;
    }

    protected abstract bool Show(Window owner);

    private void OnMachineStateChanged()
    {
        if (_automaticRunning && !_state.AutomaticRunning)
        {
            Invalidate();
        }

        _automaticRunning = _state.AutomaticRunning;
    }
}
