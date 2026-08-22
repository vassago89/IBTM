using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementHandler
{
    private readonly IXyMotion _motion;
    private readonly IIoService _io;
    private readonly PcbPlacementHandlerSettings _settings;

    public PcbPlacementHandler(
        IXyMotion motion,
        IIoService io,
        PcbPlacementHandlerSettings settings)
    {
        _motion = motion;
        _io = io;
        _settings = settings;
        io.InputChanged += OnInputChanged;
    }

    public event Action? Changed;

    public bool IsMoving => _motion.IsMoving;
    public bool PcbDetected =>
        _io.GetInput(InputIo.PcbPlacementPcbDetected);
    public bool VacuumDetected =>
        _io.GetInput(InputIo.PcbPlacementVacuumDetected);
    public bool IpmGripperClosed =>
        _io.GetInput(InputIo.PcbPlacementIpmGripperClosed);
    public bool PcbSecured =>
        PcbDetected && VacuumDetected && IpmGripperClosed;

    public async Task SecureAtBufferAsync(
        CancellationToken cancellationToken)
    {
        await SetIpmGripperAsync(false, cancellationToken);
        _io.SetOutput(OutputIo.PcbPlacementVacuumEjector, false);
        await _motion.MoveToAsync(
            _settings.BufferHandoffPosition.X,
            _settings.BufferHandoffPosition.Y,
            _settings.BufferHandoffPosition.Z,
            cancellationToken);
        await _io.WaitForInputAsync(
            InputIo.PcbPlacementPcbDetected,
            true,
            cancellationToken);
        _io.SetOutput(OutputIo.PcbPlacementVacuumEjector, true);
        await _io.WaitForInputAsync(
            InputIo.PcbPlacementVacuumDetected,
            true,
            cancellationToken);
        await SetIpmGripperAsync(true, cancellationToken);
    }

    public async Task MoveClearAsync(
        AxisPos clearPosition,
        CancellationToken cancellationToken = default)
    {
        await _motion.MoveToAsync(
            clearPosition.X,
            clearPosition.Y,
            clearPosition.Z,
            cancellationToken);
    }

    public Task SetIpmGripperAsync(
        bool closed,
        CancellationToken cancellationToken = default) =>
        _io.SetOutputAndWaitAsync(
            OutputIo.PcbPlacementIpmGripperClose,
            closed,
            cancellationToken);

    private void OnInputChanged(InputIo input, bool _)
    {
        if (input is InputIo.PcbPlacementPcbDetected
            or InputIo.PcbPlacementVacuumDetected
            or InputIo.PcbPlacementIpmGripperClosed)
        {
            Changed?.Invoke();
        }
    }
}
