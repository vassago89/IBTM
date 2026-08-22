using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Virtual;

public sealed class VirtualMachine
{
    private const int TransferDelayMilliseconds = 200;

    private readonly VirtualIoService _io;
    private readonly bool[] _supplyPcbs = new bool[2];
    private int _supplySmemaVersion;
    private int _placementVacuumVersion;
    private int? _supplyPickupSlot;
    private bool _supplyAtBuffer;
    private bool _supplyHoldingPcb;
    private bool _placementAtBuffer;
    private bool _placementHoldingPcb;

    public VirtualMachine(VirtualIoService io)
    {
        _io = io;
        io.OutputChanged += OnOutputChanged;
        io.OutputApplied += ApplyPhysicalOutput;
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, true);
        io.SetInput(InputIo.MainConveyorReadyFromRear, true);
    }

    public void UpdateSupplyPosition(
        double x,
        double y,
        double z,
        double carrierY,
        (double X, double Z) pcb1,
        (double X, double Z) pcb2,
        AxisPos buffer)
    {
        _supplyAtBuffer = IsAt(x, y, z, buffer);
        if (_supplyAtBuffer && _supplyHoldingPcb)
        {
            _io.SetInput(InputIo.PcbBufferPcbPresent, true);
        }

        if (_supplyHoldingPcb)
        {
            return;
        }

        _supplyPickupSlot = IsAt(x, y, z, pcb1.X, carrierY, pcb1.Z)
            ? 0
            : IsAt(x, y, z, pcb2.X, carrierY, pcb2.Z)
                ? 1
                : null;
        _io.SetInput(
            InputIo.PcbSupplyPcbDetected,
            _supplyPickupSlot is { } slot && _supplyPcbs[slot]);
    }

    public void UpdatePlacementPosition(
        double x,
        double y,
        double z,
        AxisPos bufferPosition)
    {
        var wasAtBuffer = _placementAtBuffer;
        _placementAtBuffer = IsAt(x, y, z, bufferPosition);
        if (wasAtBuffer && !_placementAtBuffer && _placementHoldingPcb)
        {
            _io.SetInput(InputIo.PcbBufferPcbPresent, false);
        }

        _io.SetInput(
            InputIo.PcbPlacementPcbDetected,
            _placementHoldingPcb
            || _placementAtBuffer
            && _io.GetInput(InputIo.PcbBufferPcbPresent));
    }

    private void OnOutputChanged(OutputIo output, bool value)
    {
        if (output == OutputIo.PcbPlacementVacuumEjector)
        {
            var vacuumVersion = Interlocked.Increment(
                ref _placementVacuumVersion);
            _ = ApplyPlacementVacuumAsync(value, vacuumVersion);
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

    private async Task ApplyPlacementVacuumAsync(bool value, int version)
    {
        await Task.Delay(TransferDelayMilliseconds).ConfigureAwait(false);
        if (_placementVacuumVersion != version)
        {
            return;
        }

        _io.SetInput(
            InputIo.PcbPlacementVacuumDetected,
            value
            && _placementAtBuffer
            && _io.GetInput(InputIo.PcbBufferPcbPresent));
    }

    private async Task TransferSupplyCarrierAsync(int version)
    {
        if (_io.GetInput(InputIo.PcbSupplyAvailableFromFront1))
        {
            await Task.Delay(TransferDelayMilliseconds)
                .ConfigureAwait(false);
            if (!SupplyReady(version))
            {
                return;
            }

            _io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
        }

        await Task.Delay(TransferDelayMilliseconds)
            .ConfigureAwait(false);
        if (!SupplyReady(version))
        {
            return;
        }

        _supplyPcbs[0] = true;
        _supplyPcbs[1] = true;
        _io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
    }

    private void ApplyPhysicalOutput(OutputIo output, bool value)
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
                }
                else if (!value && _placementHoldingPcb)
                {
                    if (_placementAtBuffer)
                    {
                        _io.SetInput(InputIo.PcbBufferPcbPresent, true);
                    }

                    _placementHoldingPcb = false;
                    _io.SetInput(
                        InputIo.PcbPlacementPcbDetected,
                        _placementAtBuffer);
                }
                break;
            case OutputIo.NgCarrierGripperClose
                when value
                     && _io.GetInput(InputIo.InspectionBackupPlateUp)
                     && _io.GetInput(InputIo.InspectionCarrierJigPresent):
                _io.SetInput(InputIo.InspectionCarrierJigPresent, false);
                _io.SetInput(InputIo.InspectionHousing1Present, false);
                _io.SetInput(InputIo.InspectionHousing2Present, false);
                break;
        }
    }

    private bool SupplyReady(int version) =>
        _supplySmemaVersion == version
        && _io.GetOutput(OutputIo.PcbSupplyReadyToFront1);

    private static bool IsAt(
        double x,
        double y,
        double z,
        AxisPos position) =>
        IsAt(x, y, z, position.X, position.Y, position.Z);

    private static bool IsAt(
        double x,
        double y,
        double z,
        double targetX,
        double targetY,
        double targetZ) =>
        Math.Abs(x - targetX) <= 0.05
        && Math.Abs(y - targetY) <= 0.05
        && Math.Abs(z - targetZ) <= 0.05;
}
