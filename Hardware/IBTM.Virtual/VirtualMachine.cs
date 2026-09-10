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
    private readonly bool[] _supplyPcbs = new bool[2];
    private int _supplySmemaVersion;
    private int _mainConveyorTransferVersion;
    private (bool HeatSink1, bool HeatSink2, bool Pcb1, bool Pcb2)? _mainEntryCarrier;
    private readonly Dictionary<InputIo, (bool Pcb1, bool Pcb2)> _carrierPcbs = [];
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
    private readonly bool[] _placedPcbs = new bool[2];
    private int? _placementHeatSink;
    private bool _inspectionAtNgPickup;
    private bool _inspectionAtNgShuttle;
    private bool _ngCarrierHeld;
    private bool _ngCarrierHeatSink1;
    private bool _ngCarrierHeatSink2;

    public VirtualMachine(VirtualIoService io, IReadOnlyList<VirtualMotionService> motions)
    {
        _io = io;
        _motions = motions;
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

    private void OnIoPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VirtualIoService.AutoResponseEnabled))
            SynchronizeGrip();
    }

    private bool AutoMode
    {
        get
        {
            return !_io.GetInput(InputIo.AutoMode);
        }
    }

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

    private void SynchronizeGrip()
    {
        _io.ApplyAutoResponse(
            _io.AutoResponseVersion,
            () =>
            {
                _supplyHoldingPcb = _io.GetInput(InputIo.PcbSupplyPcbDetected)
                    && _io.GetInput(InputIo.PcbSupplyGripperClosed)
                    && !_io.GetInput(InputIo.PcbSupplyGripperOpen)
                    && _io.GetInput(InputIo.PcbSupplyIpmFixerForward)
                    && !_io.GetInput(InputIo.PcbSupplyIpmFixerBackward);
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
        if (input == InputIo.PcbPlacementCarrierPresent && !value)
            Array.Clear(_placedPcbs);
        if (!value
            && input is InputIo.BoltFasteningCarrierPresent or InputIo.InspectionCarrierPresent)
            _carrierPcbs.Remove(input);
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
        AxisPosition buffer)
    {
        var wasAtBuffer = _supplyAtBuffer;
        _supplyAtBuffer = IsAt(x, y, z, buffer);
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
                if (_supplyHoldingPcb)
                {
                    if (_supplyAtBuffer)
                    {
                        _io.SetInput(InputIo.PcbBufferPcbPresent, true);
                    }
                    else if (wasAtBuffer && !_placementAtBuffer)
                    {
                        _io.SetInput(InputIo.PcbBufferPcbPresent, false);
                    }

                    return;
                }

                _io.SetInput(
                    InputIo.PcbSupplyPcbDetected,
                    _supplyAtBuffer
                        && _io.GetInput(InputIo.PcbBufferPcbPresent)
                        || _supplyPickupSlot is { } slot
                        && _supplyPcbs[slot]);
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
        var wasAtBuffer = _placementAtBuffer;
        _placementAtBuffer = IsAt(x, y, z, bufferPosition);
        _placementHeatSink = heatSink1 is not null && IsAt(x, y, z, heatSink1)
            ? 0
            : heatSink2 is not null && IsAt(x, y, z, heatSink2) ? 1 : null;
        _io.ApplyAutoResponse(
            _io.AutoResponseVersion,
            () =>
            {
                if (_placementAtBuffer && _placementHoldingPcb)
                    _io.SetInput(InputIo.PcbBufferPcbPresent, true);
                if (wasAtBuffer && !_placementAtBuffer && _placementHoldingPcb)
                {
                    _io.SetInput(InputIo.PcbBufferPcbPresent, false);
                }

                UpdatePlacementDetection();
            });
    }

    private void UpdatePlacementDetection()
    {
        _io.SetInput(
            InputIo.PcbPlacementPcbDetected,
            _placementHoldingPcb
                || _placementAtBuffer
                && _io.GetInput(InputIo.PcbBufferPcbPresent)
                || _placementHeatSink is { } slot
                && _placedPcbs[slot]
                && _io.GetInput(InputIo.PcbPlacementHandlerDown));
    }

    public void UpdateInspectionPosition(
        double x,
        double y,
        AxisPosition pickupPosition,
        AxisPosition shuttlePosition)
    {
        _inspectionAtNgPickup = IsAt(x, y, pickupPosition);
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
            && !_io.GetInput(InputIo.PcbPlacementCarrierPresent))
        {
            _ = PresentMainCarrierAsync(_mainConveyorTransferVersion);
        }

        if (output == OutputIo.PcbPlacementVacuumEjector)
        {
            var vacuumVersion = Interlocked.Increment(ref _placementVacuumVersion);
            _ = ApplyPlacementVacuumAsync(value, vacuumVersion);
            return;
        }

        if (output == OutputIo.PickupHeadVacuumPump)
        {
            var vacuumVersion = Interlocked.Increment(ref _pickupVacuumVersion);
            _ = ApplyPickupVacuumAsync(value, vacuumVersion);
            return;
        }

        if (output == OutputIo.ShootingHeadVacuumPump)
        {
            var vacuumVersion = Interlocked.Increment(ref _shootingVacuumVersion);
            if (!value)
            {
                _ = ReleaseShootingVacuumAsync(vacuumVersion);
            }

            return;
        }

        if (output == OutputIo.ShootBolt)
        {
            var shootVersion = Interlocked.Increment(ref _shootVersion);
            if (value)
            {
                _ = ApplyShootAsync(shootVersion);
            }

            return;
        }

        if (output == OutputIo.ShootingFeederRunSignal)
        {
            var feederVersion = Interlocked.Increment(ref _shootingFeederVersion);
            if (value)
            {
                _ = FeedShootingBoltAsync(feederVersion);
            }

            return;
        }

        if (output == OutputIo.NgConveyorRun)
        {
            var conveyorVersion = Interlocked.Increment(ref _ngConveyorVersion);
            if (value)
            {
                _ = TransferNgCarrierAsync(conveyorVersion);
            }

            return;
        }

        if (output != OutputIo.PcbSupplyReadyToFront1)
        {
            return;
        }

        var version = Interlocked.Increment(ref _supplySmemaVersion);
        if (value)
        {
            _ = TransferSupplyCarrierAsync(version);
        }
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
                if (_shootVersion != version)
                {
                    return;
                }

                if (!_io.GetInput(InputIo.ShootingEscapeForward)
                    || !_io.GetOutput(OutputIo.ShootingHeadVacuumPump))
                {
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

    private Task TransferSupplyCarrierAsync(int version)
    {
        var responseVersion = _io.AutoResponseVersion;
        void PresentCarrier()
        {
            if (!SupplyReady(version))
            {
                return;
            }

            _supplyPcbs[0] = true;
            _supplyPcbs[1] = true;
            _io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
        }

        if (_io.GetInput(InputIo.PcbSupplyAvailableFromFront1))
        {
            return RespondAsync(
                responseVersion,
                () =>
                {
                    if (!SupplyReady(version))
                    {
                        return;
                    }

                    _io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
                    _ = RespondAsync(responseVersion, PresentCarrier);
                });
        }

        return RespondAsync(responseVersion, PresentCarrier);
    }

    private Task TransferMainCarrierAsync(int version)
    {
        var responseVersion = _io.AutoResponseVersion;
        return RespondAsync(
            responseVersion,
            () =>
            {
                if (_mainConveyorTransferVersion != version
                    || !_io.GetOutput(OutputIo.MainConveyorRun))
                {
                    return;
                }

                if (_io.GetOutput(OutputIo.MainConveyorReverse))
                {
                    ReturnMainCarrier(version);
                    return;
                }

                if (_io.GetInput(InputIo.MainConveyorExitCarrierDetected))
                {
                    _io.SetInput(InputIo.MainConveyorExitCarrierDetected, false);
                    return;
                }

                if (_io.GetInput(InputIo.InspectionCarrierPresent)
                    && _io.GetInput(InputIo.InspectionBackupPlateDown)
                    && _io.GetInput(InputIo.InspectionStopperDown)
                    && _io.GetOutput(OutputIo.MainConveyorAvailableToRear)
                    && _io.GetInput(InputIo.MainConveyorReadyFromRear))
                {
                    ClearCarrier(
                        InputIo.InspectionCarrierPresent,
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
                }

                if (_io.GetInput(InputIo.BoltFasteningCarrierPresent)
                    && _io.GetInput(InputIo.BoltFasteningBackupPlateDown)
                    && _io.GetInput(InputIo.InspectionBackupPlateDown)
                    && _io.GetInput(InputIo.BoltFasteningStopperDown)
                    && _io.GetInput(InputIo.InspectionStopperUp)
                    && !_io.GetInput(InputIo.InspectionCarrierPresent))
                {
                    MoveCarrier(
                        InputIo.BoltFasteningCarrierPresent,
                        InputIo.BoltFasteningHeatSink1Present,
                        InputIo.BoltFasteningHeatSink2Present,
                        InputIo.InspectionCarrierPresent,
                        InputIo.InspectionHeatSink1Present,
                        InputIo.InspectionHeatSink2Present);
                    return;
                }

                if (_io.GetInput(InputIo.PcbPlacementCarrierPresent)
                    && _io.GetInput(InputIo.PcbPlacementBackupPlateDown)
                    && _io.GetInput(InputIo.BoltFasteningBackupPlateDown)
                    && _io.GetInput(InputIo.PcbPlacementStopperDown)
                    && _io.GetInput(InputIo.BoltFasteningStopperUp)
                    && !_io.GetInput(InputIo.BoltFasteningCarrierPresent))
                {
                    MoveCarrier(
                        InputIo.PcbPlacementCarrierPresent,
                        InputIo.PcbPlacementHeatSink1Present,
                        InputIo.PcbPlacementHeatSink2Present,
                        InputIo.BoltFasteningCarrierPresent,
                        InputIo.BoltFasteningHeatSink1Present,
                        InputIo.BoltFasteningHeatSink2Present);
                    return;
                }

                if ((_io.GetInput(InputIo.MainConveyorEntryCarrierDetected)
                    || _io.GetOutput(OutputIo.MainConveyorReadyToFront2)
                    && _io.GetInput(InputIo.MainConveyorAvailableFromFront2))
                    && _io.GetInput(InputIo.PcbPlacementBackupPlateDown)
                    && _io.GetInput(InputIo.PcbPlacementStopperUp)
                    && !_io.GetInput(InputIo.PcbPlacementCarrierPresent))
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
                            var carrier = _mainEntryCarrier ?? (
                                HeatSink1: true,
                                HeatSink2: true,
                                Pcb1: false,
                                Pcb2: false);
                            _mainEntryCarrier = null;
                            _placedPcbs[0] = carrier.Pcb1;
                            _placedPcbs[1] = carrier.Pcb2;
                            _io.SetInput(InputIo.PcbPlacementHeatSink1Present, carrier.HeatSink1);
                            _io.SetInput(InputIo.PcbPlacementHeatSink2Present, carrier.HeatSink2);
                            _io.SetInput(InputIo.PcbPlacementCarrierPresent, true);
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

        foreach (var (carrier, heatSink1, heatSink2) in new[]
        {
            (
                InputIo.InspectionCarrierPresent,
                InputIo.InspectionHeatSink1Present,
                InputIo.InspectionHeatSink2Present),
            (
                InputIo.BoltFasteningCarrierPresent,
                InputIo.BoltFasteningHeatSink1Present,
                InputIo.BoltFasteningHeatSink2Present),
            (
                InputIo.PcbPlacementCarrierPresent,
                InputIo.PcbPlacementHeatSink1Present,
                InputIo.PcbPlacementHeatSink2Present),
        })
        {
            if (!_io.GetInput(carrier))
                continue;
            if (carrier != InputIo.PcbPlacementCarrierPresent)
            {
                MoveCarrier(
                    carrier, heatSink1, heatSink2,
                    InputIo.PcbPlacementCarrierPresent,
                    InputIo.PcbPlacementHeatSink1Present,
                    InputIo.PcbPlacementHeatSink2Present);
                // Station 1 detects the returning carrier before the front sensor.
                // Continue only if the controller keeps the belt running.
                _ = TransferMainCarrierAsync(version);
                return;
            }

            var pcbs = CarrierPcbs(carrier);
            _mainEntryCarrier = (_io.GetInput(heatSink1), _io.GetInput(heatSink2), pcbs.Pcb1, pcbs.Pcb2);
            ClearCarrier(carrier, heatSink1, heatSink2);
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
                    && !_io.GetInput(InputIo.PcbPlacementCarrierPresent))
                {
                    _io.SetInput(InputIo.MainConveyorAvailableFromFront2, true);
                }
            });
    }

    private void MoveCarrier(
        InputIo sourceCarrier,
        InputIo sourceHeatSink1,
        InputIo sourceHeatSink2,
        InputIo destinationCarrier,
        InputIo destinationHeatSink1,
        InputIo destinationHeatSink2)
    {
        var heatSink1 = _io.GetInput(sourceHeatSink1);
        var heatSink2 = _io.GetInput(sourceHeatSink2);
        var pcbs = CarrierPcbs(sourceCarrier);
        ClearCarrier(sourceCarrier, sourceHeatSink1, sourceHeatSink2);
        if (destinationCarrier == InputIo.PcbPlacementCarrierPresent)
        {
            _placedPcbs[0] = pcbs.Pcb1;
            _placedPcbs[1] = pcbs.Pcb2;
        }
        else
        {
            _carrierPcbs[destinationCarrier] = pcbs;
        }
        _io.SetInput(destinationHeatSink1, heatSink1);
        _io.SetInput(destinationHeatSink2, heatSink2);
        _io.SetInput(destinationCarrier, true);
    }

    private void ClearCarrier(InputIo carrier, InputIo heatSink1, InputIo heatSink2)
    {
        _io.SetInput(carrier, false);
        _io.SetInput(heatSink1, false);
        _io.SetInput(heatSink2, false);
    }

    // Simulated material travels with the carrier; real control still reads only sensors.
    private (bool Pcb1, bool Pcb2) CarrierPcbs(InputIo carrier)
    {
        return carrier == InputIo.PcbPlacementCarrierPresent
            ? (_placedPcbs[0], _placedPcbs[1])
            : _carrierPcbs.GetValueOrDefault(carrier);
    }

    private void ApplyPhysicalOutput(OutputIo output, bool value)
    {
        _io.ApplyAutoResponse(
            _io.AutoResponseVersion,
            () =>
            {
                switch (output)
                {
                    case OutputIo.PcbSupplyIpmFixerForward:
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
                        else if (_supplyHoldingPcb)
                        {
                            _supplyHoldingPcb = false;
                        }

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
                            if (_placementAtBuffer)
                            {
                                _io.SetInput(InputIo.PcbBufferPcbPresent, true);
                            }

                            if (_placementHeatSink is { } slot)
                                _placedPcbs[slot] = true;

                            _placementHoldingPcb = false;
                            _io.SetInput(
                                InputIo.PcbPlacementPcbDetected,
                                _placementAtBuffer
                                    || _io.GetInput(InputIo.PcbPlacementHandlerDown));
                        }

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
                            _io.SetInput(InputIo.InspectionCarrierPresent, value);
                        break;
                    case OutputIo.NgCarrierGripperClose:
                        if (value
                            && !_ngCarrierHeld
                            && _inspectionAtNgPickup
                            && _io.GetInput(InputIo.NgCarrierPickupDown)
                            && _io.GetInput(InputIo.InspectionBackupPlateUp)
                            && _io.GetInput(InputIo.InspectionCarrierPresent))
                        {
                            _ngCarrierHeld = true;
                            _ngCarrierHeatSink1 = _io.GetInput(InputIo.InspectionHeatSink1Present);
                            _ngCarrierHeatSink2 = _io.GetInput(InputIo.InspectionHeatSink2Present);
                            _io.SetInput(InputIo.NgCarrierDetected, true);
                            _io.SetInput(InputIo.InspectionCarrierPresent, false);
                            _io.SetInput(InputIo.InspectionHeatSink1Present, false);
                            _io.SetInput(InputIo.InspectionHeatSink2Present, false);
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
                            _io.SetInput(InputIo.InspectionCarrierPresent, true);
                            _io.SetInput(InputIo.InspectionHeatSink1Present, _ngCarrierHeatSink1);
                            _io.SetInput(InputIo.InspectionHeatSink2Present, _ngCarrierHeatSink2);
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

                if (_io.GetOutput(OutputIo.NgConveyorReverse))
                {
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
                }

                if (_io.GetInput(InputIo.NgConveyorStopperDown))
                {
                    if (position1)
                    {
                        _io.SetInput(InputIo.NgConveyorPosition1Occupied, false);
                    }

                    return;
                }

                if (!position1 && position2)
                {
                    _io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
                    _io.SetInput(InputIo.NgConveyorPosition2Occupied, position3);
                    if (position3)
                    {
                        _io.SetInput(InputIo.NgShuttleCarrierDetected, false);
                    }

                    return;
                }

                if (!position1 && position3)
                {
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

    private bool SupplyReady(int version)
    {
        return _supplySmemaVersion == version
            && _io.GetOutput(OutputIo.PcbSupplyReadyToFront1);
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
