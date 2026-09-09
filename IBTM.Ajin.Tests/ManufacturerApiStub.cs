using System;
using System.Collections.Generic;

// Test assembly only. Production still uses the unchanged manufacturer's declarations.
internal static class AjinSdk
{
    internal sealed record Call(string Operation, int? Module = null, int? Offset = null,
        uint? Value = null, string? Path = null);
    internal sealed record Module(int Inputs, int Outputs, AXT_MODULE Type, int Board = 0, int Position = 0);
    internal static readonly List<Call> Calls = [];
    internal static readonly Dictionary<Call, uint> Results = [];
    internal static readonly Dictionary<int, Module> Modules = [];
    internal static readonly Dictionary<int, uint> Inputs = [];
    internal static readonly Dictionary<int, uint> Outputs = [];
    internal static readonly Dictionary<(int Module, int Offset), uint> InputWords = [];
    internal static uint Presence;
    internal static int ModuleCount;
    internal static Action<Call>? BeforeCall;

    internal static void Reset()
    {
        Calls.Clear();
        Results.Clear();
        Modules.Clear();
        Inputs.Clear();
        Outputs.Clear();
        InputWords.Clear();
        Modules[0] = new(32, 0, AXT_MODULE.AXT_SIO_RDI32RTEX, Position: 0);
        Modules[1] = new(32, 0, AXT_MODULE.AXT_SIO_RDI32RTEX, Position: 1);
        Modules[2] = new(0, 32, AXT_MODULE.AXT_SIO_RDO32RTEX, Position: 2);
        Modules[3] = new(0, 32, AXT_MODULE.AXT_SIO_RDO32RTEX, Position: 3);
        Modules[4] = new(16, 16, AXT_MODULE.AXT_SIO_RDB32RTEX, Position: 4);
        Presence = (uint)AXT_EXISTENCE.STATUS_EXIST;
        ModuleCount = 5;
        BeforeCall = null;
    }

    internal static uint Record(Call call)
    {
        Calls.Add(call);
        BeforeCall?.Invoke(call);
        return Results.GetValueOrDefault(call, (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS);
    }
}

internal static class CAXL
{
    public static uint AxlOpen(int irq) => AjinSdk.Record(new(nameof(AxlOpen), Offset: irq));
    public static uint AxlOpenNoReset(uint irq) => AjinSdk.Record(new(nameof(AxlOpenNoReset), Offset: checked((int)irq)));
    public static int AxlClose() => (int)AjinSdk.Record(new(nameof(AxlClose)));
}

internal static class CAXM
{
    public static uint AxmMotLoadParaAll(string path) => AjinSdk.Record(new(nameof(AxmMotLoadParaAll), Path: path));
}

internal static class CAXD
{
    public static uint AxdInfoIsDIOModule(ref uint status)
    {
        status = AjinSdk.Presence;
        return AjinSdk.Record(new(nameof(AxdInfoIsDIOModule)));
    }

    public static uint AxdInfoGetModuleCount(ref int count)
    {
        count = AjinSdk.ModuleCount;
        return AjinSdk.Record(new(nameof(AxdInfoGetModuleCount)));
    }

    public static uint AxdInfoGetModule(int module, ref int board, ref int position, ref uint type)
    {
        var info = AjinSdk.Modules[module];
        board = info.Board;
        position = info.Position;
        type = (uint)info.Type;
        return AjinSdk.Record(new(nameof(AxdInfoGetModule), module));
    }

    public static uint AxdInfoGetInputCount(int module, ref int count)
    {
        count = AjinSdk.Modules[module].Inputs;
        return AjinSdk.Record(new(nameof(AxdInfoGetInputCount), module));
    }

    public static uint AxdInfoGetOutputCount(int module, ref int count)
    {
        count = AjinSdk.Modules[module].Outputs;
        return AjinSdk.Record(new(nameof(AxdInfoGetOutputCount), module));
    }

    public static uint AxdiReadInportWord(int module, int offset, ref uint value)
    {
        var result = AjinSdk.Record(new(nameof(AxdiReadInportWord), module, offset));
        if (offset < 0 || (offset + 1) * 16 > AjinSdk.Modules[module].Inputs)
            throw new InvalidOperationException("Test detected an input read beyond the module's actual point count.");
        value = AjinSdk.InputWords.GetValueOrDefault((module, offset),
            (AjinSdk.Inputs.GetValueOrDefault(module) >> (offset * 16)) & 0xFFFF);
        return result;
    }

    public static uint AxdiReadInportBit(int module, int offset, ref uint value)
    {
        value = (AjinSdk.Inputs.GetValueOrDefault(module) >> offset) & 1;
        return AjinSdk.Record(new(nameof(AxdiReadInportBit), module, offset));
    }

    public static uint AxdoReadOutportBit(int module, int offset, ref uint value)
    {
        value = (AjinSdk.Outputs.GetValueOrDefault(module) >> offset) & 1;
        return AjinSdk.Record(new(nameof(AxdoReadOutportBit), module, offset));
    }

    public static uint AxdoWriteOutportBit(int module, int offset, uint value)
    {
        var result = AjinSdk.Record(new(nameof(AxdoWriteOutportBit), module, offset, value));
        if (result == (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS)
        {
            var outputs = AjinSdk.Outputs.GetValueOrDefault(module);
            AjinSdk.Outputs[module] = value == 0 ? outputs & ~(1U << offset) : outputs | (1U << offset);
        }
        return result;
    }
}
