using System;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;
using Microsoft.Extensions.Logging;

namespace IBTM.BoltFastening;

public sealed partial class BoltFasteningStation : AutoUnit
{
    public IBoltHead ShootingHead { get; }
    public IBoltHead PickupHead { get; }
    private readonly IIoService _io;
    private readonly IXyMotion _motion;
    private readonly BoltFasteningSettings _settings;
    private readonly CarrierReferenceSettings _carrierReference;
    private readonly RecipeManager _recipes;
    private readonly UnitSettings _units;
    private readonly ILogger<BoltFasteningStation>? _log;

    public BoltFasteningStation(
        IBoltHead shootingHead,
        IBoltHead pickupHead,
        IIoService io,
        IXyMotion motion,
        MotionStatus motionStatus,
        BoltFasteningSettings settings,
        CarrierReferenceSettings carrierReference,
        ConveyorStation station,
        RecipeManager recipes,
        UnitSettings units,
        ILogger<BoltFasteningStation>? log = null)
    {
        ShootingHead = shootingHead;
        PickupHead = pickupHead;
        _io = io;
        _motion = motion;
        _settings = settings;
        _carrierReference = carrierReference;
        Station = station;
        _recipes = recipes;
        _units = units;
        _log = log;
        Motion = motionStatus;
        InitializeRecipeBoltPositions();
        recipes.Changed += InitializeRecipeBoltPositions;
        io.InputChanged += OnInputChanged;
        station.Changed += NotifyChanged;
    }

    public ConveyorStation Station { get; }

    public bool Enabled => _units.BoltFastening;

    private bool IsReadyToFasten => Station.CarrierSeated && !Station.Completed;

    public override event Action? Changed;

    private void InitializeRecipeBoltPositions()
    {
        // Older recipes have only inspection XY. Never overwrite independently taught coordinates.
        foreach (var bolt in _recipes.Current.Pcb.BoltPoints)
            _settings.InitializeBoltPosition(bolt, _carrierReference);
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }

    public MotionStatus Motion { get; }

    public IMotionFeedback Feedback => _motion;

    public bool PickupBoltLoaded => _io.GetInput(InputIo.PickupHeadVacuumDetected);

    internal bool ShootingTubeBoltDetected => _io.GetInput(InputIo.ShootingTubeBoltDetected);

    public BoltCylinderState PickupHeadPosition
    {
        get
        {
            switch ((_io.GetInput(InputIo.PickupHeadUp), _io.GetInput(InputIo.PickupHeadDown)))
            {
                case (true, false):
                    return BoltCylinderState.Up;
                case (false, true):
                    return BoltCylinderState.Down;
                default:
                    return BoltCylinderState.Between;
            }
        }
    }

    public BoltCylinderState ShootingHeadPosition
    {
        get
        {
            switch ((_io.GetInput(InputIo.ShootingHeadUp), _io.GetInput(InputIo.ShootingHeadDown)))
            {
                case (true, false):
                    return BoltCylinderState.Up;
                case (false, true):
                    return BoltCylinderState.Down;
                default:
                    return BoltCylinderState.Between;
            }
        }
    }

    public BoltCylinderState PickupTablePosition
    {
        get
        {
            switch ((_io.GetInput(InputIo.PickupTableUp), _io.GetInput(InputIo.PickupTableDown)))
            {
                case (true, false):
                    return BoltCylinderState.Up;
                case (false, true):
                    return BoltCylinderState.Down;
                default:
                    return BoltCylinderState.Between;
            }
        }
    }

    public bool IsHorizontalMoveAllowed
    {
        get
        {
            return PickupHeadPosition == BoltCylinderState.Up
                && ShootingHeadPosition == BoltCylinderState.Up;
        }
    }

    internal BoltEscapeState ShootingEscape
    {
        get
        {
            switch ((
                _io.GetInput(InputIo.ShootingEscapeForward),
                _io.GetInput(InputIo.ShootingEscapeBackward)))
            {
                case (true, false):
                    return BoltEscapeState.Forward;
                case (false, true):
                    return BoltEscapeState.Backward;
                default:
                    return BoltEscapeState.Between;
            }
        }
    }

    internal IBoltHead GetHead(FasteningHead head)
    {
        switch (head)
        {
            case FasteningHead.Shooting:
                return ShootingHead;
            case FasteningHead.Pickup:
                return PickupHead;
            default:
                throw new ArgumentOutOfRangeException(nameof(head));
        }
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input is InputIo.PickupHeadVacuumDetected
            or InputIo.ShootingTubeBoltDetected
            or InputIo.PickupHeadUp
            or InputIo.PickupHeadDown
            or InputIo.ShootingHeadUp
            or InputIo.ShootingHeadDown
            or InputIo.PickupTableUp
            or InputIo.PickupTableDown
            or InputIo.ShootingEscapeForward
            or InputIo.ShootingEscapeBackward)
        {
            Changed?.Invoke();
        }
    }
}
