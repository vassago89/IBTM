using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Virtual;

public sealed class VirtualMachine
{
    private const int TransferDelayMilliseconds = 200;

    private readonly VirtualIoService _io;
    private readonly IReadOnlyList<VirtualMotionService> _motions;
    private readonly Func<bool>? _incomingCarrierHasPcbs;
    private readonly bool[] _supplyPcbs;
    private int _supplySmemaVersion;
    private int _mainConveyorTransferVersion;
    private (bool HeatSink1, bool HeatSink2, bool Pcb1, bool Pcb2)? _mainEntryCarrier;
    private readonly Dictionary<InputIo, (bool Pcb1, bool Pcb2)> _carrierPcbs;
    private int _placementVacuumVersion;
    private int _pickupVacuumVersion;
    private int _shootingVacuumVersion;
    private int _shootVersion;
    private int _shootingFeederVersion;
    private int _ngConveyorVersion;
    private int? _supplyPickupSlot;
    private bool _supplyAtBuffer;
    private bool _supplyHoldingPcb;
    private bool _placementAtBuffer;
    private bool _placementHoldingPcb;
    private readonly bool[] _placedPcbs;
    private int? _placementHeatSink;
    private bool _inspectionAtNgPickup;
    private bool _inspectionAtNgShuttle;
    private bool _ngCarrierHeld;
    private bool _ngCarrierHeatSink1;
    private bool _ngCarrierHeatSink2;
    private (bool Pcb1, bool Pcb2) _ngCarrierPcbs;

    public VirtualMachine(
        VirtualIoService io,
        IReadOnlyList<VirtualMotionService> motions,
        Func<bool>? incomingCarrierHasPcbs = null)
    {
        _supplyPcbs = new bool[2];
        _carrierPcbs = [];
        _placedPcbs = new bool[2];

        _io = io;
        _motions = motions;
        _incomingCarrierHasPcbs = incomingCarrierHasPcbs;
        io.InputChanged += OnInputChanged;
        io.OutputChanged += OnOutputChanged;
        io.OutputApplied += ApplyPhysicalOutput;
        io.PropertyChanged += OnIoPropertyChanged;
        io.FeedbackSynchronized += SynchronizeGrip;
        io.SetInput(InputIo.ServoMainContactorOn, true);
        io.ApplyAutoResponse(
            io.AutoResponseVersion,
            () =>
            {
                io.SetInput(InputIo.MainConveyorAvailableFromFront2, true);
                io.SetInput(InputIo.MainConveyorReadyFromRear, true);
                io.SetInput(InputIo.PickupFeederBoltDetected, true);
            });
        if (EmergencyStopPressed || AutoMode && DoorOpen)
        {
            DropServoPower();
        }
    }

    private bool AutoMode => !_io.GetInput(InputIo.AutoMode);

    private bool DoorOpen
    {
        get
        {
            return !_io.GetInput(InputIo.Door1Open)
                || !_io.GetInput(InputIo.Door2Open)
                || !_io.GetInput(InputIo.Door3Open)
                || !_io.GetInput(InputIo.Door4Open)
                || !_io.GetInput(InputIo.Door5Open)
                || !_io.GetInput(InputIo.Door6Open);
        }
    }

    private bool EmergencyStopPressed
    {
        get
        {
            return _io.GetInput(InputIo.EmergencyStop1Pressed)
                || _io.GetInput(InputIo.EmergencyStop2Pressed);
        }
    }

