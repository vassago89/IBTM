using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using IBTM.Device;

namespace IBTM.UI;

public enum StartPreparationType
{
    [Description("PCB Placement Recovery")]
    PcbPlacementRecovery,

    [Description("Bolt Fastening Recovery")]
    BoltFasteningRecovery,
}

public abstract class StartPreparation
{
    private volatile bool _prepared;

    public abstract StartPreparationType Type { get; }
    public abstract bool Required { get; }

    public bool Prepare(Window owner) =>
        !Required || _prepared || Open(owner);

    public bool Open(Window owner)
    {
        if (!Required || !Show(owner))
        {
            return false;
        }

        _prepared = true;
        return true;
    }

    protected void Invalidate() => _prepared = false;
    protected abstract bool Show(Window owner);
}

public abstract class StationRecoveryPreparation : StartPreparation
{
    private bool _automaticRunning;

    protected StationRecoveryPreparation(
        MachineState state,
        StationWork work)
    {
        State = state;
        _automaticRunning = state.AutomaticRunning;
        state.Changed += OnMachineStateChanged;
        work.Changed += Invalidate;
    }

    protected MachineState State { get; }

    private void OnMachineStateChanged()
    {
        if (_automaticRunning && !State.AutomaticRunning)
        {
            Invalidate();
        }

        _automaticRunning = State.AutomaticRunning;
    }
}

public sealed class StartPreparationPlan(
    IEnumerable<StartPreparation> preparations)
{
    private readonly StartPreparation[] _preparations =
        [.. preparations.OrderBy(preparation => preparation.Type)];

    public bool Prepare(Window owner)
    {
        foreach (var preparation in _preparations)
        {
            if (!preparation.Prepare(owner))
            {
                return false;
            }
        }

        return true;
    }

    public bool CanOpen(StartPreparationType type) =>
        Find(type).Required;

    public bool Open(StartPreparationType type, Window owner) =>
        Find(type).Open(owner);

    private StartPreparation Find(StartPreparationType type) =>
        _preparations.Single(preparation => preparation.Type == type);
}
