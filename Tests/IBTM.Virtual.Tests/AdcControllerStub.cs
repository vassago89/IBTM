using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Virtual.Tests;

// Valid replies with independently controlled RUN feedback and command readback.
internal sealed class AdcControllerStub : IAdcBus, IDisposable
{
    private FasteningHead _head;
    private int _fasteningVersion;

    public AdcControllerStub()
    {
        Monitor = new(this);
        ResultReplies = new();
        RunReplies = new();
        TorqueCompensations = new() { [1] = 100, [2] = 80, [3] = 100 };
        RegisterWrites = [];
        Result = AdcFasteningResult.FromRegisters(ResultRegisters);
    }

    public AdcStatusMonitor Monitor { get; }

    public event Action<AdcFrameDirection, byte[]>? FrameTransferred { add { } remove { } }

    public Queue<AdcFasteningResult> ResultReplies { get; }
    public int ResultReads { get; private set; }
    private AdcFasteningResult Result { get; set; }
    public bool SuppressTorqueCurve { get; set; }
    public List<(ushort Address, ushort Value)> RegisterWrites { get; }
    public (ushort Address, ushort Value, Exception Error)? RegisterWriteFailure { get; set; }
    public Exception? GraphRequestFailure { get; set; }
    public int GraphRequests { get; private set; }
    public Queue<bool> RunReplies { get; }
    public IOException? StatusReadFailure { get; set; }
    public string? StatusRejection { get; set; }
    public Task? StatusReadBarrier { get; set; }
    public int EventReads { get; private set; }
    public int StatusReads { get; private set; }
    public Dictionary<ushort, ushort> TorqueCompensations { get; }
    public int CompensationReads { get; private set; }
    public Exception? CompensationReadFailure { get; init; }
    public Task? CompensationReadBarrier { get; init; }

    private ushort CurrentPreset { get; set; } = 3;
    public ushort? ReportedPreset { get; set; }
    public ushort CurrentAlarm { get; set; }
    public bool NotReady { get; set; }
    public bool IgnoreReset { get; init; }
    public int ResetWrites { get; private set; }
    private AdcDirection CurrentDirection { get; set; }
    public ushort? ResultPreset { get; init; }
    public AdcDirection? ResultDirection { get; init; }
    public bool IgnoreStop { get; init; }
    public IOException? StopWriteFailure { get; set; }
    public Exception? NextResultReadFailure { get; set; }
    public IOException? BaselineReadFailure { get; init; }
    public bool SuppressCompletion
    {
        get;
        set
        {
            field = value;
            if (!value && Running)
                _ = CompleteAsync(_fasteningVersion);
        }
    }
    public AdcEventStatus ResultStatus { get; set; } = AdcEventStatus.FasteningOk;
    public ushort ResultError { get; set; }
    public Action? Started { get; init; }
    public bool Running { get; private set; }
    public int StartWrites { get; private set; }
    public int StopWrites { get; private set; }
    public bool IsOpen { get; private set; }

    public string PortName => "Controller test bus";

    public int BaudRate => 115200;

    public string[] PortNames => [];

    public void Open(string portName, int baudRate, byte slaveAddress = 0)
    {
        if (IsOpen)
            return;
        // The physical bus clears the old connection before opening the port.
        Close();
        IsOpen = true;
    }

    public void Close()
    {
        Monitor.Stop();
        IsOpen = false;
    }

    public void Dispose()
    {
        Close();
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
        RegisterWrites.Add((address, value));
        if (RegisterWriteFailure is { } failure && failure.Address == address && failure.Value == value)
            throw failure.Error;
        ApplyControl(address, value);
        return Task.CompletedTask;
    }

    private void ApplyControl(ushort address, ushort value)
    {
        switch ((AdcRemoteRegister)address)
        {
            case AdcRemoteRegister.AlarmReset:
                ResetWrites++;
                if (!IgnoreReset)
                    CurrentAlarm = 0;
                break;
            case AdcRemoteRegister.Preset:
                CurrentPreset = value;
                break;
            case AdcRemoteRegister.Direction:
                CurrentDirection = (AdcDirection)value;
                break;
            case AdcRemoteRegister.RemoteStart:
                if (value != 0)
                {
                    StartWrites++;
                    Running = true;
                    var version = ++_fasteningVersion;
                    Started?.Invoke();
                    _ = CompleteAsync(version);
                }
                else
                {
                    StopWrites++;
                    if (StopWriteFailure is not null)
                        throw StopWriteFailure;
                    if (!IgnoreStop)
                        Running = false;
                    _fasteningVersion++;
                }
                break;
        }
    }

