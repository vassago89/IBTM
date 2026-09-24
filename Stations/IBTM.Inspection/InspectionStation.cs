using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.NgConveyor;
using IBTM.Storage;
using Microsoft.Extensions.Logging;

namespace IBTM.Inspection;

public sealed partial class InspectionStation : AutoUnit
{
    private readonly ILogger<InspectionStation>? _log;
    private readonly InspectionWork _work;
    private readonly IIoService _io;
    private readonly IXyMotion _motion;
    private readonly OperationCancellation _operations;
    private readonly MotionSettings _motionSettings;
    private readonly NgCarrierTransferSettings _settings;
    private readonly NgCarrierConveyor _ngConveyor;
    private readonly UnitSettings _units;
    private HeatSinkSlot[]? _runTargets;
    // An unfinished shuttle handoff must still wait for clearance after STOP.
    private bool _waitingForShuttleDown;
    // Selected work belongs only to this run; STOP starts again at the first point.
    private (HeatSinkSlot Pcb, BoltPoint? Bolt)[]? _runPoints;
    private int _pointIndex;
    private StationWork.Job? _runJob;
    private CancellationTokenSource? _inspectionOperation;

    public InspectionStation(
        InspectionWork work,
        IXyMotion motion,
        NgCarrierConveyor ngConveyor,
        OperationCancellation operations,
        InspectionGantrySettings motionSettings,
        NgCarrierTransferSettings settings,
        IIoService io,
        UnitSettings units,
        ICamera camera,
        ILightController light,
        LightingSettings lightingSettings,
        RecipeManager recipes,
        ILogger<InspectionStation>? log = null)
    {
        _log = log;
        _work = work;
        _io = io;
        _motion = motion;
        _operations = operations;
        _motionSettings = motionSettings.Motion;
        _settings = settings;
        _ngConveyor = ngConveyor;
        _units = units;
        _camera = camera;
        _light = light;
        _lightingSettings = lightingSettings;
        _recipes = recipes;
        _visionGate = new(1, 1);
        camera.LiveViewFailed += OnCameraLiveViewFailed;
        work.Changed += NotifyChanged;
        ngConveyor.Changed += NotifyChanged;
        recipes.Changed += NotifyChanged;
        recipes.InspectionSettingsChanged += NotifyChanged;
    }

    public override event Action? Changed;

    public BoltPoint? GetActiveBolt(bool? mainConveyorRunning = null)
    {
        return _work.Enabled
            && _work.IsReadyToInspect(mainConveyorRunning)
            ? InspectionTarget.Bolt
            : null;
    }

    public HeatSinkSlot? GetActivePcb(bool? mainConveyorRunning = null)
    {
        return _work.Enabled && _work.IsReadyToInspect(mainConveyorRunning)
            ? InspectionTarget.Pcb
            : null;
    }

    private (HeatSinkSlot? Pcb, BoltPoint? Bolt) InspectionTarget
    {
        get
        {
            if (_runPoints is { } points)
            {
                var index = _pointIndex;
                return index < points.Length ? points[index] : (null, null);
            }
            foreach (var pcb in Enum.GetValues<HeatSinkSlot>())
            {
                if (_work.Station.IsHeatSinkPresent(pcb))
                    return (pcb, null);
            }
            return (null, null);
        }
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }

    public ConveyorStation Station => _work.Station;

    public bool IsEmptyRepeatAllowed => !_units.MainConveyor && !_units.NgConveyor;

    public bool IsTransferPending => _work.IsTransferPending;

    public NgTransferLiftState Lift
    {
        get
        {
            switch ((_io.GetInput(InputIo.NgCarrierPickupUp), _io.GetInput(InputIo.NgCarrierPickupDown)))
            {
                case (true, false):
                    return NgTransferLiftState.Up;
                case (false, true):
                    return NgTransferLiftState.Down;
                default:
                    return NgTransferLiftState.Between;
            }
        }
    }

    public NgTransferGripperState Gripper
    {
        get
        {
            switch ((
                _io.GetInput(InputIo.NgCarrierGripperOpen),
                _io.GetInput(InputIo.NgCarrierGripperClosed)))
            {
                case (true, false):
                    return NgTransferGripperState.Open;
                case (false, true):
                    return NgTransferGripperState.Closed;
                default:
                    return NgTransferGripperState.Between;
            }
        }
    }

    public bool IsRaised => _work.IsRaised;

    public bool IsClear => _work.IsClear;

    public bool IsCarrierPresent(NgTransferDestination location)
    {
        return location == NgTransferDestination.Station
            ? Station.CarrierPresent
            : _io.GetInput(InputIo.NgShuttleCarrierDetected);
    }

    public MotionStatus Motion => _work.Motion;

    public IMotionFeedback Feedback => _motion;
}
