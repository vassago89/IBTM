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
    private readonly InputIo _carrier;
    private readonly InputIo _backupPlateUp;
    private readonly InputIo _backupPlateDown;
    private readonly InputIo _stopperUp;
    private readonly InputIo _stopperDown;
    private readonly InputIo _heatSink1;
    private readonly InputIo _heatSink2;
    private readonly OutputIo _backupPlate;
    private readonly OutputIo _stopper;

    private ConveyorStation(
        IIoService io,
        InputIo carrier,
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
        _carrier = carrier;
        _backupPlateUp = backupPlateUp;
        _backupPlateDown = backupPlateDown;
        _stopperUp = stopperUp;
        _stopperDown = stopperDown;
        _heatSink1 = heatSink1;
        _heatSink2 = heatSink2;
        _backupPlate = backupPlate;
        _stopper = stopper;
        io.InputChanged += OnInputChanged;
    }

    internal event Action? Changed;
    public event Action<bool>? CarrierChanged;

    public static ConveyorStation PcbPlacement(IIoService io) => new(
        io,
        InputIo.PcbPlacementCarrierPresent,
        InputIo.PcbPlacementBackupPlateUp,
        InputIo.PcbPlacementBackupPlateDown,
        InputIo.PcbPlacementStopperUp,
        InputIo.PcbPlacementStopperDown,
        InputIo.PcbPlacementHeatSink1Present,
        InputIo.PcbPlacementHeatSink2Present,
        OutputIo.PcbPlacementBackupPlateUp,
        OutputIo.PcbPlacementStopperUp);

    public static ConveyorStation BoltFastening(IIoService io) => new(
        io,
        InputIo.BoltFasteningCarrierPresent,
        InputIo.BoltFasteningBackupPlateUp,
        InputIo.BoltFasteningBackupPlateDown,
        InputIo.BoltFasteningStopperUp,
        InputIo.BoltFasteningStopperDown,
        InputIo.BoltFasteningHeatSink1Present,
        InputIo.BoltFasteningHeatSink2Present,
        OutputIo.BoltFasteningBackupPlateUp,
        OutputIo.BoltFasteningStopperUp);

    public static ConveyorStation Inspection(IIoService io) => new(
        io,
        InputIo.InspectionCarrierPresent,
        InputIo.InspectionBackupPlateUp,
        InputIo.InspectionBackupPlateDown,
        InputIo.InspectionStopperUp,
        InputIo.InspectionStopperDown,
        InputIo.InspectionHeatSink1Present,
        InputIo.InspectionHeatSink2Present,
        OutputIo.InspectionBackupPlateUp,
        OutputIo.InspectionStopperUp);

    public bool CarrierPresent => _io.GetInput(_carrier);

    public IoStatus CreateIoStatus(HardwareArea area, IoSignals io) => io.Select(
        area,
        [_carrier, _backupPlateUp, _backupPlateDown, _stopperUp, _stopperDown, _heatSink1, _heatSink2],
        [_backupPlate, _stopper]);

    public TeachingOutput[] GetTeachingOutputs() =>
    [
        new(_backupPlate, HardwareArea.MainConveyor,
            (up, token) => _io.SetOutputAndWaitAsync(_backupPlate, up, token), RequiresHandler: false),
    ];

    public StationCylinderState BackupPlate => CylinderState(
        _backupPlateUp,
        _backupPlateDown);
    public StationCylinderState Stopper => CylinderState(
        _stopperUp,
        _stopperDown);
    internal bool HeatSinkPresent(HeatSinkSlot heatSink) =>
        _io.GetInput(heatSink == HeatSinkSlot.HeatSink1
            ? _heatSink1
            : _heatSink2);

    public Task PrepareToReceiveAsync(
        CancellationToken cancellationToken) =>
        Task.WhenAll(
            _io.SetOutputAndWaitAsync(
                _stopper,
                true,
                cancellationToken),
            _io.SetOutputAndWaitAsync(
                _backupPlate,
                false,
                cancellationToken));

    public Task ReleaseAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(
            _io.SetOutputAndWaitAsync(
                _stopper,
                false,
                cancellationToken),
            _io.SetOutputAndWaitAsync(
                _backupPlate,
                false,
                cancellationToken));

    public async Task SeatAsync(CancellationToken cancellationToken)
    {
        await _io.SetOutputAndWaitAsync(
            _stopper,
            true,
            cancellationToken);
        await _io.SetOutputAndWaitAsync(
            _backupPlate,
            true,
            cancellationToken);
        await _io.SetOutputAndWaitAsync(
            _stopper,
            false,
            cancellationToken);
    }

    public Task RaiseBackupPlateAsync(
        CancellationToken cancellationToken) =>
        _io.SetOutputAndWaitAsync(
            _backupPlate,
            true,
            cancellationToken);

    public Task WaitForCarrierAsync(CancellationToken cancellationToken) =>
        _io.WaitForInputAsync(_carrier, true, cancellationToken);

    private StationCylinderState CylinderState(InputIo up, InputIo down) =>
        (_io.GetInput(up), _io.GetInput(down)) switch
        {
            (true, false) => StationCylinderState.Up,
            (false, true) => StationCylinderState.Down,
            _ => StationCylinderState.Between,
        };

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input == _carrier)
        {
            CarrierChanged?.Invoke(value);
        }

        if (input == _carrier
            || input == _backupPlateUp
            || input == _backupPlateDown
            || input == _stopperUp
            || input == _stopperDown
            || input == _heatSink1
            || input == _heatSink2)
        {
            Changed?.Invoke();
        }
    }
}
