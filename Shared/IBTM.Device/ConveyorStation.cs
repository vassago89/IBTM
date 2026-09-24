using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;

namespace IBTM.Device;

public enum StationCylinderState
{
    [Description("Up")]
    Up,

    [Description("Between")]
    Between,

    [Description("Down")]
    Down,
}

public sealed class ConveyorStation
{
    private readonly IIoService _io;
    // Notification history only; CarrierPresent always reads the current inputs.
    private bool? _lastNotifiedPresence;
    private readonly InputIo _backupPlateUp;
    private readonly InputIo _backupPlateDown;
    private readonly InputIo _stopperUp;
    private readonly InputIo _stopperDown;
    private readonly InputIo _heatSink1;
    private readonly OutputIo _backupPlate;
    private readonly OutputIo _stopper;

    private ConveyorStation(
        IIoService io,
        InputIo backupPlateUp,
        InputIo backupPlateDown,
        InputIo stopperUp,
        InputIo stopperDown,
        InputIo heatSink1,
        InputIo heatSink2,
        OutputIo backupPlate,
        OutputIo stopper)
    {
        _io = io;
        _backupPlateUp = backupPlateUp;
        _backupPlateDown = backupPlateDown;
        _stopperUp = stopperUp;
        _stopperDown = stopperDown;
        _heatSink1 = heatSink1;
        HeatSink2Input = heatSink2;
        _backupPlate = backupPlate;
        _stopper = stopper;
        io.InputChanged += OnInputChanged;
    }

    public InputIo HeatSink2Input { get; }

    public event Action? Changed;
    public event Action<bool>? CarrierChanged;

    public bool CarrierPresent
    {
        get
        {
            var present = _io.GetInput(_heatSink1) || _io.GetInput(HeatSink2Input);
            _lastNotifiedPresence ??= present;
            return present;
        }
    }

    public StationCylinderState BackupPlate
    {
        get
        {
            switch ((_io.GetInput(_backupPlateUp), _io.GetInput(_backupPlateDown)))
            {
                case (true, false):
                    return StationCylinderState.Up;
                case (false, true):
                    return StationCylinderState.Down;
                default:
                    return StationCylinderState.Between;
            }
        }
    }

    public StationCylinderState Stopper
    {
        get
        {
            switch ((_io.GetInput(_stopperUp), _io.GetInput(_stopperDown)))
            {
                case (true, false):
                    return StationCylinderState.Up;
                case (false, true):
                    return StationCylinderState.Down;
                default:
                    return StationCylinderState.Between;
            }
        }
    }

    public bool CarrierSeated
    {
        get
        {
            return CarrierPresent
                && BackupPlate == StationCylinderState.Up;
        }
    }

    public static ConveyorStation CreatePcbPlacement(IIoService io)
    {
        return new(
            io,
            InputIo.PcbPlacementBackupPlateUp,
            InputIo.PcbPlacementBackupPlateDown,
            InputIo.PcbPlacementStopperUp,
            InputIo.PcbPlacementStopperDown,
            InputIo.PcbPlacementHeatSink1Present,
            InputIo.PcbPlacementHeatSink2Present,
            OutputIo.PcbPlacementBackupPlateUp,
            OutputIo.PcbPlacementStopperUp);
    }

    public static ConveyorStation CreateBoltFastening(IIoService io)
    {
        return new(
            io,
            InputIo.BoltFasteningBackupPlateUp,
            InputIo.BoltFasteningBackupPlateDown,
            InputIo.BoltFasteningStopperUp,
            InputIo.BoltFasteningStopperDown,
            InputIo.BoltFasteningHeatSink1Present,
            InputIo.BoltFasteningHeatSink2Present,
            OutputIo.BoltFasteningBackupPlateUp,
            OutputIo.BoltFasteningStopperUp);
    }

    public static ConveyorStation CreateInspection(IIoService io)
    {
        return new(
            io,
            InputIo.InspectionBackupPlateUp,
            InputIo.InspectionBackupPlateDown,
            InputIo.InspectionStopperUp,
            InputIo.InspectionStopperDown,
            InputIo.InspectionHeatSink1Present,
            InputIo.InspectionHeatSink2Present,
            OutputIo.InspectionBackupPlateUp,
            OutputIo.InspectionStopperUp);
    }

    public IoStatus CreateIoStatus(HardwareArea area, IoSignals io)
    {
        return io.Select(
            area,
            [
                _backupPlateUp,
                _backupPlateDown,
                _stopperUp,
                _stopperDown,
                _heatSink1,
                HeatSink2Input,
            ],
            [_backupPlate, _stopper]);
    }

    public bool IsHeatSinkPresent(HeatSinkSlot heatSink)
    {
        return _io.GetInput(heatSink == HeatSinkSlot.HeatSink1 ? _heatSink1 : HeatSink2Input);
    }

    public Task PrepareToReceiveAsync(CancellationToken cancellationToken)
    {
        return Task.WhenAll(
            _io.SetOutputAndWaitAsync(_stopper, true, cancellationToken),
            _io.SetOutputAndWaitAsync(_backupPlate, false, cancellationToken));
    }

    public Task ReleaseAsync(CancellationToken cancellationToken)
    {
        return Task.WhenAll(
            _io.SetOutputAndWaitAsync(_stopper, false, cancellationToken),
            _io.SetOutputAndWaitAsync(_backupPlate, false, cancellationToken));
    }

    public async Task SeatAsync(CancellationToken cancellationToken)
    {
        await _io.SetOutputAndWaitAsync(_stopper, true, cancellationToken);
        await RaisePlateAndLowerStopperAsync(cancellationToken);
    }

    public async Task RaisePlateAndLowerStopperAsync(CancellationToken cancellationToken)
    {
        await _io.SetOutputAndWaitAsync(_backupPlate, true, cancellationToken);
        await _io.SetOutputAndWaitAsync(_stopper, false, cancellationToken);
    }

    public async Task WaitForCarrierAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCarrierChanged(bool present)
        {
            if (present)
                arrived.TrySetResult();
        }

        CarrierChanged += OnCarrierChanged;
        try
        {
            if (CarrierPresent)
                return;
            await arrived.Task.WaitAsync(TimeSpan.FromMilliseconds(_io.TimeoutMilliseconds), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"Carrier arrival requires {_heatSink1} or {HeatSink2Input}=ON "
                    + $"within {_io.TimeoutMilliseconds} ms.",
                exception);
        }
        finally
        {
            CarrierChanged -= OnCarrierChanged;
        }
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input == _heatSink1 || input == HeatSink2Input)
        {
            var previous = _lastNotifiedPresence;
            var present = _io.GetInput(_heatSink1) || _io.GetInput(HeatSink2Input);
            _lastNotifiedPresence = present;
            if (previous != present)
                CarrierChanged?.Invoke(present);
        }

        if (input == _backupPlateUp
            || input == _backupPlateDown
            || input == _stopperUp
            || input == _stopperDown
            || input == _heatSink1
            || input == HeatSink2Input)
        {
            Changed?.Invoke();
        }
    }
}
