using System;

namespace IBTM.AlphaMotion;

public sealed class AlphaMotionController(
    AlphaMotionSettings settings) : IDisposable
{
    private readonly int _controllerNumber = settings.ControllerNumber;
    private readonly int _stationNumber = settings.StationNumber;
    private readonly AlphaMotionCommunicationSpeed _communicationSpeed =
        settings.CommunicationSpeed;
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
        try
        {
            Check(
                nmiMNApi.nmiSetCommSpeed(
                    _controllerNumber,
                    (int)_communicationSpeed),
                nameof(nmiMNApi.nmiSetCommSpeed));
            Check(
                nmiMNApi.nmiSysComm(_controllerNumber),
                nameof(nmiMNApi.nmiSysComm));
            Check(
                nmiMNApi.nmiCyclicBegin(_controllerNumber),
                nameof(nmiMNApi.nmiCyclicBegin));
            Check(
                nmiMNApi.nmiConParamLoad(),
                nameof(nmiMNApi.nmiConParamLoad));
            _initialized = true;
        }
        catch
        {
            nmiMNApi.nmiSysUnload();
            throw;
        }
    }

    public bool ReadInput(int bit)
    {
        var value = 0U;
        Check(
            nmiMNApi.nmiDiGetBit(
                _controllerNumber,
                _stationNumber,
                bit,
                ref value),
            nameof(nmiMNApi.nmiDiGetBit));
        return value != 0;
    }

    public uint ReadInputs()
    {
        var value = 0U;
        Check(
            nmiMNApi.nmiDiGetData(
                _controllerNumber,
                _stationNumber,
                ref value),
            nameof(nmiMNApi.nmiDiGetData));
        return value;
    }

    public bool ReadOutput(int bit)
    {
        var value = 0U;
        Check(
            nmiMNApi.nmiDoGetBit(
                _controllerNumber,
                _stationNumber,
                bit,
                ref value),
            nameof(nmiMNApi.nmiDoGetBit));
        return value != 0;
    }

    public void WriteOutput(int bit, bool value) =>
        Check(
            nmiMNApi.nmiDoSetBit(
                _controllerNumber,
                _stationNumber,
                bit,
                value ? 1U : 0U),
            nameof(nmiMNApi.nmiDoSetBit));

    public void Dispose()
    {
        if (!_initialized)
        {
            return;
        }

        try
        {
            Check(
                nmiMNApi.nmiCyclicEnd(_controllerNumber),
                nameof(nmiMNApi.nmiCyclicEnd));
        }
        finally
        {
            Check(
                nmiMNApi.nmiSysUnload(),
                nameof(nmiMNApi.nmiSysUnload));
            _initialized = false;
        }
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
