namespace IBTM.Device.Adapters.Ajin;

/// <summary>Ajin motion controller adapter. Public coordinates use millimetres.</summary>
public sealed class AjinMotionService : IMotionService
{
    private const double UnitsPerMillimetre = 1_000.0;
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromMilliseconds(20);

    private int? _axisX;
    private int? _axisY;
    private int? _axisZ;

    public event EventHandler<MotionPositionEventArgs>? PositionChanged;

    public static void InitializeController(string parameterFilePath = "Settings/Default.mot")
    {
        CAXL.AxlClose();
        EnsureSuccess(CAXL.AxlOpen(7), "Failed to open the Ajin controller.");
        EnsureSuccess(
            CAXM.AxmMotLoadParaAll(parameterFilePath),
            $"Failed to load the motion parameter file '{parameterFilePath}'.");
    }

    public void InitializeAxes(int? axisX, int? axisY, int? axisZ)
    {
        _axisX = axisX;
        _axisY = axisY;
        _axisZ = axisZ;
    }

    public void Enable() => ForEachAxis(axis => CAXM.AxmSignalServoOn(axis, 1));

    public void Disable() => ForEachAxis(axis => CAXM.AxmSignalServoOn(axis, 0));

    public async Task MoveToXYAsync(
        double x,
        double y,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        ValidateVelocity(velocity);
        var axisX = RequireAxis(_axisX, "X");
        var axisY = RequireAxis(_axisY, "Y");
        var current = GetPosition();
        var deltaX = x - current.X!.Value;
        var deltaY = y - current.Y!.Value;
        var distance = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));

        if (distance == 0)
        {
            return;
        }

        if (deltaX == 0)
        {
            await MoveToYAsync(y, velocity, cancellationToken);
            return;
        }

        if (deltaY == 0)
        {
            await MoveToXAsync(x, velocity, cancellationToken);
            return;
        }

        var xVelocity = velocity * Math.Abs(deltaX) / distance * UnitsPerMillimetre;
        var yVelocity = velocity * Math.Abs(deltaY) / distance * UnitsPerMillimetre;

        EnsureSuccess(
            CAXM.AxmMoveMultiPos(
                2,
                [axisX, axisY],
                [ToControllerUnits(x), ToControllerUnits(y)],
                [xVelocity, yVelocity],
                [xVelocity * 2, yVelocity * 2],
                [xVelocity * 2, yVelocity * 2]),
            "Failed to start the XY move.");

        await WaitForMotionAsync([axisX, axisY], cancellationToken);
    }

    public Task MoveToXAsync(
        double x,
        double velocity,
        CancellationToken cancellationToken = default) =>
        MoveAxisAsync(RequireAxis(_axisX, "X"), x, velocity, cancellationToken);

    public Task MoveToYAsync(
        double y,
        double velocity,
        CancellationToken cancellationToken = default) =>
        MoveAxisAsync(RequireAxis(_axisY, "Y"), y, velocity, cancellationToken);

    public Task MoveToZAsync(
        double z,
        double velocity,
        CancellationToken cancellationToken = default) =>
        MoveAxisAsync(RequireAxis(_axisZ, "Z"), z, velocity, cancellationToken);

    public void JogX(double velocity) => JogAxis(_axisX, "X", velocity);
    public void JogY(double velocity) => JogAxis(_axisY, "Y", velocity);
    public void JogZ(double velocity) => JogAxis(_axisZ, "Z", velocity);

    public void Stop() => ForEachAxis(axis => CAXM.AxmMoveSStop(axis));

    public void EmergencyStop() => ForEachAxis(axis => CAXM.AxmMoveEStop(axis));

    public MotionPosition GetPosition() => new(
        GetPosition(_axisX),
        GetPosition(_axisY),
        GetPosition(_axisZ));

    public Task<bool> HomeXAsync(
        double velocity,
        CancellationToken cancellationToken = default) =>
        HomeAsync(_axisX, velocity, configureZAxis: false, cancellationToken);

    public Task<bool> HomeYAsync(
        double velocity,
        CancellationToken cancellationToken = default) =>
        HomeAsync(_axisY, velocity, configureZAxis: false, cancellationToken);

    public Task<bool> HomeZAsync(
        double velocity,
        CancellationToken cancellationToken = default) =>
        HomeAsync(_axisZ, velocity, configureZAxis: true, cancellationToken);

    public void ResetAlarm() =>
        ForEachAxis(axis => CAXM.AxmSignalServoAlarmReset(axis, 1));

    public MotionStatus? GetXStatus() => GetStatus(_axisX);
    public MotionStatus? GetYStatus() => GetStatus(_axisY);
    public MotionStatus? GetZStatus() => GetStatus(_axisZ);

    private async Task MoveAxisAsync(
        int axis,
        double position,
        double velocity,
        CancellationToken cancellationToken)
    {
        ValidateVelocity(velocity);
        var controllerVelocity = ToControllerUnits(velocity);
        EnsureSuccess(
            CAXM.AxmMovePos(
                axis,
                ToControllerUnits(position),
                controllerVelocity,
                controllerVelocity * 2,
                controllerVelocity * 2),
            $"Failed to start motion on axis {axis}.");

        await WaitForMotionAsync([axis], cancellationToken);
    }

    private void JogAxis(int? configuredAxis, string axisName, double velocity)
    {
        if (velocity == 0 || !double.IsFinite(velocity))
        {
            throw new ArgumentOutOfRangeException(nameof(velocity), "Jog velocity must be finite and non-zero.");
        }

        var axis = RequireAxis(configuredAxis, axisName);
        var controllerVelocity = ToControllerUnits(velocity);
        EnsureSuccess(
            CAXM.AxmMoveVel(
                axis,
                controllerVelocity,
                Math.Abs(controllerVelocity) * 2,
                Math.Abs(controllerVelocity) * 2),
            $"Failed to jog axis {axis}.");
    }

    private async Task WaitForMotionAsync(
        IReadOnlyCollection<int> axes,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(StatusPollInterval, cancellationToken);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isMoving = false;

                foreach (var axis in axes)
                {
                    var status = 0U;
                    EnsureSuccess(
                        CAXM.AxmStatusReadInMotion(axis, ref status),
                        $"Failed to read motion status for axis {axis}.");
                    isMoving |= status != 0;
                }

                RaisePositionChanged();
                if (!isMoving)
                {
                    return;
                }

                await Task.Delay(StatusPollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var axis in axes)
            {
                CAXM.AxmMoveSStop(axis);
            }

            throw;
        }
    }

    private async Task<bool> HomeAsync(
        int? configuredAxis,
        double velocity,
        bool configureZAxis,
        CancellationToken cancellationToken)
    {
        if (configuredAxis is null)
        {
            return false;
        }

        ValidateVelocity(velocity);
        var axis = configuredAxis.Value;
        var controllerVelocity = ToControllerUnits(velocity);

        if (CAXM.AxmHomeSetResult(axis, (uint)AXT_MOTION_HOME_RESULT.HOME_ERR_UNKNOWN)
            != (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS)
        {
            return false;
        }

        if (configureZAxis
            && CAXM.AxmHomeSetMethod(axis, 0, 4, 0, 1_000, 0)
                != (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS)
        {
            return false;
        }

        if (CAXM.AxmHomeSetVel(
                axis,
                controllerVelocity,
                controllerVelocity / 5,
                controllerVelocity / 10,
                controllerVelocity / 100,
                controllerVelocity,
                controllerVelocity / 10)
            != (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS
            || CAXM.AxmHomeSetStart(axis) != (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS)
        {
            return false;
        }

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = 0U;
                CAXM.AxmHomeGetResult(axis, ref result);
                RaisePositionChanged();

                if (result == (uint)AXT_MOTION_HOME_RESULT.HOME_SUCCESS)
                {
                    return true;
                }

                if (result != (uint)AXT_MOTION_HOME_RESULT.HOME_SEARCHING)
                {
                    return false;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            CAXM.AxmMoveSStop(axis);
            throw;
        }
    }

    private MotionStatus? GetStatus(int? configuredAxis)
    {
        if (configuredAxis is null)
        {
            return null;
        }

        var axis = configuredAxis.Value;
        var mechanicalStatus = 0U;
        if (CAXM.AxmStatusReadMechanical(axis, ref mechanicalStatus)
            != (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS)
        {
            return null;
        }

        var homeResult = 0U;
        CAXM.AxmHomeGetResult(axis, ref homeResult);
        var servoOn = 0U;
        CAXM.AxmSignalIsServoOn(axis, ref servoOn);

        return new MotionStatus(
            IsOriginDone: homeResult == (uint)AXT_MOTION_HOME_RESULT.HOME_SUCCESS,
            IsServoOn: servoOn != 0,
            IsEmergency: IsBitSet(mechanicalStatus, 6),
            IsAlarm: IsBitSet(mechanicalStatus, 4),
            IsInPosition: IsBitSet(mechanicalStatus, 5),
            IsHome: IsBitSet(mechanicalStatus, 7),
            IsLimitPositive: IsBitSet(mechanicalStatus, 0),
            IsLimitNegative: IsBitSet(mechanicalStatus, 1));
    }

    private double? GetPosition(int? configuredAxis)
    {
        if (configuredAxis is null)
        {
            return null;
        }

        var position = 0.0;
        EnsureSuccess(
            CAXM.AxmStatusGetActPos(configuredAxis.Value, ref position),
            $"Failed to read position for axis {configuredAxis.Value}.");
        return position / UnitsPerMillimetre;
    }

    private void RaisePositionChanged()
    {
        var position = GetPosition();
        PositionChanged?.Invoke(
            this,
            new MotionPositionEventArgs(
                position.X ?? 0,
                position.Y ?? 0,
                position.Z ?? 0));
    }

    private void ForEachAxis(Action<int> action)
    {
        if (_axisX.HasValue)
        {
            action(_axisX.Value);
        }

        if (_axisY.HasValue)
        {
            action(_axisY.Value);
        }

        if (_axisZ.HasValue)
        {
            action(_axisZ.Value);
        }
    }

    private static int RequireAxis(int? axis, string axisName) =>
        axis ?? throw new InvalidOperationException($"Axis {axisName} is not configured.");

    private static void ValidateVelocity(double velocity)
    {
        if (!double.IsFinite(velocity) || velocity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(velocity), "Velocity must be finite and greater than zero.");
        }
    }

    private static double ToControllerUnits(double millimetres) =>
        millimetres * UnitsPerMillimetre;

    private static bool IsBitSet(uint value, int bit) =>
        (value & (1U << bit)) != 0;

    private static void EnsureSuccess(uint result, string message)
    {
        if (result != (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS)
        {
            throw new InvalidOperationException($"{message} Result code: {result}.");
        }
    }
}
