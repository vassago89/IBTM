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
    internal static readonly Dictionary<string, int> Errors = [];
    internal static readonly HashSet<string> SkipRefWrites = [];
    internal static uint Model;
    internal static uint Communication;
    internal static uint InputCount;
    internal static uint OutputCount;
    internal static uint Inputs;
    internal static uint Outputs;
    internal static int DefaultResult;
    internal static bool SuppressOutputWrites;
    internal static int ErrorCode;
    internal static Action<string>? BeforeCall;

    internal static void Reset()
    {
        Calls.Clear();
        Results.Clear();
        Errors.Clear();
        SkipRefWrites.Clear();
        Model = tmcDef.TMC_AE;
        Communication = 0;
        InputCount = OutputCount = 16;
        Inputs = Outputs = 0;
        DefaultResult = tmcDef.TMC_ST_OK;
        SuppressOutputWrites = false;
        ErrorCode = tmcDef.ERR_SUCCESS;
        BeforeCall = null;
    }

    private static int Record(Call call, int? successResult = null)
    {
        Calls.Add(call);
        ErrorCode = Errors.GetValueOrDefault(call.Operation, tmcDef.ERR_SUCCESS);
        BeforeCall?.Invoke(call.Operation);
        return Results.GetValueOrDefault(call.Operation, successResult ?? DefaultResult);
    }

    // Manufacturer frmDIGITAL.LoadDevice: nonnegative result + 1 is the board count.
    public static int AIO_LoadDevice() => Record(new(nameof(AIO_LoadDevice)), successResult: 0);
    public static int AIO_UnloadDevice() => Record(new(nameof(AIO_UnloadDevice)));
    public static int AIO_GetErrorCode()
    {
        Calls.Add(new(nameof(AIO_GetErrorCode)));
        return ErrorCode;
    }

    public static int AIO_BoardInfo(ushort card, ref uint model, ref uint communication, ref uint inputs, ref uint outputs)
    {
        var result = Record(new(nameof(AIO_BoardInfo), card));
        if (!SkipRefWrites.Contains(nameof(AIO_BoardInfo)))
        {
            model = Model;
            communication = Communication;
            inputs = InputCount;
            outputs = OutputCount;
        }
        return result;
    }

    public static int AIO_GetDIDWord(ushort card, ushort group, ref uint value)
    {
        var result = Record(new(nameof(AIO_GetDIDWord), card, Group: group));
        if (!SkipRefWrites.Contains(nameof(AIO_GetDIDWord))) value = Inputs;
        return result;
    }

    public static int AIO_GetDODWord(ushort card, ushort group, ref uint value)
    {
        var result = Record(new(nameof(AIO_GetDODWord), card, Group: group));
        if (!SkipRefWrites.Contains(nameof(AIO_GetDODWord))) value = Outputs;
        return result;
    }

    public static int AIO_PutDOBit(ushort card, ushort channel, ushort value)
    {
        var result = Record(new(nameof(AIO_PutDOBit), card, channel, Value: value));
        if (result is 0 or tmcDef.TMC_ST_OK && ErrorCode == tmcDef.ERR_SUCCESS && !SuppressOutputWrites)
            Outputs = value == 0 ? Outputs & ~(1U << channel) : Outputs | (1U << channel);
        return result;
    }
}