    private async Task CompleteAsync(int version)
    {
        await Task.Delay(40);
        if (!IsOpen || version != _fasteningVersion || SuppressCompletion)
            return;
        _fasteningVersion++;
        Running = false;
        Result = ResultReplies.TryDequeue(out var queued)
            ? queued : AdcFasteningResult.FromRegisters(ResultRegisters);
        if (Result.Status == AdcEventStatus.Error)
            CurrentAlarm = Result.Error;
    }

    public Task<byte[]> RequestTorqueCurveAsync(byte slaveAddress, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GraphRequests++;
        if (GraphRequestFailure is { } failure)
            throw failure;
        if (Monitor.IsTorqueCurveMonitoringRequested && !SuppressTorqueCurve)
        {
            var result = AdcFasteningResult.FromRegisters(ResultRegisters);
            Monitor.ReceiveTorqueCurve(new(Stopwatch.GetTimestamp(), 5, [0, 0.5, result.Torque],
                result.FasteningTimeMilliseconds, result.TargetTorque, result.Torque, result.ScrewCount, result.Error));
        }
        return Task.FromResult(AdcRtuFrame.Build(slaveAddress, AdcFunctionCode.RequestTorqueCurve, [2, 0, 0]));
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
            ApplyControl((ushort)AdcRemoteRegister.RemoteStart, (ushort)(value ? 1 : 0));
        else if (output == (pickup ? OutputIo.PickupBoltReset : OutputIo.ShootingBoltReset) && value)
            ApplyControl((ushort)AdcRemoteRegister.AlarmReset, 1);
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

    public async Task<(AdcControllerStatus? Status, string? Rejection)> ReadControllerStatusAsync(
        byte slaveAddress, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (StatusRejection is { } rejection)
        {
            StatusReads++;
            return (null, rejection);
        }
        var values = await ReadRegistersAsync(slaveAddress, AdcFunctionCode.ReadInputRegisters,
            (ushort)AdcStatusRegister.Preset, AdcControllerStatus.RegisterCount, cancellationToken);
        return (AdcControllerStatus.FromRegisters(values), null);
    }

    public async Task<ushort[]> ReadRegistersAsync(byte slaveAddress, AdcFunctionCode function, ushort address, ushort count, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (function == AdcFunctionCode.ReadHoldingRegisters && count == 1 && address is 15 or 30 or 45)
        {
            CompensationReads++;
            if (CompensationReadBarrier is { } barrier)
                await barrier.WaitAsync(cancellationToken);
            if (CompensationReadFailure is { } failure)
                throw failure;
            return [TorqueCompensations[(ushort)(address / 15)]];
        }
        switch (address)
        {
            case (ushort)AdcStatusRegister.Preset:
                StatusReads++;
                if (StatusReadBarrier is { } barrier)
                    await barrier.WaitAsync(cancellationToken);
                if (StatusReadFailure is { } statusFailure)
                    throw statusFailure;
                if (Running && RunReplies.TryDequeue(out var running))
                    Running = running;
                return [
                    ReportedPreset ?? CurrentPreset, 0, 0, (ushort)(NotReady || Running || CurrentAlarm != 0 ? 0 : 1),
                    (ushort)(Running ? 1 : 0), CurrentAlarm, (ushort)CurrentDirection,
                ];
            case (ushort)AdcResultRegister.EventCount when count == 1:
                EventReads++;
                if (BaselineReadFailure is { } baselineFailure)
                    throw baselineFailure;
                return [Result.EventCount];
            case (ushort)AdcResultRegister.EventCount:
                return Result.Registers!;
        }
        throw new NotSupportedException();
    }

    public Task<AdcFasteningResult> ReadFasteningResultAsync(byte slaveAddress, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResultReads++;
        if (NextResultReadFailure is { } failure)
        {
            NextResultReadFailure = null;
            throw failure;
        }
        return Task.FromResult(Result);
    }

    private ushort[] ResultRegisters
    {
        get
        {
            return [
                (ushort)StartWrites, 250, ResultPreset ?? CurrentPreset, 100, 100, 1000, 0, 0, 0, (ushort)StartWrites, ResultError,
                (ushort)(ResultDirection ?? CurrentDirection), (ushort)(StartWrites == 0 ? AdcEventStatus.None : ResultStatus), 0,
            ];
        }
    }
}
