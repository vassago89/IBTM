using System;
using System.Collections.Generic;

namespace Shared;

// Test assembly only. Production uses the unchanged manufacturer's tmcDApiAed.cs.
internal static class TMCAEDLL
{
    internal sealed record Call(string Operation, ushort? Card = null, ushort? Channel = null,
        ushort? Group = null, ushort? Value = null);
    internal static readonly List<Call> Calls = [];
    internal static readonly Dictionary<string, int> Results = [];
    internal static ushort InputCount = 16;
    internal static ushort OutputCount = 16;
    internal static ushort Inputs;
    internal static ushort Outputs;
    internal static int ErrorCode;
    internal static Action<string>? BeforeCall;

    internal static void Reset()
    {
        Calls.Clear();
        Results.Clear();
        InputCount = OutputCount = 16;
        Inputs = Outputs = 0;
        ErrorCode = tmcDef.ERR_SUCCESS;
        BeforeCall = null;
    }

    private static int Record(Call call, int successResult = tmcDef.TMC_ST_OK)
    {
        Calls.Add(call);
        BeforeCall?.Invoke(call.Operation);
        return Results.GetValueOrDefault(call.Operation, successResult);
    }

    // Manufacturer frmDIGITAL.LoadDevice: nonnegative result + 1 is the board count.
    public static int AIO_LoadDevice() => Record(new(nameof(AIO_LoadDevice)), successResult: 0);
    public static int AIO_UnloadDevice() => Record(new(nameof(AIO_UnloadDevice)));
    public static int AIO_GetErrorCode()
    {
        Calls.Add(new(nameof(AIO_GetErrorCode)));
        return ErrorCode;
    }

    public static int AIO_GetDiNum(ushort card, ref ushort count)
    {
        count = InputCount;
        return Record(new(nameof(AIO_GetDiNum), card));
    }

    public static int AIO_GetDoNum(ushort card, ref ushort count)
    {
        count = OutputCount;
        return Record(new(nameof(AIO_GetDoNum), card));
    }

    public static int AIO_GetDIBit(ushort card, ushort channel, ref ushort value)
    {
        value = (ushort)((Inputs >> channel) & 1);
        return Record(new(nameof(AIO_GetDIBit), card, channel));
    }

    public static int AIO_GetDIWord(ushort card, ushort group, ref ushort value)
    {
        value = Inputs;
        return Record(new(nameof(AIO_GetDIWord), card, Group: group));
    }

    public static int AIO_GetDOBit(ushort card, ushort channel, ref ushort value)
    {
        value = (ushort)((Outputs >> channel) & 1);
        return Record(new(nameof(AIO_GetDOBit), card, channel));
    }

    public static int AIO_PutDOBit(ushort card, ushort channel, ushort value)
    {
        var result = Record(new(nameof(AIO_PutDOBit), card, channel, Value: value));
        if (result == tmcDef.TMC_ST_OK)
            Outputs = (ushort)(value == 0 ? Outputs & ~(1 << channel) : Outputs | (1 << channel));
        return result;
    }
}
