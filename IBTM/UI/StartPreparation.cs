using System.Threading;
using System.Windows;
using IBTM.Device;

namespace IBTM.UI;

public abstract class StartPreparation
{
    private int _preparedVersion = -1;
    private int _changeVersion;
    private readonly MachineState _state;
    private readonly StationWork _work;
    private readonly IIoService _io;
    private bool _automaticRunning;

    protected StartPreparation(
        MachineState state,
        StationWork work,
        IIoService io,
        RecipeEditor recipeEditor)
    {
        _state = state;
        _work = work;
        _io = io;
        _automaticRunning = state.AutomaticRunning;
        state.Changed += OnMachineStateChanged;
        work.Station.Changed += Invalidate;
        // Keep physical work history, but confirm it against the newly loaded recipe.
        recipeEditor.Changed += Invalidate;
    }

    public bool Required
    {
        get
        {
            return _io.IsReady
                && _work.Enabled
                && !_state.AutomaticRunning
                && _work.CarrierPresent;
        }
    }

    public bool Prepared
    {
        get
        {
            return _io.IsReady
                && (!Required
                    || Volatile.Read(ref _preparedVersion) == Volatile.Read(ref _changeVersion));
        }
    }

    public bool Prepare(Window owner)
    {
        return Prepared || Open(owner);
    }

    public bool Open(Window owner)
    {
        var version = Volatile.Read(ref _changeVersion);
        if (!Required || !Show(owner, version) || !CanApply(version))
        {
            return false;
        }

        Volatile.Write(ref _preparedVersion, version);
        return Prepared;
    }

    private void Invalidate()
    {
        Interlocked.Increment(ref _changeVersion);
    }

    protected bool CanApply(int version)
    {
        // Returning to the same sensor values does not restore the operator's confirmation.
        return version == Volatile.Read(ref _changeVersion) && Required;
    }

    protected abstract bool Show(Window owner, int version);

    private void OnMachineStateChanged()
    {
        if (!_io.IsReady || _automaticRunning != _state.AutomaticRunning)
        {
            Invalidate();
        }

        _automaticRunning = _state.AutomaticRunning;
    }
}
