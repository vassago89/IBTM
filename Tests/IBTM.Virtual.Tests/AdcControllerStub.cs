using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;
using IBTM.Core;
using IBTM.Virtual;

namespace IBTM.Virtual.Tests;

// Valid replies with independently controlled RUN feedback and command readback.
internal sealed class AdcControllerStub : IAdcBus
{
    private FasteningHead _head;
    private bool _stopRequested;
    private bool _resetRequested;

    public AdcControllerStub()
    {
        ResultReplies = new();
    }

    public event Action<AdcFrameDirection, byte[]>? FrameTransferred { add { } remove { } }

    public Queue<AdcFasteningResult> ResultReplies { get; }
    public int ResultReads { get; private set; }
    public int ResultPolls { get; private set; }
    public int StatusReads { get; private set; }

    public ushort CurrentPreset { get; set; } = 3;
    public ushort CurrentAlarm { get; set; }
    public bool NotReady { get; set; }
    public int ResetPollsRemaining { get; set; }
    public int ResetWrites { get; private set; }
    public AdcDirection CurrentDirection { get; set; }
    public bool IgnorePresetWrites { get; set; }
    public bool IgnoreDirectionWrites { get; init; }
    public ushort? ResultPreset { get; init; }
    public AdcDirection? ResultDirection { get; init; }
    public int StopPollsRemaining { get; set; }
    public IOException? StopWriteFailure { get; set; }
    public int StopWriteFailuresRemaining { get; set; } = -1;
    public IOException? StopReadFailure { get; init; }
    public IOException? ResultReadFailure { get; set; }
    public bool SuppressCompletion { get; set; }
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

    public string[] GetPortNames()
    {
        return [];
    }

    public void Open(string portName, int baudRate)
    {
        IsOpen = true;
    }

    public void Close()
    {
        IsOpen = false;
    }

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
        ApplyControl(address, value);
        return Task.CompletedTask;
    }

    private void ApplyControl(ushort address, ushort value)
    {
        switch ((AdcRemoteRegister)address)
        {
            case AdcRemoteRegister.AlarmReset:
                ResetWrites++;
                _resetRequested = true;
                break;
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
                    if (StopWriteFailure is not null && StopWriteFailuresRemaining != 0)
                    {
                        if (StopWriteFailuresRemaining > 0)
                            StopWriteFailuresRemaining--;
                        throw StopWriteFailure;
                    }
                    _stopRequested = true;
                }
                break;
        }
    }

    public void BindIo(VirtualIoService io, FasteningHead head)
    {
        _head = head;
        io.OutputChanged += OnOutputChanged;
    }

    private void OnOutputChanged(OutputIo output, bool value)
    {
        var pickup = _head == FasteningHead.Pickup;
        if (output == (pickup ? OutputIo.PickupBoltStart : OutputIo.ShootingBoltStart))
        {
            ApplyControl((ushort)AdcRemoteRegister.RemoteStart, (ushort)(value ? 1 : 0));
            if (!value && StopPollsRemaining == 0)
                Running = false;
        }
        else if (output == (pickup ? OutputIo.PickupBoltReset : OutputIo.ShootingBoltReset) && value)
        {
            ApplyControl((ushort)AdcRemoteRegister.AlarmReset, 1);
            if (ResetPollsRemaining >= 0)
                CurrentAlarm = 0;
        }
        else if (output == (pickup ? OutputIo.PickupBoltDirection : OutputIo.ShootingBoltDirection))
            ApplyControl((ushort)AdcRemoteRegister.Direction, (ushort)(value ? 1 : 0));
        else if (value)
        {
            OutputIo[] presets = pickup
                ? [OutputIo.PickupBoltPreset1, OutputIo.PickupBoltPreset2, OutputIo.PickupBoltPreset3]
                : [OutputIo.ShootingBoltPreset1, OutputIo.ShootingBoltPreset2, OutputIo.ShootingBoltPreset3];
            var index = Array.IndexOf(presets, output);
            if (index >= 0)
                ApplyControl((ushort)AdcRemoteRegister.Preset, (ushort)(index + 1));
        }
    }

    public Task<ushort[]> ReadRegistersAsync(byte slaveAddress, AdcFunctionCode function, ushort address, ushort count, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (address)
        {
            case (ushort)AdcStatusRegister.Preset:
                StatusReads++;
                if (StopWrites > 0 && StopReadFailure is not null)
                    throw StopReadFailure;
                if (_stopRequested)
                {
                    StopFeedbackReads++;
                    if (StopPollsRemaining == 0)
                    {
                        Running = false;
                        _stopRequested = false;
                    }
                    else if (StopPollsRemaining > 0)
                        StopPollsRemaining--;
                }
                if (_resetRequested)
                {
                    if (ResetPollsRemaining == 0)
                    {
                        CurrentAlarm = 0;
                        _resetRequested = false;
                    }
                    else if (ResetPollsRemaining > 0)
                        ResetPollsRemaining--;
                }
                return Task.FromResult<ushort[]>([
                    CurrentPreset, 0, 0, (ushort)(NotReady || Running || CurrentAlarm != 0 ? 0 : 1),
                    (ushort)(Running ? 1 : 0), CurrentAlarm, (ushort)CurrentDirection,
                ]);
            case (ushort)AdcResultRegister.EventCount:
                return Task.FromResult(ResultRegisters);
        }
        throw new NotSupportedException();
    }

    public Task<AdcFasteningResult> ReceiveFasteningResultAsync(byte slaveAddress, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("This controller only returns results when queried.");
    }

    public Task<AdcFasteningResult> ReadFasteningResultAsync(byte slaveAddress, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResultReads++;
        var received = AdcFasteningResult.FromRegisters(ResultRegisters);
        if (!Running || _stopRequested)
            return Task.FromResult(received);
        ResultPolls++;
        if (ResultReadFailure is { } failure)
        {
            ResultReadFailure = null;
            throw failure;
        }
        if (SuppressCompletion)
            received = received with { Status = AdcEventStatus.None };
        else if (ResultReplies.TryDequeue(out var result))
            received = result;
        if (received.Status == AdcEventStatus.Error)
        {
            CurrentAlarm = received.Error;
        }
        return Task.FromResult(received);
    }

    private ushort[] ResultRegisters => [
        (ushort)StartWrites, 250, ResultPreset ?? CurrentPreset, 100, 100, 1000, 0, 0, 0, (ushort)StartWrites, ResultError,
        (ushort)(ResultDirection ?? CurrentDirection), (ushort)(StartWrites == 0 ? AdcEventStatus.None : ResultStatus), 0,
    ];
}
