using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.NgConveyor;

public sealed partial class NgCarrierConveyor : AutoUnit
{
    private readonly IIoService _io;
    private readonly NgConveyorSettings _settings;
    private readonly INgCarrierTransferFeedback _transfer;
    private readonly UnitSettings _units;
    private volatile Movement _movement;
    private volatile EjectionPhase _ejectionPhase;
    private bool _repeat;

    public NgCarrierConveyor(
        IIoService io,
        NgConveyorSettings settings,
        INgCarrierTransferFeedback transfer,
        UnitSettings units)
    {
        _io = io;
        _settings = settings;
        _transfer = transfer;
        _units = units;
        transfer.Changed += NotifyChanged;
        io.InputChanged += OnInputChanged;
        io.OutputChanged += OnOutputChanged;
    }

    public override event Action? Changed;

    public bool RunCommandOn => _io.GetOutput(OutputIo.NgConveyorRun);

    public bool Position1Occupied => _io.GetInput(InputIo.NgConveyorPosition1Occupied);

    public bool Position2Occupied => _io.GetInput(InputIo.NgConveyorPosition2Occupied);

    public bool Position3Occupied => _io.GetInput(InputIo.NgShuttleCarrierDetected);

    public NgShuttleLiftState ShuttleLift
    {
        get
        {
            switch ((_io.GetInput(InputIo.NgShuttleUp), _io.GetInput(InputIo.NgShuttleDown)))
            {
                case (true, false):
                    return NgShuttleLiftState.Up;
                case (false, true):
                    return NgShuttleLiftState.Down;
                default:
                    return NgShuttleLiftState.Between;
            }
        }
    }

    public int CarrierCount => (Position1Occupied ? 1 : 0) + (Position2Occupied ? 1 : 0) + (Position3Occupied ? 1 : 0);

    public int AlarmCarrierCount => _settings.AlarmCarrierCount;

    public bool AlarmRequired => CarrierCount >= AlarmCarrierCount;

    public bool Full => CarrierCount == 3;

    private bool EjectRequested => _io.GetInput(InputIo.NgCarrierEjectButton);

    private bool EjectConfirmed => _io.GetInput(InputIo.NgCarrierEjectCompleteButton);

    private bool IsShuttleRaiseRequired(bool runCommandOn)
    {
        return !runCommandOn
            && ((!Position3Occupied
                    && (_movement == Movement.None
                        || _movement == Movement.ToPosition1 && Position1Occupied
                        || _movement == Movement.ToPosition2 && Position2Occupied))
                || Full && _movement == Movement.None);
    }

    public NgConveyorState State => Step is NgConveyorState step ? step : GetNextStep(RunCommandOn);

    // Pending ownership is cleared by the release operation, never by presence DI.
    private bool IsTransferClear => _transfer.IsClear
        && _io.GetInput(InputIo.NgCarrierGripperOpen)
        && !_io.GetInput(InputIo.NgCarrierGripperClosed);

    public bool IsReceiveAllowed(bool? conveyorRunning = null)
    {
        return ShuttleLift == NgShuttleLiftState.Up
            && !Position3Occupied
            && IsAcceptCarrierAllowed(conveyorRunning);
    }

    private bool NeedsCompaction => _movement == Movement.Compacting || !Position1Occupied && Position2Occupied;

    private bool IsAcceptCarrierAllowed(bool? runCommandOn = null)
    {
        return _movement == Movement.None
            && _ejectionPhase == EjectionPhase.Idle
            && !Full
            && !NeedsCompaction
            && (_repeat || !EjectRequested)
            && !(runCommandOn ?? RunCommandOn);
    }

    private void NotifyChanged()
    {
        if (_ejectionPhase == EjectionPhase.WaitingForButtonRelease
            && !EjectRequested
            && !EjectConfirmed)
        {
            _ejectionPhase = EjectionPhase.Idle;
        }

        Changed?.Invoke();
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input is InputIo.NgShuttleUp or InputIo.NgShuttleDown or InputIo.NgShuttleCarrierDetected
            && ShuttleLift == NgShuttleLiftState.Up && IsShuttleRaiseRequired(RunCommandOn))
            _movement = Movement.None;
        if (input == InputIo.NgConveyorPosition1Occupied
            && Position1Occupied
            && _movement == Movement.Compacting)
        {
            _movement = Movement.None;
        }

        if (input is InputIo.NgConveyorPosition1Occupied
            or InputIo.NgConveyorPosition2Occupied
            or InputIo.NgConveyorStopperUp
            or InputIo.NgConveyorStopperDown
            or InputIo.NgCarrierEjectButton
            or InputIo.NgCarrierEjectCompleteButton
            or InputIo.NgShuttleUp
            or InputIo.NgShuttleDown
            or InputIo.NgShuttleCarrierDetected)
        {
            NotifyChanged();
        }
    }

    private void OnOutputChanged(OutputIo output, bool value)
    {
        if (output == OutputIo.NgConveyorRun)
        {
            Changed?.Invoke();
        }
    }

    private enum EjectionPhase
    {
        Idle,
        Ejecting,
        WaitingForConfirmation,
        WaitingForButtonRelease,
    }

    private enum Movement
    {
        None,
        ToPosition1,
        ToPosition2,
        Compacting,
    }
}
