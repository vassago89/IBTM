using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

namespace FASTECH;

public partial class EziMOTIONPlusELib
{
    public const int FMM_OK = 0;
    public const uint Ready = 0x02180000;
    public const uint Moving = 0x08000000;

    static EziMOTIONPlusELib()
    {
        Calls = [];
        Parameters = [];
        MoveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static List<string> Calls { get; }
    public static Dictionary<byte, int> Parameters { get; }
    public static bool ConnectResult { get; set; }
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
        ConnectResult = true;
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

    public static bool FAS_ConnectTCP(IPAddress address, int boardId)
    {
        Calls.Add($"Connect:{address}:{boardId}");
        return ConnectResult;
    }

    public static void FAS_Close(int boardId)
    {
        Calls.Add($"Close:{boardId}");
    }

    public static int FAS_GetAxisStatus(int boardId, ref uint status)
    {
        Calls.Add($"Status:{boardId}");
        status = Status;
        return ReadResult;
    }

    public static int FAS_GetActualPos(int boardId, ref int position)
    {
        position = ActualPosition;
        return ReadResult;
    }

    public static int FAS_MoveSingleAxisAbsPosEx(int boardId, int target, uint speed, MOTION_OPTION_EX options)
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

    public static int FAS_MoveVelocityEx(int boardId, uint speed, int direction, VELOCITY_OPTION_EX options)
    {
        Speed = speed;
        Status = Ready | Moving;
        MoveStarted.TrySetResult();
        return FMM_OK;
    }

    public static int FAS_MoveStop(int boardId)
    {
        Calls.Add($"Stop:{boardId}");
        if (CompleteStop)
            Status = Ready;
        StopRequested.TrySetResult();
        return FMM_OK;
    }

    public static int FAS_ServoEnable(int boardId, int on)
    {
        Calls.Add($"Servo:{boardId}:{on}");
        Status = on != 0 ? Status | 0x00100000 : Status & ~0x00100000U;
        return FMM_OK;
    }

    public static int FAS_ServoAlarmReset(int boardId)
    {
        Calls.Add($"Reset:{boardId}");
        return FMM_OK;
    }

    public static int FAS_SetParameter(int boardId, byte parameter, int value)
    {
        Parameters[parameter] = value;
        return FMM_OK;
    }

    public static int FAS_MoveOriginSingleAxis(int boardId)
    {
        Status = Ready;
        ActualPosition = 0;
        return FMM_OK;
    }
}
