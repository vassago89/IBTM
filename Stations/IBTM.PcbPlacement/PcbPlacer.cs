using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;

namespace IBTM.PcbPlacement;

public sealed partial class PcbPlacer : AutoUnit, IPcbPlacementHandoff
{
    private readonly IXyMotion _motion;
    private readonly IIoService _io;
    private readonly PcbPlacementHandlerSettings _settings;
    private readonly IPcbSupplyHandoff _supply;
    private readonly RecipeManager _recipes;
    private readonly UnitSettings _units;

    // Ownership until the PCB is placed back on its original carrier, including STOP.
    private RepeatPcbTrip? _repeatTrip;

    public HeatSinkSlot? ReturningPcb => IsRunning && _repeatTrip is { } trip
        && State is PcbPlacementState.ReturningToSupply or PcbPlacementState.WaitingForSupplyReceipt
            or PcbPlacementState.PresentingToSupply or PcbPlacementState.WaitingForSupplyGrip
        ? trip.HeatSink : null;

    private sealed record RepeatPcbTrip(ConveyorStation.Job Job, HeatSinkSlot HeatSink);

    private HeatSinkSlot[]? _runTargets;
    // Completed stage position, invalidated by motion/state changes; never proof of current readiness.
    private AxisPosition? _handoffPosition;

    public PcbPlacer(
        IXyMotion motion,
        MotionStatus motionStatus,
        IIoService io,
        PcbPlacementHandlerSettings settings,
        IPcbSupplyHandoff supply,
        ConveyorStation station,
        RecipeManager recipes,
        UnitSettings units)
    {
        _motion = motion;
        _io = io;
        _settings = settings;
        _supply = supply;
        Station = station;
        _recipes = recipes;
        _units = units;
        Motion = motionStatus;
        io.InputChanged += OnInputChanged;
        motion.StateChanged += OnMotionStateChanged;
        station.Changed += NotifyChanged;
        station.CarrierChanged += OnCarrierChanged;
        StepChanged += NotifyChanged;
    }

    public ConveyorStation Station { get; }

    public bool Enabled => _units.PcbPlacement;

