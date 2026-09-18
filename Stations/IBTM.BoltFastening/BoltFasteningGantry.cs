using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningGantry
{
    private readonly IBoltHead _shootingHead;
    private readonly IBoltHead _pickupHead;
    private readonly IIoService _io;
    private readonly IXyMotion _motion;
    private readonly BoltFasteningSettings _settings;
    private readonly CarrierReferenceSettings _carrierReference;

    public BoltFasteningGantry(
        IBoltHead shootingHead,
        IBoltHead pickupHead,
        IIoService io,
        IXyMotion motion,
        BoltFasteningSettings settings,
        CarrierReferenceSettings carrierReference)
    {
        _shootingHead = shootingHead;
        _pickupHead = pickupHead;
        _io = io;
        _motion = motion;
        _settings = settings;
        _carrierReference = carrierReference;
        Motion = new(motion);
        io.InputChanged += OnInputChanged;
    }

    public event Action? Changed;

    public MotionStatus Motion { get; }

    public bool HasPendingResult
    {
        get
        {
            return _pickupHead.HasPendingResult || _shootingHead.HasPendingResult;
        }
    }

    public IMotionFeedback Feedback
    {
        get
        {
            return _motion;
        }
    }

    public bool PickupBoltLoaded
    {
        get
        {
            return _io.GetInput(InputIo.PickupHeadVacuumDetected);
        }
    }

    public bool ShootingBoltLoaded
    {
        get
        {
            return _io.GetInput(InputIo.ShootingHeadVacuumDetected);
        }
    }

    internal bool ShootingTubeBoltDetected
    {
        get
        {
            return _io.GetInput(InputIo.ShootingTubeBoltDetected);
        }
    }

    public BoltCylinderState PickupHeadPosition
    {
        get
        {
            return CylinderState(InputIo.PickupHeadUp, InputIo.PickupHeadDown);
        }
    }

    public BoltCylinderState ShootingHeadPosition
    {
        get
        {
            return CylinderState(InputIo.ShootingHeadUp, InputIo.ShootingHeadDown);
        }
    }

    public bool CanMoveHorizontal
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
            return (
                _io.GetInput(InputIo.ShootingEscapeForward),
                _io.GetInput(InputIo.ShootingEscapeBackward)) switch
            {
                (true, false) => BoltEscapeState.Forward,
                (false, true) => BoltEscapeState.Backward,
                _ => BoltEscapeState.Between,
            };
        }
    }

    public bool IsAtSafeZ(bool live = true)
    {
        return Motion.IsSettled(live, MotionAxis.Z)
            && (live ? _motion.IsAtHorizontalZ : Motion.IsAtZ(_settings.SafeZ));
    }

    internal bool IsAt(BoltTarget bolt, bool live = true)
    {
        return IsAt(_settings.GetBoltPosition(bolt, _carrierReference), live);
    }

    internal bool HasPosition(BoltTarget bolt)
    {
        return _settings.HasBoltPosition(bolt, _carrierReference);
    }

    public bool HasReference(FasteningHead head)
    {
        var reference = _settings.GetHead(head);
        return CarrierCoordinates.IsDefined(
            reference.UpperLeftLocatingPin,
            reference.LowerRightLocatingPin);
    }

    internal bool IsAtPickupPosition(bool live = true)
    {
        return IsAt(_settings.PickupPosition, live);
    }

    internal bool IsAtPickupXY(bool live = true)
    {
        var target = _settings.PickupPosition;
        var current = Motion.ReadPosition(live);
        return Motion.IsSettled(live, MotionAxis.X, MotionAxis.Y)
            && Math.Abs(current.X - target.X) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Y - target.Y) <= MotionService.PositionToleranceMillimeters;
    }

    public void InitializeMotion()
    {
        _motion.Initialize();
    }

    public void StopMotion()
    {
        _motion.Stop();
    }

    public void ResetMotion()
    {
        _motion.Reset();
    }

    public void SetServo(MotionAxis axis, bool on)
    {
        _motion.SetServo(axis, on);
    }

    public Task<bool> HomeAxisAsync(MotionAxis axis, CancellationToken cancellationToken = default)
    {
        if (axis != MotionAxis.Z)
            EnsureCanMoveHorizontal(cancellationToken);
        return _motion.HomeAsync(axis, _settings.Motion.Home(axis).SearchSpeed, cancellationToken);
    }

    public Task<bool> HomeHorizontalAsync(CancellationToken cancellationToken = default)
    {
        EnsureCanMoveHorizontal(cancellationToken);
        return _motion.HomeHorizontalAsync(
            _settings.Motion.HorizontalHome.SearchSpeed,
            cancellationToken);
    }

    public Task MoveToXYAsync(double x, double y, CancellationToken cancellationToken = default)
    {
        EnsureCanMoveHorizontal(cancellationToken);
        return _motion.MoveToXYAsync(x, y, _settings.Motion.HorizontalSpeed, cancellationToken);
    }

    public Task MoveZAsync(double z, CancellationToken cancellationToken = default)
    {
        return _motion.MoveAxisAsync(MotionAxis.Z, z, _settings.Motion.ZSpeed, cancellationToken);
    }

    public async Task MoveToPickupPositionAsync(CancellationToken cancellationToken = default)
    {
        await MoveToPickupXYAsync(cancellationToken);
        await SetHeadDownAsync(FasteningHead.Pickup, true, cancellationToken);
        await MoveToPickupZAsync(cancellationToken);
    }

    public async Task ReturnFromPickupAsync(CancellationToken cancellationToken = default)
    {
        await MoveToSafeZAsync(cancellationToken);
        await SetHeadDownAsync(FasteningHead.Pickup, false, cancellationToken);
    }

    public Task MoveToTeachingPositionAsync(
        TeachingPosition point,
        AxisPosition position,
        CancellationToken cancellationToken = default)
    {
        return point switch
        {
            { Target: TeachingTarget.BoltPickup } => MoveToPickupPositionAsync(cancellationToken),
            { Mode: TeachMode.XYOnly } => MoveToXYAsync(position.X, position.Y, cancellationToken),
            { Mode: TeachMode.ZOnly } => MoveZAsync(position.Z, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(point)),
        };
    }

    public bool CanJog(MotionAxis axis)
    {
        return axis switch
        {
            MotionAxis.X => true,
            MotionAxis.Y => _motion.HasY,
            MotionAxis.Z => _motion.HasZ,
            _ => false,
        };
    }

    public Task JogAsync(MotionAxis axis, double velocity, CancellationToken cancellationToken = default)
    {
        return _motion.JogAsync(axis, velocity, cancellationToken, atCurrentHeight: true);
    }

    public Task AdjustAxisAsync(
        MotionAxis axis,
        double position,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        return _motion.AdjustAxisAsync(axis, position, velocity, cancellationToken);
    }

    public async Task CheckReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _shootingHead.CheckReadyAsync(cancellationToken);
        await _pickupHead.CheckReadyAsync(cancellationToken);
    }

    public async Task ResetHeadsAsync(CancellationToken cancellationToken = default)
    {
        List<Exception>? failures = null;
        foreach (var head in new[] { _shootingHead, _pickupHead })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await head.ResetAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("Bolt controller reset failed.", failures);
        }
    }

    internal async Task MoveToBoltAsync(BoltTarget bolt, CancellationToken cancellationToken = default)
    {
        var position = _settings.GetBoltPosition(bolt, _carrierReference);
        // XY travel uses Safe Z. Approach the work height with both heads raised.
        await MoveToXYAsync(position.X, position.Y, cancellationToken);
        EnsureCanMoveHorizontal(cancellationToken);
        await MoveZAsync(position.Z, cancellationToken);
    }

    internal async Task FinishFasteningAsync(
        FasteningHead head,
        CancellationToken cancellationToken = default)
    {
        await SetVacuumAsync(head, false, cancellationToken);

        await SetHeadDownAsync(head, false, cancellationToken);
    }

    public Task SetHeadDownAsync(
        FasteningHead head,
        bool down,
        CancellationToken cancellationToken = default)
    {
        var output = head switch
        {
            FasteningHead.Pickup => OutputIo.PickupHeadUp,
            FasteningHead.Shooting => OutputIo.ShootingHeadUp,
            _ => throw new ArgumentOutOfRangeException(nameof(head)),
        };
        return _io.SetOutputAndWaitAsync(output, !down, cancellationToken);
    }

    public Task RaiseCylindersAsync(CancellationToken cancellationToken = default)
    {
        return Task.WhenAll(
            SetHeadDownAsync(FasteningHead.Pickup, false, cancellationToken),
            SetHeadDownAsync(FasteningHead.Shooting, false, cancellationToken));
    }

    internal async Task MoveToPickupXYAsync(CancellationToken cancellationToken = default)
    {
        await RaiseCylindersAsync(cancellationToken);
        await MoveToXYAsync(
            _settings.PickupPosition.X,
            _settings.PickupPosition.Y,
            cancellationToken);
    }

    internal Task MoveToPickupZAsync(CancellationToken cancellationToken = default)
    {
        return MoveZAsync(_settings.PickupPosition.Z, cancellationToken);
    }

    internal Task SetShootingEscapeForwardAsync(bool forward, CancellationToken cancellationToken = default)
    {
        return _io.SetOutputAndWaitAsync(OutputIo.ShootingEscapeForward, forward, cancellationToken);
    }

    internal async Task ShootBoltAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.ShootingHeadVacuumPump, true);
        using var passage = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var boltPassed = _io.WaitForInputAsync(InputIo.ShootingTubeBoltDetected, true, passage.Token);
        Exception? failure = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _io.SetOutput(OutputIo.ShootBolt, true);
            await boltPassed;
            await _io.WaitForInputAsync(InputIo.ShootingHeadVacuumDetected, true, cancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            try
            {
                StopShooting(failure);
            }
            finally
            {
                passage.Cancel();
                await boltPassed.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    internal Task WaitForShootingTubeClearAsync(CancellationToken cancellationToken = default)
    {
        return _io.WaitForInputAsync(InputIo.ShootingTubeBoltDetected, false, cancellationToken);
    }

    public Task MoveToSafeZAsync(CancellationToken cancellationToken = default)
    {
        return _motion.MoveToHorizontalZAsync(cancellationToken);
    }

    public void StopShooting(Exception? operationFailure = null)
    {
        try
        {
            _io.SetOutput(OutputIo.ShootBolt, false);
        }
        catch (Exception cleanupFailure) when (operationFailure is not null)
        {
            throw new AggregateException(operationFailure, cleanupFailure);
        }
    }

    public void StopIoStart(FasteningHead head)
    {
        if (GetHead(head) is IoBoltHead ioHead)
        {
            ioHead.Stop();
            return;
        }

        _io.SetOutput(
            head == FasteningHead.Pickup ? OutputIo.PickupBoltStart : OutputIo.ShootingBoltStart,
            false);
    }

    internal void DiscardPendingResults()
    {
        _shootingHead.DiscardPendingResult();
        _pickupHead.DiscardPendingResult();
    }

    internal IBoltHead GetHead(FasteningHead head)
    {
        return head switch
        {
            FasteningHead.Shooting => _shootingHead,
            FasteningHead.Pickup => _pickupHead,
            _ => throw new ArgumentOutOfRangeException(nameof(head)),
        };
    }

    private bool IsAt(AxisPosition target, bool live = true)
    {
        var current = Motion.ReadPosition(live);
        return Motion.IsSettled(live, MotionAxis.X, MotionAxis.Y)
            && Motion.ReadAxisState(MotionAxis.Z, live).InPosition
            && Math.Abs(current.X - target.X) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Y - target.Y) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Z - target.Z) <= MotionService.PositionToleranceMillimeters;
    }

    private BoltCylinderState CylinderState(InputIo up, InputIo down)
    {
        return (_io.GetInput(up), _io.GetInput(down)) switch
        {
            (true, false) => BoltCylinderState.Up,
            (false, true) => BoltCylinderState.Down,
            _ => BoltCylinderState.Between,
        };
    }

    public async Task SetVacuumAsync(
        FasteningHead head,
        bool on,
        CancellationToken cancellationToken,
        bool waitForFeedback = true)
    {
        var output = head == FasteningHead.Pickup
            ? OutputIo.PickupHeadVacuumPump
            : OutputIo.ShootingHeadVacuumPump;
        var input = head == FasteningHead.Pickup
            ? InputIo.PickupHeadVacuumDetected
            : InputIo.ShootingHeadVacuumDetected;
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(output, on);
        if (waitForFeedback)
            await _io.WaitForInputAsync(input, on, cancellationToken);
    }

    private void OnInputChanged(InputIo input, bool _)
    {
        if (input is InputIo.PickupHeadVacuumDetected
            or InputIo.ShootingHeadVacuumDetected
            or InputIo.ShootingTubeBoltDetected
            or InputIo.PickupHeadUp
            or InputIo.PickupHeadDown
            or InputIo.ShootingHeadUp
            or InputIo.ShootingHeadDown
            or InputIo.ShootingEscapeForward
            or InputIo.ShootingEscapeBackward)
        {
            Changed?.Invoke();
        }
    }

    private void EnsureCanMoveHorizontal(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanMoveHorizontal)
        {
            throw new MotionInterlockException("Raise both fastening heads before moving X/Y.");
        }
    }

}