    private void OnIoPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VirtualIoService.AutoResponseEnabled))
            SynchronizeGrip();
    }

    private void SynchronizeGrip()
    {
        _io.ApplyAutoResponse(
            _io.AutoResponseVersion,
            () =>
            {
                _supplyHoldingPcb = _io.GetInput(InputIo.PcbSupplyPcbDetected)
                    && _io.GetInput(InputIo.PcbSupplyGripperClosed)
                    && !_io.GetInput(InputIo.PcbSupplyGripperOpen);
                _placementHoldingPcb = _io.GetInput(InputIo.PcbPlacementPcbDetected)
                    && _io.GetInput(InputIo.PcbPlacementVacuumDetected)
                    && _io.GetInput(InputIo.PcbPlacementIpmGripperClosed)
                    && !_io.GetInput(InputIo.PcbPlacementIpmGripperOpen);
                _ngCarrierHeld = _io.GetInput(InputIo.NgCarrierDetected)
                    && _io.GetInput(InputIo.NgCarrierGripperClosed)
                    && !_io.GetInput(InputIo.NgCarrierGripperOpen);
            });
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input is InputIo.PcbPlacementHeatSink1Present or InputIo.PcbPlacementHeatSink2Present
            && !_io.GetInput(InputIo.PcbPlacementHeatSink1Present)
            && !_io.GetInput(InputIo.PcbPlacementHeatSink2Present))
            Array.Clear(_placedPcbs);
        if (input is InputIo.BoltFasteningHeatSink1Present or InputIo.BoltFasteningHeatSink2Present
            && !_io.GetInput(InputIo.BoltFasteningHeatSink1Present)
            && !_io.GetInput(InputIo.BoltFasteningHeatSink2Present))
            _carrierPcbs.Remove(InputIo.BoltFasteningHeatSink1Present);
        if (input is InputIo.InspectionHeatSink1Present or InputIo.InspectionHeatSink2Present
            && !_io.GetInput(InputIo.InspectionHeatSink1Present)
            && !_io.GetInput(InputIo.InspectionHeatSink2Present))
            _carrierPcbs.Remove(InputIo.InspectionHeatSink1Present);
        if (value
            && IsEmergencyStop(input)
            || (input == InputIo.AutoMode || IsDoor(input))
            && AutoMode
            && DoorOpen)
        {
            DropServoPower();
            return;
        }

        if (input == InputIo.ResetButton
            && value
            && !EmergencyStopPressed
            && (!AutoMode || !DoorOpen))
        {
            RestoreServoPower();
        }
    }

    private void DropServoPower()
    {
        _io.SetInput(InputIo.ServoMainContactorOn, false);
        foreach (var motion in _motions)
        {
            foreach (var axis in motion.Axes)
            {
                motion.SetServo(axis, false);
                motion.SetAlarm(axis, true);
            }
        }
    }

    private void RestoreServoPower()
    {
        _io.SetInput(InputIo.ServoMainContactorOn, true);
        foreach (var motion in _motions)
        {
            foreach (var axis in motion.Axes)
            {
                motion.SetServo(axis, true);
            }
        }
    }

    private static bool IsDoor(InputIo input)
    {
        return input is InputIo.Door1Open
            or InputIo.Door2Open
            or InputIo.Door3Open
            or InputIo.Door4Open
            or InputIo.Door5Open
            or InputIo.Door6Open;
    }

    private static bool IsEmergencyStop(InputIo input)
    {
        return input is InputIo.EmergencyStop1Pressed or InputIo.EmergencyStop2Pressed;
    }

    public void UpdateSupplyPosition(
        double x,
        double y,
        double z,
        double carrierY,
        (double X, double Z) pcb1,
        (double X, double Z) pcb2,
        AxisPosition handoff)
    {
        _supplyAtBuffer = IsAt(x, y, z, handoff);
        if (!_supplyHoldingPcb)
        {
            _supplyPickupSlot = IsAt(x, y, z, pcb1.X, carrierY, pcb1.Z)
                ? 0
                : IsAt(x, y, z, pcb2.X, carrierY, pcb2.Z) ? 1 : null;
        }

        _io.ApplyAutoResponse(
            _io.AutoResponseVersion,
            () =>
            {
                UpdateSupplyDetection();
                UpdatePlacementDetection();
            });
    }

    public void UpdatePlacementPosition(
        double x,
        double y,
        double z,
        AxisPosition bufferPosition,
        AxisPosition? heatSink1 = null,
        AxisPosition? heatSink2 = null)
    {
        _placementAtBuffer = IsAt(x, y, z, bufferPosition);
        _placementHeatSink = heatSink1 is not null && IsAt(x, y, z, heatSink1)
            ? 0
            : heatSink2 is not null && IsAt(x, y, z, heatSink2) ? 1 : null;
        _io.ApplyAutoResponse(
            _io.AutoResponseVersion,
            () =>
            {
                UpdateSupplyDetection();
                UpdatePlacementDetection();
            });
    }

    private void UpdateSupplyDetection()
    {
        _io.SetInput(
            InputIo.PcbSupplyPcbDetected,
            _supplyHoldingPcb
                || _supplyAtBuffer && _placementAtBuffer && _placementHoldingPcb
                || _supplyPickupSlot is { } slot && _supplyPcbs[slot]);
    }

    private void UpdatePlacementDetection()
    {
        _io.SetInput(
            InputIo.PcbPlacementPcbDetected,
            _placementHoldingPcb
                || _placementAtBuffer
                && _supplyAtBuffer && _supplyHoldingPcb
                || _placementHeatSink is { } slot
                && _placedPcbs[slot]
                && _io.GetInput(InputIo.PcbPlacementHandlerDown));
    }

    public void UpdateInspectionPosition(
        double x,
        double y,
        AxisPosition? pickupPosition,
        AxisPosition shuttlePosition)
    {
        _inspectionAtNgPickup = pickupPosition is not null && IsAt(x, y, pickupPosition);
        _inspectionAtNgShuttle = IsAt(x, y, shuttlePosition);
    }

    private void OnOutputChanged(OutputIo output, bool value)
    {
        if (!_io.AutoResponseEnabled)
        {
            return;
        }

        if (output == OutputIo.MainConveyorRun)
        {
            var transferVersion = Interlocked.Increment(ref _mainConveyorTransferVersion);
            if (value)
            {
                _ = TransferMainCarrierAsync(transferVersion);
            }
        }
        else if (output == OutputIo.MainConveyorReadyToFront2
            && value
            && !_io.GetOutput(OutputIo.MainConveyorRun)
            && !_io.GetInput(InputIo.MainConveyorAvailableFromFront2)
            && !(_io.GetInput(InputIo.PcbPlacementHeatSink1Present) || _io.GetInput(InputIo.PcbPlacementHeatSink2Present)))
        {
            _ = PresentMainCarrierAsync(_mainConveyorTransferVersion);
        }

        switch (output)
        {
            case OutputIo.PcbPlacementVacuumEjector:
                {
                    var vacuumVersion = Interlocked.Increment(ref _placementVacuumVersion);
                    _ = ApplyPlacementVacuumAsync(value, vacuumVersion);
                    return;
                }
            case OutputIo.PickupHeadVacuumPump:
                {
                    var vacuumVersion = Interlocked.Increment(ref _pickupVacuumVersion);
                    _ = ApplyPickupVacuumAsync(value, vacuumVersion);
                    return;
                }
            case OutputIo.ShootingHeadVacuumPump:
                {
                    var vacuumVersion = Interlocked.Increment(ref _shootingVacuumVersion);
                    if (!value)
                    {
                        _ = ReleaseShootingVacuumAsync(vacuumVersion);
                    }

                    return;
                }
            case OutputIo.ShootBolt:
                {
                    var shootVersion = Interlocked.Increment(ref _shootVersion);
                    if (value)
                    {
                        _ = ApplyShootAsync(shootVersion);
                    }

                    return;
                }
            case OutputIo.ShootingFeederRunSignal:
                {
                    var feederVersion = Interlocked.Increment(ref _shootingFeederVersion);
                    if (value)
                    {
                        _ = FeedShootingBoltAsync(feederVersion);
                    }

                    return;
                }
            case OutputIo.NgConveyorRun:
                {
                    var conveyorVersion = Interlocked.Increment(ref _ngConveyorVersion);
                    if (value)
                    {
                        _ = TransferNgCarrierAsync(conveyorVersion);
                    }

                    return;
                }
            case not OutputIo.PcbSupplyReadyToFront1:
                return;
        }

        var version = Interlocked.Increment(ref _supplySmemaVersion);
        _ = TransferSupplyCarrierAsync(value, version);
    }

    private Task ApplyPlacementVacuumAsync(bool value, int version)
    {
        return RespondAsync(
            _io.AutoResponseVersion,
            () =>
            {
                if (_placementVacuumVersion != version)
                {
                    return;
                }

                _io.SetInput(
                    InputIo.PcbPlacementVacuumDetected,
                    value && _io.GetInput(InputIo.PcbPlacementPcbDetected));
            });
    }

    private Task ApplyPickupVacuumAsync(bool value, int version)
    {
        var responseVersion = _io.AutoResponseVersion;
        return RespondAsync(
            responseVersion,
            () =>
            {
                if (_pickupVacuumVersion != version)
                {
                    return;
                }

                var loaded = value && _io.GetInput(InputIo.PickupFeederBoltDetected);
                _io.SetInput(InputIo.PickupHeadVacuumDetected, loaded);
                if (!loaded)
                {
                    return;
                }

                _io.SetInput(InputIo.PickupFeederBoltDetected, false);
                _ = RespondAsync(responseVersion, () => _io.SetInput(InputIo.PickupFeederBoltDetected, true));
            });
    }

    private Task FeedShootingBoltAsync(int version)
    {
        return RespondAsync(
            _io.AutoResponseVersion,
            () =>
            {
                if (_shootingFeederVersion == version
                    && _io.GetOutput(OutputIo.ShootingFeederRunSignal))
                {
                    _io.SetInput(InputIo.ShootingFeederBoltDetected, true);
                }
            });
    }

    private Task ApplyShootAsync(int version)
    {
        var responseVersion = _io.AutoResponseVersion;
        return RespondAsync(
            responseVersion,
            () =>
            {
                switch (true)
                {
                    case true when _shootVersion != version:
                        return;
                    case true when !_io.GetInput(InputIo.ShootingEscapeForward)
                        || !_io.GetOutput(OutputIo.ShootingHeadVacuumPump):
                        return;
                }

                _io.SetInput(InputIo.ShootingTubeBoltDetected, true);
                _ = RespondAsync(
                    responseVersion,
                    () =>
                    {
                        if (_shootVersion == version && _io.GetOutput(OutputIo.ShootBolt))
                        {
                            _io.SetInput(InputIo.ShootingTubeBoltDetected, false);
                            _io.SetInput(InputIo.ShootingHeadVacuumDetected, true);
                        }
                    });
            });
    }

    private Task ReleaseShootingVacuumAsync(int version)
    {
        return RespondAsync(
            _io.AutoResponseVersion,
            () =>
            {
                if (_shootingVacuumVersion == version)
                {
                    _io.SetInput(InputIo.ShootingHeadVacuumDetected, false);
                }
            });
    }

    private Task TransferSupplyCarrierAsync(bool ready, int version)
    {
        return RespondAsync(
            _io.AutoResponseVersion,
            () =>
            {
                if (_supplySmemaVersion != version
                    || _io.GetOutput(OutputIo.PcbSupplyReadyToFront1) != ready)
                    return;

                if (ready)
                {
                    if (_io.GetInput(InputIo.PcbSupplyAvailableFromFront1))
                        return;
                    _supplyPcbs[0] = true;
                    _supplyPcbs[1] = true;
                    _io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
                }
                else
                {
                    Array.Clear(_supplyPcbs);
                    _io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
                    UpdateSupplyDetection();
                }
            });
    }

    private Task TransferMainCarrierAsync(int version)
    {
        var responseVersion = _io.AutoResponseVersion;
        return RespondAsync(
            responseVersion,
            () =>
            {
                switch (true)
                {
                    case true when _mainConveyorTransferVersion != version
                        || !_io.GetOutput(OutputIo.MainConveyorRun):
                        return;
                    case true when !_io.GetOutput(OutputIo.MainConveyorForward):
                        ReturnMainCarrier(version);
                        return;
                    case true when _io.GetInput(InputIo.MainConveyorExitCarrierDetected):
                        _io.SetInput(InputIo.MainConveyorExitCarrierDetected, false);
                        return;
                    case true when (_io.GetInput(InputIo.InspectionHeatSink1Present) || _io.GetInput(InputIo.InspectionHeatSink2Present))
                        && _io.GetInput(InputIo.InspectionBackupPlateDown)
                        && _io.GetInput(InputIo.InspectionStopperDown)
                        && _io.GetOutput(OutputIo.MainConveyorAvailableToRear)
                        && _io.GetInput(InputIo.MainConveyorReadyFromRear):
                        ClearCarrier(
                            InputIo.InspectionHeatSink1Present,
                            InputIo.InspectionHeatSink2Present);
                        _io.SetInput(InputIo.MainConveyorExitCarrierDetected, true);
                        _ = RespondAsync(
                            responseVersion,
                            () =>
                            {
                                if (_mainConveyorTransferVersion == version
                                    && _io.GetOutput(OutputIo.MainConveyorRun))
                                {
                                    _io.SetInput(InputIo.MainConveyorExitCarrierDetected, false);
                                }
                            });
                        return;
                    case true when (_io.GetInput(InputIo.BoltFasteningHeatSink1Present) || _io.GetInput(InputIo.BoltFasteningHeatSink2Present))
                        && _io.GetInput(InputIo.BoltFasteningBackupPlateDown)
                        && _io.GetInput(InputIo.InspectionBackupPlateDown)
                        && _io.GetInput(InputIo.BoltFasteningStopperDown)
                        && _io.GetInput(InputIo.InspectionStopperUp)
                        && !(_io.GetInput(InputIo.InspectionHeatSink1Present) || _io.GetInput(InputIo.InspectionHeatSink2Present)):
                        MoveCarrier(
                            InputIo.BoltFasteningHeatSink1Present,
                            InputIo.BoltFasteningHeatSink2Present,
                            InputIo.InspectionHeatSink1Present,
                            InputIo.InspectionHeatSink2Present);
                        return;
                    case true when (_io.GetInput(InputIo.PcbPlacementHeatSink1Present) || _io.GetInput(InputIo.PcbPlacementHeatSink2Present))
                        && _io.GetInput(InputIo.PcbPlacementBackupPlateDown)
                        && _io.GetInput(InputIo.BoltFasteningBackupPlateDown)
                        && _io.GetInput(InputIo.PcbPlacementStopperDown)
                        && _io.GetInput(InputIo.BoltFasteningStopperUp)
                        && !(_io.GetInput(InputIo.BoltFasteningHeatSink1Present) || _io.GetInput(InputIo.BoltFasteningHeatSink2Present)):
                        MoveCarrier(
                            InputIo.PcbPlacementHeatSink1Present,
                            InputIo.PcbPlacementHeatSink2Present,
                            InputIo.BoltFasteningHeatSink1Present,
                            InputIo.BoltFasteningHeatSink2Present);
                        return;
                }

                if ((_io.GetInput(InputIo.MainConveyorEntryCarrierDetected)
                    || _io.GetOutput(OutputIo.MainConveyorReadyToFront2)
                    && _io.GetInput(InputIo.MainConveyorAvailableFromFront2))
                    && _io.GetInput(InputIo.PcbPlacementBackupPlateDown)
                    && _io.GetInput(InputIo.PcbPlacementStopperUp)
                    && !(_io.GetInput(InputIo.PcbPlacementHeatSink1Present) || _io.GetInput(InputIo.PcbPlacementHeatSink2Present)))
                {
                    if (!_io.GetInput(InputIo.MainConveyorEntryCarrierDetected))
                    {
                        _io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
                        _io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
                    }

                    _ = RespondAsync(
                        responseVersion,
                        () =>
                        {
                            if (_mainConveyorTransferVersion != version
                                || !_io.GetOutput(OutputIo.MainConveyorRun))
                            {
                                return;
                            }

                            _io.SetInput(InputIo.MainConveyorEntryCarrierDetected, false);
                            // Repeat starts with PCBs already seated on the incoming carrier.
                            var hasPcbs = _incomingCarrierHasPcbs?.Invoke() == true;
                            var carrier = _mainEntryCarrier ?? (
                                HeatSink1: true,
                                HeatSink2: true,
                                Pcb1: hasPcbs,
                                Pcb2: hasPcbs);
                            _mainEntryCarrier = null;
                            _placedPcbs[0] = carrier.Pcb1;
                            _placedPcbs[1] = carrier.Pcb2;
                            _io.SetInputs(
                                (InputIo.PcbPlacementHeatSink1Present, carrier.HeatSink1),
                                (InputIo.PcbPlacementHeatSink2Present, carrier.HeatSink2));
                        });
                }
            });
    }

    private void ReturnMainCarrier(int version)
    {
        // The returning carrier passes every station. There is no reverse stopper.
        foreach (var input in new[]
        {
            InputIo.PcbPlacementBackupPlateDown,
            InputIo.PcbPlacementStopperDown,
            InputIo.BoltFasteningBackupPlateDown,
            InputIo.BoltFasteningStopperDown,
            InputIo.InspectionBackupPlateDown,
            InputIo.InspectionStopperDown,
        })
            if (!_io.GetInput(input))
                return;

        foreach (var (heatSink1, heatSink2) in new[]
        {
            (
                InputIo.InspectionHeatSink1Present,
                InputIo.InspectionHeatSink2Present),
            (
                InputIo.BoltFasteningHeatSink1Present,
                InputIo.BoltFasteningHeatSink2Present),
            (
                InputIo.PcbPlacementHeatSink1Present,
                InputIo.PcbPlacementHeatSink2Present),
        })
        {
            if (!_io.GetInput(heatSink1) && !_io.GetInput(heatSink2))
                continue;
            if (heatSink1 != InputIo.PcbPlacementHeatSink1Present)
            {
                MoveCarrier(
                    heatSink1, heatSink2,
                    InputIo.PcbPlacementHeatSink1Present,
                    InputIo.PcbPlacementHeatSink2Present);
                // Station 1 detects the returning carrier before the front sensor.
                // Continue only if the controller keeps the belt running.
                _ = TransferMainCarrierAsync(version);
                return;
            }

            var pcbs = GetCarrierPcbs(heatSink1);
            _mainEntryCarrier = (_io.GetInput(heatSink1), _io.GetInput(heatSink2), pcbs.Pcb1, pcbs.Pcb2);
            ClearCarrier(heatSink1, heatSink2);
            _io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
            return;
        }
    }

    private Task PresentMainCarrierAsync(int version)
    {
        return RespondAsync(
            _io.AutoResponseVersion,
            () =>
            {
                if (_mainConveyorTransferVersion == version
                    && _io.GetOutput(OutputIo.MainConveyorReadyToFront2)
                    && !_io.GetOutput(OutputIo.MainConveyorRun)
                    && !(_io.GetInput(InputIo.PcbPlacementHeatSink1Present) || _io.GetInput(InputIo.PcbPlacementHeatSink2Present)))
                {
                    _io.SetInput(InputIo.MainConveyorAvailableFromFront2, true);
                }
            });
    }

    private void MoveCarrier(
        InputIo sourceHeatSink1,
        InputIo sourceHeatSink2,
        InputIo destinationHeatSink1,
        InputIo destinationHeatSink2)
    {
        var heatSink1 = _io.GetInput(sourceHeatSink1);
        var heatSink2 = _io.GetInput(sourceHeatSink2);
        var pcbs = GetCarrierPcbs(sourceHeatSink1);
        ClearCarrier(sourceHeatSink1, sourceHeatSink2);
        if (destinationHeatSink1 == InputIo.PcbPlacementHeatSink1Present)
        {
            _placedPcbs[0] = pcbs.Pcb1;
            _placedPcbs[1] = pcbs.Pcb2;
        }
        else
        {
            _carrierPcbs[destinationHeatSink1] = pcbs;
        }
        _io.SetInputs((destinationHeatSink1, heatSink1), (destinationHeatSink2, heatSink2));
    }

    private void ClearCarrier(InputIo heatSink1, InputIo heatSink2)
    {
        _io.SetInputs((heatSink1, false), (heatSink2, false));
    }

    // Simulated material travels with the carrier; real control still reads only sensors.
    private (bool Pcb1, bool Pcb2) GetCarrierPcbs(InputIo stationHeatSink1)
    {
        return stationHeatSink1 == InputIo.PcbPlacementHeatSink1Present
            ? (_placedPcbs[0], _placedPcbs[1])
            : _carrierPcbs.GetValueOrDefault(stationHeatSink1);
    }

    private void ApplyPhysicalOutput(OutputIo output, bool value)
    {
        _io.ApplyAutoResponse(
            _io.AutoResponseVersion,
            () =>
            {
                switch (output)
                {
                    case OutputIo.PcbSupplyGripperClosed:
                        if (value)
                        {
                            if (_io.GetInput(InputIo.PcbSupplyPcbDetected))
                            {
                                _supplyHoldingPcb = true;
                                if (_supplyPickupSlot is { } slot)
                                {
                                    _supplyPcbs[slot] = false;
                                }
                            }
                        }
                        else
                        {
                            _supplyHoldingPcb = false;
                        }
                        UpdateSupplyDetection();
                        UpdatePlacementDetection();
                        break;
                    case OutputIo.PcbPlacementIpmGripperClose:
                        if (value
                            && _io.GetInput(InputIo.PcbPlacementPcbDetected)
                            && _io.GetInput(InputIo.PcbPlacementVacuumDetected))
                        {
                            _placementHoldingPcb = true;
                            if (_placementHeatSink is { } slot)
                                _placedPcbs[slot] = false;
                        }
                        else if (!value && _placementHoldingPcb)
                        {
                            if (_placementHeatSink is { } slot)
                                _placedPcbs[slot] = true;

                            _placementHoldingPcb = false;
                        }
                        UpdateSupplyDetection();
                        UpdatePlacementDetection();
                        break;

                    case OutputIo.PcbPlacementHandlerDown:
                        UpdatePlacementDetection();
                        break;

                    case OutputIo.ShootingEscapeForward when value:
                        _io.SetInput(InputIo.ShootingFeederBoltDetected, false);
                        break;
                    case OutputIo.NgCarrierPickupDown when _ngCarrierHeld:
                        if (_inspectionAtNgShuttle)
                            _io.SetInput(InputIo.NgShuttleCarrierDetected, value);
                        else if (_inspectionAtNgPickup)
                            _io.SetInputs(
                                (InputIo.InspectionHeatSink1Present, value && _ngCarrierHeatSink1),
                                (InputIo.InspectionHeatSink2Present, value && _ngCarrierHeatSink2));
                        break;
                    case OutputIo.NgCarrierGripperClose:
                        if (value
                            && !_ngCarrierHeld
                            && _inspectionAtNgPickup
                            && _io.GetInput(InputIo.NgCarrierPickupDown)
                            && _io.GetInput(InputIo.InspectionBackupPlateUp)
                            && (_io.GetInput(InputIo.InspectionHeatSink1Present) || _io.GetInput(InputIo.InspectionHeatSink2Present)))
                        {
                            _ngCarrierHeld = true;
                            _ngCarrierHeatSink1 = _io.GetInput(InputIo.InspectionHeatSink1Present);
                            _ngCarrierHeatSink2 = _io.GetInput(InputIo.InspectionHeatSink2Present);
                            _ngCarrierPcbs = GetCarrierPcbs(InputIo.InspectionHeatSink1Present);
                            _io.SetInput(InputIo.NgCarrierDetected, true);
                            ClearCarrier(InputIo.InspectionHeatSink1Present, InputIo.InspectionHeatSink2Present);
                        }
                        else if (value
                            && !_ngCarrierHeld
                            && _inspectionAtNgShuttle
                            && _io.GetInput(InputIo.NgCarrierPickupDown)
                            && _io.GetInput(InputIo.NgShuttleUp)
                            && _io.GetInput(InputIo.NgShuttleCarrierDetected))
                        {
                            _ngCarrierHeld = true;
                            _io.SetInput(InputIo.NgCarrierDetected, true);
                            _io.SetInput(InputIo.NgShuttleCarrierDetected, false);
                        }
                        else if (!value
                            && _ngCarrierHeld
                            && _inspectionAtNgShuttle
                            && _io.GetInput(InputIo.NgCarrierPickupDown)
                            && _io.GetInput(InputIo.NgShuttleUp))
                        {
                            _ngCarrierHeld = false;
                            _io.SetInput(InputIo.NgCarrierDetected, false);
                            _io.SetInput(InputIo.NgShuttleCarrierDetected, true);
                        }
                        else if (!value
                            && _ngCarrierHeld
                            && _inspectionAtNgPickup
                            && _io.GetInput(InputIo.NgCarrierPickupDown)
                            && _io.GetInput(InputIo.InspectionBackupPlateUp))
                        {
                            _ngCarrierHeld = false;
                            _io.SetInput(InputIo.NgCarrierDetected, false);
                            _carrierPcbs[InputIo.InspectionHeatSink1Present] = _ngCarrierPcbs;
                            _io.SetInputs(
                                (InputIo.InspectionHeatSink1Present, _ngCarrierHeatSink1),
                                (InputIo.InspectionHeatSink2Present, _ngCarrierHeatSink2));
                        }

                        break;
                }
            });
    }

    private Task TransferNgCarrierAsync(int version)
    {
        return RespondAsync(
            _io.AutoResponseVersion,
            () =>
            {
                if (_ngConveyorVersion != version || !_io.GetOutput(OutputIo.NgConveyorRun))
                {
                    return;
                }

                var position1 = _io.GetInput(InputIo.NgConveyorPosition1Occupied);
                var position2 = _io.GetInput(InputIo.NgConveyorPosition2Occupied);
                var position3 = _io.GetInput(InputIo.NgShuttleCarrierDetected)
                    && _io.GetInput(InputIo.NgShuttleDown)
                    && !_io.GetInput(InputIo.NgShuttleUp);

                switch (true)
                {
                    case true when _io.GetOutput(OutputIo.NgConveyorReverse):
                        if (_io.GetInput(InputIo.NgShuttleDown)
                            && !_io.GetInput(InputIo.NgShuttleUp)
                            && !position3
                            && (position1 || position2))
                        {
                            _io.SetInput(InputIo.NgConveyorPosition1Occupied, false);
                            _io.SetInput(InputIo.NgConveyorPosition2Occupied, false);
                            _io.SetInput(InputIo.NgShuttleCarrierDetected, true);
                        }

                        return;
                    case true when _io.GetInput(InputIo.NgConveyorStopperDown):
                        if (position1)
                        {
                            _io.SetInput(InputIo.NgConveyorPosition1Occupied, false);
                        }

                        return;
                    case true when !position1 && position2:
                        _io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
                        _io.SetInput(InputIo.NgConveyorPosition2Occupied, position3);
                        if (position3)
                        {
                            _io.SetInput(InputIo.NgShuttleCarrierDetected, false);
                        }

                        return;
                    case true when !position1 && position3:
                        _io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
                        _io.SetInput(InputIo.NgShuttleCarrierDetected, false);
                        return;
                }

                if (position1 && !position2 && position3)
                {
                    _io.SetInput(InputIo.NgConveyorPosition2Occupied, true);
                    _io.SetInput(InputIo.NgShuttleCarrierDetected, false);
                }
            });
    }

    private async Task RespondAsync(int responseVersion, Action response)
    {
        await Task.Delay(TransferDelayMilliseconds).ConfigureAwait(false);
        _io.ApplyAutoResponse(responseVersion, response);
    }

    private static bool IsAt(double x, double y, AxisPosition position)
    {
        return Math.Abs(x - position.X) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(y - position.Y) <= MotionService.PositionToleranceMillimeters;
    }

    private static bool IsAt(double x, double y, double z, AxisPosition position)
    {
        return IsAt(x, y, z, position.X, position.Y, position.Z);
    }

    private static bool IsAt(
        double x,
        double y,
        double z,
        double targetX,
        double targetY,
        double targetZ)
    {
        return Math.Abs(x - targetX) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(y - targetY) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(z - targetZ) <= MotionService.PositionToleranceMillimeters;
    }
}
