using System;

// In-memory SDK stand-in only; no native declarations or equipment access.
internal static partial class CAXM
{
    public static uint AxmMotSetMoveUnitPerPulse(int axis, double unit, int pulse)
    {
        var result = AjinSdk.Record(new(nameof(AxmMotSetMoveUnitPerPulse), Axis: axis));
        if (result == 0)
            AjinSdk.MotionAxes[axis] = AjinSdk.MotionAxes[axis] with { Unit = unit, Pulse = pulse };
        return result;
    }

    public static uint AxmMotSetAccelUnit(int axis, uint unit)
    {
        var result = AjinSdk.Record(new(nameof(AxmMotSetAccelUnit), Value: unit, Axis: axis));
        if (result == 0)
            AjinSdk.MotionAxes[axis] = AjinSdk.MotionAxes[axis] with { AccelerationUnit = unit };
        return result;
    }

    public static uint AxmMotGetAccelUnit(int axis, ref uint unit)
    {
        unit = AjinSdk.MotionAxes[axis].AccelerationUnit;
        return AjinSdk.Record(new(nameof(AxmMotGetAccelUnit), Axis: axis));
    }

    public static uint AxmMotGetMoveUnitPerPulse(int axis, ref double unit, ref int pulse)
    {
        unit = AjinSdk.MotionAxes[axis].Unit;
        pulse = AjinSdk.MotionAxes[axis].Pulse;
        return AjinSdk.Record(new(nameof(AxmMotGetMoveUnitPerPulse), Axis: axis));
    }

    public static uint AxmStatusReadMechanical(int axis, ref uint value)
    {
        value = AjinSdk.MotionAxes[axis].Mechanical;
        return AjinSdk.Record(new(nameof(AxmStatusReadMechanical), Axis: axis));
    }

    public static uint AxmHomeGetResult(int axis, ref uint value)
    {
        value = AjinSdk.MotionAxes[axis].HomeResult;
        return AjinSdk.Record(new(nameof(AxmHomeGetResult), Axis: axis));
    }

    public static uint AxmSignalIsServoOn(int axis, ref uint value)
    {
        value = AjinSdk.MotionAxes[axis].ServoOn;
        return AjinSdk.Record(new(nameof(AxmSignalIsServoOn), Axis: axis));
    }

    public static uint AxmStatusGetActPos(int axis, ref double value)
    {
        value = AjinSdk.MotionAxes[axis].Position;
        return AjinSdk.Record(new(nameof(AxmStatusGetActPos), Axis: axis));
    }

    public static uint AxmSignalServoOn(int axis, uint on)
    {
        var result = AjinSdk.Record(new(nameof(AxmSignalServoOn), Value: on, Axis: axis));
        if (result != 0)
            return result;
        var state = AjinSdk.MotionAxes[axis];
        if (on != 0 && (state.Mechanical & (1U << 4)) != 0)
            return (uint)AXT_FUNC_RESULT.AXT_RT_MOTION_ERROR_IN_ALARM;
        AjinSdk.MotionAxes[axis] = state with { ServoOn = on };
        return 0;
    }

    public static uint AxmSignalServoAlarmReset(int axis, uint value)
    {
        var result = AjinSdk.Record(new(nameof(AxmSignalServoAlarmReset), Value: value, Axis: axis));
        if (result == 0)
            AjinSdk.MotionAxes[axis] = AjinSdk.MotionAxes[axis] with { Mechanical = AjinSdk.MotionAxes[axis].Mechanical & ~(1U << 4) };
        return result;
    }

    // Commands must be explicitly configured; status/initialization tests cannot move axes.
    private static uint Command(AjinSdk.Call call)
    {
        if (!AjinSdk.Results.ContainsKey(call))
        {
            throw new NotSupportedException($"Unexpected motion command: {call.Operation}");
        }

        return AjinSdk.Record(call);
    }

    public static uint AxmMoveMultiPos(
        int count,
        int[] axes,
        double[] positions,
        double[] velocities,
        double[] accelerations,
        double[] decelerations)
    {
        throw new NotSupportedException();
    }

    public static uint AxmMovePos(
        int axis,
        double position,
        double velocity,
        double acceleration,
        double deceleration)
    {
        return Command(new(nameof(AxmMovePos), Axis: axis));
    }

    public static uint AxmMoveVel(int axis, double velocity, double acceleration, double deceleration)
    {
        throw new NotSupportedException();
    }

    public static uint AxmMoveSignalSearch(
        int axis,
        double velocity,
        double acceleration,
        int signal,
        int edge,
        int method)
    {
        throw new NotSupportedException();
    }

    public static uint AxmMoveSStop(int axis)
    {
        return Command(new(nameof(AxmMoveSStop), Axis: axis));
    }

    public static uint AxmStatusReadInMotion(int axis, ref uint value)
    {
        value = AjinSdk.MotionAxes[axis].InMotion;
        return AjinSdk.Record(new(nameof(AxmStatusReadInMotion), Axis: axis));
    }

    public static uint AxmSignalGetLimit(
        int axis,
        ref uint stopMode,
        ref uint positive,
        ref uint negative)
    {
        throw new NotSupportedException();
    }

    public static uint AxmHomeSetResult(int axis, uint result)
    {
        return Command(new(nameof(AxmHomeSetResult), Axis: axis, Value: result));
    }

    public static uint AxmHomeSetVel(
        int axis,
        double first,
        double second,
        double third,
        double last,
        double firstAcceleration,
        double secondAcceleration)
    {
        return Command(new(nameof(AxmHomeSetVel), Axis: axis));
    }

    public static uint AxmHomeSetStart(int axis)
    {
        return Command(new(nameof(AxmHomeSetStart), Axis: axis));
    }
}
