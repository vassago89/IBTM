using System.Runtime.InteropServices;

namespace IBTM.Ajin;

internal static class AjinNative
{
    private const string LibraryName = "AXL.dll";

    [DllImport(LibraryName)]
    internal static extern uint AxlOpen(int interruptNumber);

    [DllImport(LibraryName)]
    internal static extern int AxlClose();

    [DllImport(LibraryName, CharSet = CharSet.Ansi)]
    internal static extern uint AxmMotLoadParaAll(string filePath);

    [DllImport(LibraryName)]
    internal static extern uint AxmSignalServoOn(int axis, uint on);

    [DllImport(LibraryName)]
    internal static extern uint AxmSignalIsServoOn(int axis, ref uint on);

    [DllImport(LibraryName)]
    internal static extern uint AxmSignalServoAlarmReset(int axis, uint on);

    [DllImport(LibraryName)]
    internal static extern uint AxmStatusGetActPos(int axis, ref double position);

    [DllImport(LibraryName)]
    internal static extern uint AxmStatusReadInMotion(int axis, ref uint status);

    [DllImport(LibraryName)]
    internal static extern uint AxmStatusReadMechanical(int axis, ref uint status);

    [DllImport(LibraryName)]
    internal static extern uint AxmSignalGetLimit(
        int axis,
        ref uint stopMode,
        ref uint positiveLevel,
        ref uint negativeLevel);

    [DllImport(LibraryName)]
    internal static extern uint AxmHomeSetResult(int axis, uint result);

    [DllImport(LibraryName)]
    internal static extern uint AxmHomeSetVel(
        int axis,
        double velocityFirst,
        double velocitySecond,
        double velocityThird,
        double velocityLast,
        double accelerationFirst,
        double accelerationSecond);

    [DllImport(LibraryName)]
    internal static extern uint AxmHomeSetStart(int axis);

    [DllImport(LibraryName)]
    internal static extern uint AxmHomeGetResult(int axis, ref uint result);

    [DllImport(LibraryName)]
    internal static extern uint AxmMovePos(
        int axis,
        double position,
        double velocity,
        double acceleration,
        double deceleration);

    [DllImport(LibraryName)]
    internal static extern uint AxmMoveMultiPos(
        int axisCount,
        int[] axes,
        double[] positions,
        double[] velocities,
        double[] accelerations,
        double[] decelerations);

    [DllImport(LibraryName)]
    internal static extern uint AxmMoveVel(
        int axis,
        double velocity,
        double acceleration,
        double deceleration);

    [DllImport(LibraryName)]
    internal static extern uint AxmMoveSignalSearch(
        int axis,
        double velocity,
        double acceleration,
        int detectSignal,
        int signalEdge,
        int signalMethod);

    [DllImport(LibraryName)]
    internal static extern uint AxmMoveSStop(int axis);

    [DllImport(LibraryName)]
    internal static extern uint AxdiReadInportBit(
        int module,
        int offset,
        ref uint value);

    [DllImport(LibraryName)]
    internal static extern uint AxdoReadOutportBit(
        int module,
        int offset,
        ref uint value);

    [DllImport(LibraryName)]
    internal static extern uint AxdoWriteOutportBit(
        int module,
        int offset,
        uint value);
}
