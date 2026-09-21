using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.Virtual.Tests;

// Valid replies with independently controlled RUN feedback and command readback.
internal sealed class AdcControllerStub : IAdcBus
{
    private bool _stopRequested;

    public AdcControllerStub()
    {
        AutomaticResults = new();
    }

    public event Action<AdcFrameDirection, byte[]>? FrameTransferred { add { } remove { } }

    public Queue<AdcFasteningResult> AutomaticResults { get; }
    public int ResultReceives { get; private set; }

    public ushort CurrentPreset { get; set; } = 3;
    public AdcDirection CurrentDirection { get; set; }
    public bool IgnorePresetWrites { get; set; }
    public bool IgnoreDirectionWrites { get; init; }
    public ushort? ResultPreset { get; init; }
    public AdcDirection? ResultDirection { get; init; }
    public int StopPollsRemaining { get; set; }
    public IOException? StopWriteFailure { get; set; }
    public IOException? StopReadFailure { get; init; }
    public IOException? ResultReceiveFailure { get; set; }
    public AdcEventStatus ResultStatus { get; set; } = AdcEventStatus.FasteningOk;
    public ushort ResultError { get; set; }
    public Action? Started { get; init; }
    public bool Running { get; private set; }
    public int StartWrites { get; private set; }
    public int StopWrites { get; private set; }
    public int StopFeedbackReads { get; private set; }
    public bool IsOpen { get; private set; } = true;

    public string PortName => "Controller test bus";

    public int BaudRate => 115200;

    public string[] GetPortNames() { return []; }

    public void Open(string portName, int baudRate) { IsOpen = true; }

    public void Close() { IsOpen = false; }

    public Task<byte[]> ReadDeviceInformationAsync(byte slaveAddress, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new byte[12]);
    }

    public Task<byte[]> CaptureDeviceInformationAsync(byte slaveAddress, int durationMilliseconds, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task WriteRegisterAsync(byte slaveAddress, ushort address, ushort value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch ((AdcRemoteRegister)address)
        {
            case AdcRemoteRegister.Preset when !IgnorePresetWrites:
                CurrentPreset = value;
                break;
            case AdcRemoteRegister.Direction when !IgnoreDirectionWrites:
                CurrentDirection = (AdcDirection)value;
                break;
            case AdcRemoteRegister.RemoteStart:
                if (value != 0)
                {
                    StartWrites++;
                    _stopRequested = false;
                    Running = true;
                    Started?.Invoke();
                }
                else
                {
                    StopWrites++;
                    if (StopWriteFailure is not null)
                        throw StopWriteFailure;
                    _stopRequested = true;
                }
                break;
        }
        return Task.CompletedTask;
    }

    public Task<ushort[]> ReadRegistersAsync(byte slaveAddress, AdcFunctionCode function, ushort address, ushort count, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (address)
        {
            case (ushort)AdcStatusRegister.Preset:
                if (_stopRequested)
                {
                    StopFeedbackReads++;
                    if (StopReadFailure is not null)
                        throw StopReadFailure;
                    if (StopPollsRemaining == 0)
                    {
                        Running = false;
                        _stopRequested = false;
                    }
                    else if (StopPollsRemaining > 0)
                        StopPollsRemaining--;
                }
                return Task.FromResult<ushort[]>([
                    CurrentPreset, 0, 0, (ushort)(Running ? 0 : 1), (ushort)(Running ? 1 : 0), 0, (ushort)CurrentDirection,
                ]);
            case (ushort)AdcResultRegister.EventCount:
                return Task.FromResult(ResultRegisters);
        }
        throw new NotSupportedException();
    }

    public Task<AdcFasteningResult> ReceiveFasteningResultAsync(byte slaveAddress, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResultReceives++;
        if (ResultReceiveFailure is { } failure)
        {
            ResultReceiveFailure = null;
            throw failure;
        }
        return Task.FromResult(AutomaticResults.TryDequeue(out var result)
            ? result
            : AdcFasteningResult.FromRegisters(ResultRegisters));
    }

    private ushort[] ResultRegisters => [
        (ushort)StartWrites, 250, ResultPreset ?? CurrentPreset, 100, 100, 1000, 0, 0, 0, (ushort)StartWrites, ResultError,
        (ushort)(ResultDirection ?? CurrentDirection), (ushort)(StartWrites == 0 ? AdcEventStatus.None : ResultStatus), 0,
    ];
}
