using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;

namespace IBTM.Conveyor;

public sealed partial class MainConveyor : AutoUnit
{
    private readonly IIoService _io;
    private readonly ConveyorSettings _settings;
    private readonly OperationCancellation _operations;
    private readonly ConveyorStation _placement;
    private readonly ConveyorStation _fastening;
    private readonly InspectionStation _inspection;
    private readonly UnitSettings _units;
    private OperationCancellation.Operation? _runCancellation;
    private bool _repeat;
    // Commissioning inputs, kept only for this application session.
    private volatile bool _testUpstreamCarrierAvailable;
    private volatile bool _testDownstreamReady;

    public MainConveyor(
        IIoService io,
        ConveyorSettings settings,
        OperationCancellation operations,
        ConveyorStation placement,
        ConveyorStation fastening,
        InspectionStation inspection,
        UnitSettings units)
    {
        _io = io;
        _settings = settings;
        _operations = operations;
        _placement = placement;
        _fastening = fastening;
        _inspection = inspection;
        _units = units;
        io.InputChanged += OnInputChanged;
        placement.Changed += NotifyChanged;
        fastening.Changed += NotifyChanged;
        inspection.Changed += NotifyChanged;
    }

    public override event Action? Changed;

    private void SetSmemaOutput(OutputIo output, bool value)
    {
        // The selector contact is ON in teaching/manual mode; direct OUTPUTS remain available.
        if (!_io.GetInput(InputIo.AutoMode))
            _io.SetOutput(output, value);
    }

    public MainConveyorState State => Step is MainConveyorState step ? step : GetNextStep(RunCommandOn);

    private bool IsNgTransferRequired => _units.Inspection && _inspection.RouteToNg;

    public bool UpstreamCarrierAvailable
    {
        get
        {
            return _io.GetInput(InputIo.AutoMode)
                ? _testUpstreamCarrierAvailable
                : _io.GetInput(InputIo.MainConveyorAvailableFromFront2);
        }
    }

    public bool DownstreamReady
    {
        get
        {
            return _io.GetInput(InputIo.AutoMode)
                ? _testDownstreamReady
                : _io.GetInput(InputIo.MainConveyorReadyFromRear);
        }
    }

    public bool TestUpstreamCarrierAvailable
    {
        get => _testUpstreamCarrierAvailable;
        set
        {
            // The selector contact is ON in teaching/manual mode.
            value = value && _io.IsReady && _io.GetInput(InputIo.AutoMode);
            if (_testUpstreamCarrierAvailable == value)
                return;
            _testUpstreamCarrierAvailable = value;
            Changed?.Invoke();
        }
    }

    public bool TestDownstreamReady
    {
        get => _testDownstreamReady;
        set
        {
            value = value && _io.IsReady && _io.GetInput(InputIo.AutoMode);
            if (_testDownstreamReady == value)
                return;
            _testDownstreamReady = value;
            Changed?.Invoke();
        }
    }

    public bool RunCommandOn => _io.GetOutput(OutputIo.MainConveyorRun);

    public bool EntryCarrierDetected => _io.GetInput(InputIo.MainConveyorEntryCarrierDetected);


    public async Task RunMotorAsync(CancellationToken cancellationToken = default)
    {
        using var runCancellation = BeginConveyorOperation(cancellationToken);
        cancellationToken = runCancellation.Token;
        using var motor = new ConveyorRun(_io, OutputIo.MainConveyorRun, cancellationToken, OutputIo.MainConveyorReadyToFront2, OutputIo.MainConveyorAvailableToRear);
        try
        {
            StartMotor(cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            motor.Failure = exception;
        }
    }

    private OperationCancellation.Operation BeginConveyorOperation(CancellationToken cancellationToken)
    {
        Stop();
        var operation = _operations.Link(cancellationToken);
        _runCancellation = operation;
        operation.Disposed += () =>
        {
            if (ReferenceEquals(_runCancellation, operation))
                _runCancellation = null;
        };
        return operation;
    }

    public void Stop()
    {
        var run = _runCancellation;
        _runCancellation = null;
        Exception? failure = null;
        try
        {
            run?.Cancel();
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            StopOutputs(failure,
                OutputIo.MainConveyorRun,
                OutputIo.MainConveyorReadyToFront2,
                OutputIo.MainConveyorAvailableToRear);
        }
    }

    private void StopOutputs(Exception? operationFailure, params OutputIo[] outputs)
    {
        List<Exception>? failures = null;
        foreach (var output in outputs)
        {
            try
            {
                _io.SetOutput(output, false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null && operationFailure is not null)
            failures.Insert(0, operationFailure);
        if (failures?.Count == 1)
            ExceptionDispatchInfo.Throw(failures[0]);
        if (failures is not null)
            throw new AggregateException("Main conveyor outputs could not all be stopped.", failures);
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input == InputIo.AutoMode && !value)
        {
            _testUpstreamCarrierAvailable = false;
            _testDownstreamReady = false;
        }

        if (input is InputIo.AutoMode
            or InputIo.MainConveyorAvailableFromFront2
            or InputIo.MainConveyorReadyFromRear
            or InputIo.MainConveyorEntryCarrierDetected)
        {
            Changed?.Invoke();
        }
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }
}