    public override event Action? Changed;

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }

    private void OnMotionStateChanged()
    {
        _handoffPosition = null;
        NotifyChanged();
    }

    public MotionStatus Motion { get; }

    public IMotionFeedback Feedback => _motion;

    public PlacementCylinderState Lift
    {
        get
        {
            switch ((_io.GetInput(InputIo.PcbPlacementHandlerUp), _io.GetInput(InputIo.PcbPlacementHandlerDown)))
            {
                case (true, false):
                    return PlacementCylinderState.Up;
                case (false, true):
                    return PlacementCylinderState.Down;
                default:
                    return PlacementCylinderState.Between;
            }
        }
    }

    public bool HandlerRaised => Lift == PlacementCylinderState.Up;

    public PlacementCylinderState IpmLift
    {
        get
        {
            switch ((_io.GetInput(InputIo.PcbPlacementIpmUp), _io.GetInput(InputIo.PcbPlacementIpmDown)))
            {
                case (true, false):
                    return PlacementCylinderState.Up;
                case (false, true):
                    return PlacementCylinderState.Down;
                default:
                    return PlacementCylinderState.Between;
            }
        }
    }

    public PlacementPcbState Pcb
    {
        get
        {
            if (!_io.GetInput(InputIo.PcbPlacementPcbDetected))
            {
                return PlacementPcbState.None;
            }

            return VacuumDetected
                ? PlacementPcbState.Secured
                : PlacementPcbState.Detected;
        }
    }

    public bool PcbSecured => Pcb == PlacementPcbState.Secured;

    public bool VacuumDetected => _io.GetInput(InputIo.PcbPlacementVacuumDetected);

    public bool IsAtHorizontalZ(bool live = true)
    {
        return Motion.IsAtZ(_settings.HandoffPosition.Z, live)
            && Motion.IsSettled(live, MotionAxis.Z);
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input is InputIo.PcbPlacementHandlerDown
            or InputIo.PcbPlacementHandlerUp
            or InputIo.PcbPlacementIpmDown
            or InputIo.PcbPlacementIpmUp
            or InputIo.PcbPlacementPcbDetected
            or InputIo.PcbPlacementVacuumDetected)
        {
            Changed?.Invoke();
        }
    }

    protected override Enum? DisplayStep => State;

    public PcbPlacementState State
    {
        get => !Enabled ? PcbPlacementState.Disabled
            : SequenceStep is PcbPlacementState step ? step : PcbPlacementState.MovingToHandoff;
        private set
        {
            if (Equals(SequenceStep, value))
                return;
            EnterStep(value, workId: Station.CurrentJob.Id);
            if (!IsRunning)
                Changed?.Invoke();
        }
    }

    public PcbPlacementHandoff Handoff
    {
        get
        {
            var position = _handoffPosition;
            if (position is null)
                return PcbPlacementHandoff.Unavailable;
            if (!Motion.IsHoldingPosition(position))
            {
                _handoffPosition = null;
                return PcbPlacementHandoff.Unavailable;
            }
            if (!HandlerRaised || IpmLift == PlacementCylinderState.Between)
                return PcbPlacementHandoff.Unavailable;
            switch (State)
            {
                case PcbPlacementState.WaitingForSupplyGrip when PcbSecured:
                    return PcbPlacementHandoff.Returning;
                case PcbPlacementState.WaitingForSupplyRelease when PcbSecured:
                    return PcbPlacementHandoff.Holding;
                case PcbPlacementState.WaitingForSupply when !PcbSecured:
                case PcbPlacementState.WaitingForSupplyDeparture:
                case PcbPlacementState.WaitingForSupplyClear:
                case PcbPlacementState.WaitingForCarrier
                    or PcbPlacementState.PlacingPcb or PcbPlacementState.CompletingCarrier:
                    return PcbPlacementHandoff.Clear;
                default:
                    return PcbPlacementHandoff.Unavailable;
            }
        }
    }

    public HeatSinkSlot? TargetHeatSink
    {
        get
        {
            switch (true)
            {
                case true when _repeatTrip is { } trip:
                    return trip.HeatSink;
                case true when Station.Completed:
                    return null;
                case true when IsTarget(HeatSinkSlot.HeatSink1) && !IsHeatSinkCompleted(HeatSinkSlot.HeatSink1):
                    return HeatSinkSlot.HeatSink1;
                default:
                    return IsTarget(HeatSinkSlot.HeatSink2) && !IsHeatSinkCompleted(HeatSinkSlot.HeatSink2)
                        ? HeatSinkSlot.HeatSink2
                        : null;
            }
        }
    }

    private void OnCarrierChanged(bool present)
    {
        _runTargets = null;
    }

    private bool IsPcbGripUncertain
    {
        get
        {
            var pcb = Pcb;
            if (pcb == PlacementPcbState.Secured || !VacuumDetected)
                return false;
            if (State is PcbPlacementState.ReceivingPcb or PcbPlacementState.WaitingForSupplyRelease
                && _supply.Handoff == PcbSupplyHandoff.Holding)
                return false;
            return true;
        }
    }

    private bool IsTarget(HeatSinkSlot heatSink)
    {
        return _runTargets?.Contains(heatSink) ?? Station.IsHeatSinkPresent(heatSink);
    }

    private bool IsHeatSinkCompleted(HeatSinkSlot heatSink)
    {
        return Station.Assemblies.Any(assembly => assembly.HeatSink == heatSink);
    }

    private AxisPosition GetHeatSinkPosition(HeatSinkSlot heatSink)
    {
        var recipe = _recipes.Current.PcbPlacement;
        return heatSink == HeatSinkSlot.HeatSink1
            ? recipe.HeatSink1PcbPlacementPosition
            : recipe.HeatSink2PcbPlacementPosition;
    }
}
