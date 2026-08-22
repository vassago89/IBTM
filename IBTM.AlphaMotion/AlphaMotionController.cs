using System;

namespace IBTM.AlphaMotion;

public sealed class AlphaMotionController(
    AlphaMotionSettings settings) : IDisposable
{
    private bool _initialized;

    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        var controllerCount = 0;
        Check(
            nmiMNApi.nmiSysLoad(nmiMNApiDefs.TMC_FALSE, ref controllerCount),
            nameof(nmiMNApi.nmiSysLoad));
        Check(
            nmiMNApi.nmiSetCommSpeed(
                settings.ControllerNumber,
                settings.CommunicationSpeed),
            nameof(nmiMNApi.nmiSetCommSpeed));
        Check(
            nmiMNApi.nmiSysComm(settings.ControllerNumber),
            nameof(nmiMNApi.nmiSysComm));
        Check(
            nmiMNApi.nmiCyclicBegin(settings.ControllerNumber),
            nameof(nmiMNApi.nmiCyclicBegin));
        Check(
            nmiMNApi.nmiConParamLoad(),
            nameof(nmiMNApi.nmiConParamLoad));
        _initialized = true;
    }

    public bool ReadInput(int bit)
    {
        var value = 0U;
        Check(
            nmiMNApi.nmiDiGetBit(
                settings.ControllerNumber,
                settings.StationNumber,
                bit,
                ref value),
            nameof(nmiMNApi.nmiDiGetBit));
        return value != 0;
    }

    public bool ReadOutput(int bit)
    {
        var value = 0U;
        Check(
            nmiMNApi.nmiDoGetBit(
                settings.ControllerNumber,
                settings.StationNumber,
                bit,
                ref value),
            nameof(nmiMNApi.nmiDoGetBit));
        return value != 0;
    }

    public void WriteOutput(int bit, bool value) =>
        Check(
            nmiMNApi.nmiDoSetBit(
                settings.ControllerNumber,
                settings.StationNumber,
                bit,
                value ? 1U : 0U),
            nameof(nmiMNApi.nmiDoSetBit));

    public void Dispose()
    {
        if (!_initialized)
        {
            return;
        }

        Check(
            nmiMNApi.nmiCyclicEnd(settings.ControllerNumber),
            nameof(nmiMNApi.nmiCyclicEnd));
        Check(
            nmiMNApi.nmiSysUnload(),
            nameof(nmiMNApi.nmiSysUnload));
        _initialized = false;
    }

    private static void Check(int result, string operation)
    {
        if (result < 0)
        {
            throw new InvalidOperationException(
                $"{operation} failed with AlphaMotion result {result}.");
        }
    }
}
