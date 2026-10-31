using System.Collections.Generic;
using System.Threading.Tasks;

namespace FASTECH;

public partial class EziMOTIONPlusRLib
{
    public const int FMM_OK = 0;
    public const int MAX_SLAVE_NUMS = 16;
    public const uint Ready = 0x02180000;
    public const uint Moving = 0x08000000;

    static EziMOTIONPlusRLib()
    {
        Calls = [];
        Parameters = [];
        MoveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static List<string> Calls { get; }
    public static Dictionary<byte, int> Parameters { get; }
    public static int ConnectResult { get; set; }
    public static int ReadResult { get; set; }
    public static uint Status { get; set; }
    public static int ActualPosition { get; set; }
    public static int Target { get; private set; }
    public static uint Speed { get; private set; }
    public static MOTION_OPTION_EX? MoveOptions { get; private set; }
    public static bool CompleteMove { get; set; }
    public static bool CompleteStop { get; set; }
    public static TaskCompletionSource MoveStarted { get; private set; }
    public static TaskCompletionSource StopRequested { get; private set; }

    public static void Reset()
    {
        Calls.Clear();
        Parameters.Clear();
        ConnectResult = 1;
        ReadResult = 0;
        Status = Ready;
        ActualPosition = 0;
        Target = 0;
        Speed = 0;
        MoveOptions = null;
        CompleteMove = true;
        CompleteStop = true;
        MoveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static int FAS_Connect(byte port, uint baud)
    {
        Calls.Add($"Connect:{port}:{baud}");
        return ConnectResult;
    }

    public static void FAS_Close(byte port)
    {
        Calls.Add($"Close:{port}");
    }

    public static int FAS_GetAxisStatus(byte port, byte slave, ref uint status)
    {
        Calls.Add($"Status:{slave}");
        status = Status;
        return ReadResult;
    }

    public static int FAS_GetActualPos(byte port, byte slave, ref int position)
    {
        position = ActualPosition;
        return ReadResult;
    }

    public static int FAS_MoveSingleAxisAbsPosEx(byte port, byte slave, int target, uint speed, MOTION_OPTION_EX options)
    {
        Target = target;
        Speed = speed;
        MoveOptions = options;
        if (CompleteMove)
            ActualPosition = target;
        Status = CompleteMove ? Ready : Ready | Moving;
        MoveStarted.TrySetResult();
        return FMM_OK;
    }

    public static int FAS_MoveVelocityEx(byte port, byte slave, uint speed, int direction, VELOCITY_OPTION_EX options)
    {
        Speed = speed;
        Status = Ready | Moving;
        MoveStarted.TrySetResult();
        return FMM_OK;
    }

    public static int FAS_MoveStop(byte port, byte slave)
    {
        Calls.Add($"Stop:{slave}");
        if (CompleteStop)
            Status = Ready;
        StopRequested.TrySetResult();
        return FMM_OK;
    }

    public static int FAS_ServoEnable(byte port, byte slave, int on)
    {
        Calls.Add($"Servo:{slave}:{on}");
        Status = on != 0 ? Status | 0x00100000 : Status & ~0x00100000U;
        return FMM_OK;
    }

    public static int FAS_ServoAlarmReset(byte port, byte slave)
    {
        Calls.Add($"Reset:{slave}");
        return FMM_OK;
    }

    public static int FAS_SetParameter(byte port, byte slave, byte parameter, int value)
    {
        Parameters[parameter] = value;
        return FMM_OK;
    }

    public static int FAS_MoveOriginSingleAxis(byte port, byte slave)
    {
        Status = Ready;
        ActualPosition = 0;
        return FMM_OK;
    }
}
