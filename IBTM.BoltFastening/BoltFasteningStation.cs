using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningStation
{
    private readonly IBoltHead _shootingHead;
    private readonly IBoltHead _pickupHead;
    private readonly IIoService _io;
    private readonly IXyMotion _motion;
    private readonly BoltFasteningSettings _settings;

    public BoltFasteningStation(
        IBoltHead shootingHead,
        IBoltHead pickupHead,
        IIoService io,
        IXyMotion motion,
        BoltFasteningSettings settings)
    {
        _shootingHead = shootingHead;
        _pickupHead = pickupHead;
        _io = io;
        _motion = motion;
        _settings = settings;
    }

    public bool PickupBoltLoaded =>
        _io.GetInput(InputIo.BoltHead1VacuumDetected);
    public bool ShootingBoltLoaded =>
        _io.GetInput(InputIo.BoltHead2VacuumDetected);

    public async Task CheckReadyAsync(
        CancellationToken cancellationToken = default)
    {
        await _shootingHead.CheckReadyAsync(cancellationToken);
        await _pickupHead.CheckReadyAsync(cancellationToken);
    }

    public async Task<BoltResult> FastenAsync(
        BoltPoint bolt,
        CancellationToken cancellationToken = default)
    {
        var position = _settings.GetBoltPosition(bolt);
        if (bolt.Head == FasteningHead.Shooting)
        {
            await _io.SetOutputAndWaitAsync(
                OutputIo.ShootingEscapeForward,
                false,
                cancellationToken);
        }

        await _motion.MoveToAsync(
            position.X,
            position.Y,
            position.Z,
            cancellationToken);
        if (bolt.Head == FasteningHead.Pickup)
        {
            await SetPickupHeadDownAsync(true, cancellationToken);
        }

        var result = await GetHead(bolt.Head).TightenAsync(cancellationToken);
        await SetVacuumAsync(
            bolt.Head,
            false,
            cancellationToken);

        if (bolt.Head == FasteningHead.Pickup)
        {
            await SetPickupHeadDownAsync(false, cancellationToken);
        }

        return result;
    }

    public async Task SelectHeadAsync(
        FasteningHead head,
        ushort preset,
        CancellationToken cancellationToken = default)
    {
        await SetPickupHeadDownAsync(false, cancellationToken);
        await _io.SetOutputAndWaitAsync(
            OutputIo.BoltHead2Down,
            head == FasteningHead.Shooting,
            cancellationToken);
        await GetHead(head).SelectPresetAsync(preset, cancellationToken);
    }

    public Task ReleaseHeadAsync(
        FasteningHead head,
        CancellationToken cancellationToken = default) =>
        head == FasteningHead.Shooting
            ? _io.SetOutputAndWaitAsync(
                OutputIo.BoltHead2Down,
                false,
                cancellationToken)
            : Task.CompletedTask;

    public async Task LoadPickupBoltAsync(
        CancellationToken cancellationToken = default)
    {
        await _motion.MoveToAsync(
            _settings.PickupPosition.X,
            _settings.PickupPosition.Y,
            _settings.PickupPosition.Z,
            cancellationToken);
        await SetVacuumAsync(
            FasteningHead.Pickup,
            true,
            cancellationToken);
        await _motion.MoveToHorizontalZAsync(cancellationToken);
    }

    public async Task LoadShootingBoltAsync(
        CancellationToken cancellationToken = default)
    {
        await _io.SetOutputAndWaitAsync(
            OutputIo.ShootingEscapeForward,
            false,
            cancellationToken);
        await _io.WaitForInputAsync(
            InputIo.ShootingTubeBoltDetected,
            false,
            cancellationToken);
        _io.SetOutput(OutputIo.BoltHead2VacuumPump, true);
        await _io.SetOutputAndWaitAsync(
            OutputIo.ShootingEscapeForward,
            true,
            cancellationToken);
        _io.SetOutput(OutputIo.ShootBolt, true);
        try
        {
            await _io.WaitForInputAsync(
                InputIo.ShootingTubeBoltDetected,
                true,
                cancellationToken);
            await _io.WaitForInputAsync(
                InputIo.BoltHead2VacuumDetected,
                true,
                cancellationToken);
        }
        finally
        {
            _io.SetOutput(OutputIo.ShootBolt, false);
        }

        await _io.WaitForInputAsync(
            InputIo.ShootingTubeBoltDetected,
            false,
            cancellationToken);
        await _io.SetOutputAndWaitAsync(
            OutputIo.ShootingEscapeForward,
            false,
            cancellationToken);
    }

    public async Task MoveToSafeZAsync(
        CancellationToken cancellationToken = default)
    {
        await _motion.MoveToHorizontalZAsync(cancellationToken);
    }

    public void Stop() =>
        _io.SetOutput(OutputIo.ShootBolt, false);

    private IBoltHead GetHead(FasteningHead head) => head switch
    {
        FasteningHead.Shooting => _shootingHead,
        FasteningHead.Pickup => _pickupHead,
        _ => throw new System.ArgumentOutOfRangeException(nameof(head)),
    };

    private Task SetPickupHeadDownAsync(
        bool down,
        CancellationToken cancellationToken) =>
        Task.WhenAll(
            _io.SetOutputAndWaitAsync(
                OutputIo.BoltTableDown,
                down,
                cancellationToken),
            _io.SetOutputAndWaitAsync(
                OutputIo.BoltHead1Down,
                down,
                cancellationToken));

    private async Task SetVacuumAsync(
        FasteningHead head,
        bool on,
        CancellationToken cancellationToken)
    {
        var output = head == FasteningHead.Pickup
            ? OutputIo.BoltHead1VacuumPump
            : OutputIo.BoltHead2VacuumPump;
        var input = head == FasteningHead.Pickup
            ? InputIo.BoltHead1VacuumDetected
            : InputIo.BoltHead2VacuumDetected;
        _io.SetOutput(output, on);
        await _io.WaitForInputAsync(input, on, cancellationToken);
    }

}
