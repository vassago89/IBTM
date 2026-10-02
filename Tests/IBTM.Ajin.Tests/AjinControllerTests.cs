using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Ajin;
using IBTM.Core;
using IBTM.Device;
using Xunit;
using Microsoft.Extensions.Logging;

namespace IBTM.Ajin.Tests;

public sealed class AjinControllerTests
{
    public AjinControllerTests()
    {
        AjinSdk.Reset();
    }

    [Fact]
    public void UnassignedSupplyFeedbackStaysUnknownAndNeitherCoilIsWrittenWithoutBothAddresses()
    {
        Shared.TMCAEDLL.Reset();
        using var alpha = new IBTM.AlphaMotion.AlphaMotionController(new());
        using var ajin = new AjinController(new());
        var fixer = new OutputHardware
        {
            Number = 24,
            OffNumber = -1,
            Feedback = new(InputIo.PcbSupplyIpmFixerForward, InputIo.PcbSupplyIpmFixerBackward),
        };
        using var io = new PhysicalIoService(
            alpha,
            ajin,
            new System.Collections.Generic.Dictionary<InputIo, int>
            {
                [InputIo.PcbSupplyGripperOpen] = 25,
                [InputIo.PcbSupplyIpmFixerBackward] = -1,
            },
            new System.Collections.Generic.Dictionary<OutputIo, OutputHardware>
            {
                [OutputIo.PcbSupplyIpmFixerForward] = fixer,
            },
            new());
        AjinSdk.Inputs[0] = 1U << 9; // DI-109: opening the gripper must not confirm IPM retraction.
        io.Initialize();
        io.RefreshInputs();
        io.CheckReady();
        Assert.True(io.IsReady);
        Assert.True(io.GetInput(InputIo.PcbSupplyGripperOpen));
        var error = Assert.Throws<IOException>(() => io.GetInput(InputIo.PcbSupplyIpmFixerBackward));
        Assert.Contains("no configured input address", error.Message);

        var reads = Shared.TMCAEDLL.Calls.Count + AjinSdk.Calls.Count;
        Assert.Throws<IOException>(() => io.SetOutput(OutputIo.PcbSupplyIpmFixerForward, true));
        Assert.Throws<IOException>(() => io.SetOutput(OutputIo.PcbSupplyIpmFixerForward, false));
        Assert.Equal(reads, Shared.TMCAEDLL.Calls.Count + AjinSdk.Calls.Count);

        // Confirmed single-coil configuration: retract clears DO-108 only.
        fixer.OffNumber = null;
        AjinSdk.Calls.Clear();
        io.SetOutput(OutputIo.PcbSupplyIpmFixerForward, true);
        io.SetOutput(OutputIo.PcbSupplyIpmFixerForward, false);
        Assert.Equal(new[]
        {
            new AjinSdk.Call("AxdoWriteOutportBit", Module: 2, Offset: 8, Value: 1),
            new AjinSdk.Call("AxdoWriteOutportBit", Module: 2, Offset: 8, Value: 0),
        }, AjinSdk.Calls);
        Assert.Throws<IOException>(() => io.GetInput(InputIo.PcbSupplyIpmFixerBackward));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidSpeedDoesNotIssueMoveOrHomeCommands(bool home)
    {
        using var controller = new AjinController(new());
        AjinSdk.MotionAxes[9] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1);
        var motion = new AjinMotionService(
            controller, new() { Number = 9 }, null, null,
            new(), new(), new());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => home
            ? motion.HomeAsync(MotionAxis.X, 0)
            : motion.MoveAxisAsync(MotionAxis.X, 10, 0));
        Assert.DoesNotContain(AjinSdk.Calls, call =>
            call.Operation.StartsWith("AxmMove", StringComparison.Ordinal)
            || call.Operation.StartsWith("AxmHomeSet", StringComparison.Ordinal));
    }

    [Fact]
    public void UnassignedOutputStaysUnknownWithoutReadingAnInvalidSdkAddress()
    {
        Shared.TMCAEDLL.Reset();
        using var alpha = new IBTM.AlphaMotion.AlphaMotionController(new());
        using var ajin = new AjinController(new());
        var hardware = new MachineHardwareSettings();
        hardware.Inputs.Clear();
        hardware.Outputs.Clear();
        hardware.Outputs.Add(OutputIo.TowerLampRed, new() { Number = -1 });
        hardware.Outputs.Add(OutputIo.TowerLampGreen, new() { Number = 0 });
        using var io = new PhysicalIoService(
            alpha, ajin, hardware.Inputs, hardware.Outputs, new());
        io.Initialize();
        io.CheckReady();
        var signals = new IoSignals([hardware], io);
        signals.RefreshOutputs();
        var unassigned = signals.Outputs[OutputIo.TowerLampRed];
        Assert.Null(unassigned.Number);
        Assert.Equal("—", unassigned.Address);
        Assert.Null(unassigned.IsOn);
        Assert.False(unassigned.IsMatched);
        Assert.False(signals.Outputs[OutputIo.TowerLampGreen].IsOn);

        var calls = Shared.TMCAEDLL.Calls.Count + AjinSdk.Calls.Count;
        Assert.Throws<IOException>(() => io.GetOutput(OutputIo.TowerLampRed));
        Assert.Throws<IOException>(() => io.SetOutput(OutputIo.TowerLampRed, false));
        Assert.Equal(calls, Shared.TMCAEDLL.Calls.Count + AjinSdk.Calls.Count);
    }

    [Fact]
    public async Task OppositeOutputCommandsCannotInterleaveTheTwoCoilWrites()
    {
        Shared.TMCAEDLL.Reset();
        using var alpha = new IBTM.AlphaMotion.AlphaMotionController(new());
        using var ajin = new AjinController(new());
        using var io = new PhysicalIoService(alpha, ajin,
            new System.Collections.Generic.Dictionary<InputIo, int>(),
            new System.Collections.Generic.Dictionary<OutputIo, OutputHardware>
            {
                [OutputIo.NgShuttleDown] = new() { Number = 0, OffNumber = 16 },
            }, new());
        io.Initialize();
        using var releaseFirstWrite = new ManualResetEventSlim();
        using var oppositeWrite = new ManualResetEventSlim();
        var firstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oppositeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AjinSdk.BeforeCall = call =>
        {
            if (call is { Operation: "AxdoWriteOutportBit", Value: 0 })
            {
                firstWrite.TrySetResult();
                if (!releaseFirstWrite.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The first coil write was not released.");
            }
        };
        Shared.TMCAEDLL.BeforeCall = operation =>
        {
            if (operation == "AIO_PutDOBit")
                oppositeWrite.Set();
        };
        var forward = Task.Run(() => io.SetOutput(OutputIo.NgShuttleDown, true));
        var backward = Task.CompletedTask;
        var interleaved = false;
        try
        {
            await firstWrite.Task.WaitAsync(TimeSpan.FromSeconds(2));
            backward = Task.Run(() =>
            {
                oppositeStarted.SetResult();
                io.SetOutput(OutputIo.NgShuttleDown, false);
            });
            await oppositeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            interleaved = oppositeWrite.Wait(TimeSpan.FromMilliseconds(200));
        }
        finally
        {
            releaseFirstWrite.Set();
            await Task.WhenAll(forward, backward).WaitAsync(TimeSpan.FromSeconds(5));
            AjinSdk.BeforeCall = null;
            Shared.TMCAEDLL.BeforeCall = null;
        }
        Assert.False(interleaved);
        Assert.Equal(0U, Shared.TMCAEDLL.Outputs & 1U);
        Assert.Equal(1U, AjinSdk.Outputs[2] & 1U);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PhysicalInputScanPublishesBothProvidersAndRequiresExplicitRecovery(bool failAlphaMotion)
    {
        Shared.TMCAEDLL.Reset();
        using var alpha = new IBTM.AlphaMotion.AlphaMotionController(new());
        using var ajin = new AjinController(new());
        var inputs = new System.Collections.Generic.Dictionary<InputIo, int>
        {
            [InputIo.EmergencyStop1Pressed] = 0,
        };
        inputs[InputIo.PcbSupplyPcbDetected] = IBTM.AlphaMotion.AlphaMotionController.ChannelCount;
        using var io = new PhysicalIoService(
            alpha,
            ajin,
            inputs,
            new System.Collections.Generic.Dictionary<OutputIo, OutputHardware>(),
            new());
        io.Initialize();
        var changes = 0;
        void OnInputChanged(InputIo input, bool value)
        {
            changes++;
            Assert.True(io.GetInput(InputIo.EmergencyStop1Pressed));
            Assert.True(io.GetInput(InputIo.PcbSupplyPcbDetected));
        }

        io.InputChanged += OnInputChanged;
        Shared.TMCAEDLL.Inputs = 1;
        AjinSdk.Inputs[0] = 1;
        io.RefreshInputs();
        Assert.Equal(inputs.Count, changes);
        Assert.Throws<IOException>(() => io.GetInput(InputIo.PcbPlacementCarrierPresent));
        io.InputChanged -= OnInputChanged;
        io.InputChanged += (_, _) => changes++;

        Shared.TMCAEDLL.Inputs = 0;
        if (failAlphaMotion)
            Shared.TMCAEDLL.Errors["AIO_GetDIDWord"] = Shared.tmcDef.ERR_INVALID_PARAMETER;
        else
            AjinSdk.Results[new("AxdiReadInportWord", 0, 0)] = (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;
        Exception? fault = null;
        io.Faulted += error =>
        {
            fault = error;
            Assert.False(io.IsReady);
            Assert.Throws<IOException>(() => io.GetInput(InputIo.EmergencyStop1Pressed));
        };
        Assert.Same(Assert.Throws<IOException>(io.RefreshInputs), fault);
        Assert.Equal(inputs.Count, changes); // A partial provider scan never publishes input changes.

        Shared.TMCAEDLL.Errors.Clear();
        AjinSdk.Results.Clear();
        var reads = Shared.TMCAEDLL.Calls.Count + AjinSdk.Calls.Count;
        io.RefreshInputs();
        Assert.Equal(reads, Shared.TMCAEDLL.Calls.Count + AjinSdk.Calls.Count);
        Assert.False(io.IsReady);
        AjinSdk.Results[new("AxdiReadInportBit", 0, 0)] = (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;
        Assert.Throws<IOException>(io.Initialize);
        Assert.False(io.IsReady);
        Assert.Equal(inputs.Count, changes);
        AjinSdk.Results.Clear();
        io.Initialize();
        Assert.True(io.IsReady);
        Assert.False(io.GetInput(InputIo.EmergencyStop1Pressed));
        Assert.True(io.GetInput(InputIo.PcbSupplyPcbDetected));
        Assert.Equal(inputs.Count * 2 - 1, changes);
        io.RefreshInputs();
        Assert.Equal(inputs.Count * 2 - 1, changes);
    }

    [Fact]
    public void CarrierFeedbackRecoveryKeepsWorkUntilExplicitClear()
    {
        Shared.TMCAEDLL.Reset();
        using var alpha = new IBTM.AlphaMotion.AlphaMotionController(new());
        using var ajin = new AjinController(new());
        var rtex = IBTM.AlphaMotion.AlphaMotionController.ChannelCount;
        var inputs = new System.Collections.Generic.Dictionary<InputIo, int>
        {
            [InputIo.EmergencyStop1Pressed] = 0,
            [InputIo.InspectionHeatSink1Present] = rtex,
            [InputIo.InspectionBackupPlateUp] = rtex + 1,
            [InputIo.InspectionStopperDown] = rtex + 2,
            [InputIo.InspectionHeatSink2Present] = rtex + 3,
            [InputIo.InspectionBackupPlateDown] = rtex + 4,
            [InputIo.InspectionStopperUp] = rtex + 5,
        };
        using var io = new PhysicalIoService(
            alpha, ajin, inputs, new System.Collections.Generic.Dictionary<OutputIo, OutputHardware>(), new());
        var work = ConveyorStation.CreateInspection(io);
        AjinSdk.Inputs[0] = 0b111;
        io.Initialize();
        var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        assembly.RecordBolt(FasteningHead.Shooting, Guid.NewGuid(), new BoltResult(false, 1.25));
        work.Complete(work.CurrentJob);
        Assert.True(work.Completed);
        var job = work.CurrentJob;
        AjinSdk.Inputs[0] = 0b1110; // Heat Sink 1 -> 2 in one complete physical scan.
        io.RefreshInputs();
        Assert.Same(job, work.CurrentJob);
        Assert.Same(assembly, Assert.Single(work.Assemblies));
        Assert.True(work.Completed);
        AjinSdk.Inputs[0] = 0b110;
        io.RefreshInputs();
        Assert.False(work.CarrierPresent);

        Shared.TMCAEDLL.Errors["AIO_GetDIDWord"] = Shared.tmcDef.ERR_INVALID_PARAMETER;
        Assert.Throws<IOException>(io.RefreshInputs);
        AjinSdk.Inputs[0] = 0b1111; // Both heat sinks arrive while feedback is unavailable.
        Shared.TMCAEDLL.Errors.Clear();
        io.Initialize();

        Assert.True(work.CarrierSeated);
        Assert.Same(job, work.CurrentJob);
        Assert.True(work.Completed);
        Assert.Same(assembly, Assert.Single(work.Assemblies));
        Assert.Throws<InvalidOperationException>(work.ClearJob);

        AjinSdk.Inputs[0] = 0b110;
        io.RefreshInputs();
        work.ClearJob();
        AjinSdk.Inputs[0] = 0b1111;
        io.RefreshInputs();

        Assert.NotSame(job, work.CurrentJob);
        Assert.False(work.Completed);
        Assert.Empty(work.Assemblies);
    }

    [Fact]
    public void MotionUsesLiveFeedbackAndConfiguresUnitsOnlyDuringInitialization()
    {
        using var controller = new AjinController(new());
        controller.Initialize();
        AjinSdk.MotionAxes[9] = new(
            Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1, Position: 1200, InMotion: 1);
        var motion = new AjinMotionService(
            controller, new() { Number = 9 }, null, null, new(), new(), new());
        var feedback = new MotionStatus(motion);

        Assert.True(motion.IsReady);
        Assert.True(motion.IsMoving);
        Assert.True(motion.IsMovingHorizontal);
        Assert.Equal(MotionCommand.None, motion.Command); // External motion, not an application command.
        feedback.RefreshMonitorFeedback();
        feedback.RefreshControlFeedback();
        Assert.True(feedback.IsMoving);
        Assert.Equal(AxisCondition.Moving, feedback.Axes[MotionAxis.X].Condition);
        Assert.Equal(1.2, motion.Position.X);

        motion.Initialize();
        Assert.True(motion.IsMoving);
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation.StartsWith("AxmMotSet"));

        AjinSdk.MotionAxes[9] = AjinSdk.MotionAxes[9] with
        {
            InMotion = 0,
            Mechanical = 0,
            Position = 2400
        };
        feedback.RefreshMonitorFeedback();
        feedback.RefreshControlFeedback();
        Assert.False(motion.IsMoving);
        Assert.False(feedback.IsMoving);
        Assert.Equal(AxisCondition.NotInPosition, feedback.Axes[MotionAxis.X].Condition);
        Assert.Equal(2.4, motion.Position.X);

        AjinSdk.MotionAxes[9] = AjinSdk.MotionAxes[9] with { Position = 24, Pulse = 100 };
        Assert.True(motion.IsReady);
        Assert.Equal(0.024, motion.Position.X);
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation.StartsWith("AxmMotSet"));

        AjinSdk.MotionAxes[9] = AjinSdk.MotionAxes[9] with { InMotion = 1 };
        var movingError = Assert.Throws<MotionInterlockException>(motion.Initialize);
        Assert.Contains("axis 9", movingError.Message);
        Assert.Contains("AxmStatusReadInMotion=1", movingError.Message);
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation.StartsWith("AxmMotSet"));

        var movingRead = new AjinSdk.Call(nameof(CAXM.AxmStatusReadInMotion), Axis: 9);
        AjinSdk.Results[movingRead] = (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;
        Assert.Throws<IOException>(motion.Initialize);
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation.StartsWith("AxmMotSet"));
        AjinSdk.Results.Remove(movingRead);
        AjinSdk.MotionAxes[9] = AjinSdk.MotionAxes[9] with { InMotion = 0 };
        motion.Initialize();
        Assert.True(motion.IsReady);
        var writes = AjinSdk.Calls.Count(call => call.Operation.StartsWith("AxmMotSet"));
        motion.Initialize();
        Assert.Equal(writes, AjinSdk.Calls.Count(call => call.Operation.StartsWith("AxmMotSet")));

        var read = new AjinSdk.Call(nameof(CAXM.AxmStatusGetActPos), Axis: 9);
        AjinSdk.Results[read] = (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;
        Assert.Throws<IOException>(() => motion.Position); // Never substitute zero.

        AjinSdk.Results.Clear();
        AjinSdk.Results[new(nameof(CAXM.AxmMotGetAccelUnit), Axis: 9)] =
            (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;
        Assert.True(motion.IsReady);
        Assert.Throws<IOException>(motion.Initialize);
        Assert.Equal(writes, AjinSdk.Calls.Count(call => call.Operation.StartsWith("AxmMotSet")));

        AjinSdk.Results[new(nameof(CAXM.AxmStatusReadMechanical), Axis: 9)] =
            (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;
        var error = Assert.Throws<IOException>(() => motion.IsReady);
        Assert.Contains("AxmStatusReadMechanical (axis=9)", error.Message);
    }

    [Fact]
    public void MotionInitializationConfiguresOnlyTheStoppedAxisThatNeedsChanges()
    {
        var log = new ApplicationLog();
        using var loggerFactory = log.CreateLoggerFactory();
        using var controller = new AjinController(new());
        AjinSdk.MotionAxes[9] = new(Unit: 0.1, InMotion: 1);
        AjinSdk.MotionAxes[10] = new(Unit: 2, AccelerationUnit: 1);
        var motion = new AjinMotionService(
            controller, new() { Number = 9, MoveUnit = 0.1 }, new() { Number = 10 }, null,
            new(), new(), new(), loggerFactory.CreateLogger<AjinMotionService>());

        motion.Initialize();

        Assert.True(motion.IsReady);
        Assert.True(motion.IsMoving);
        Assert.Equal(0.1, AjinSdk.MotionAxes[9].Unit);
        Assert.Equal(1U, AjinSdk.MotionAxes[9].InMotion);
        Assert.Equal(1, AjinSdk.MotionAxes[10].Unit);
        Assert.Equal(0U, AjinSdk.MotionAxes[10].AccelerationUnit);
        Assert.All(
            AjinSdk.Calls.Where(call => call.Operation.StartsWith("AxmMotSet")),
            call => Assert.Equal(10, call.Axis));
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation.StartsWith("AxmMove"));
        Assert.Contains(log.Entries, entry => entry.Message.Contains(
            "axis 10 initialization: SDK Unit=2, Pulse=1, AccelUnit=1; configured Unit=1, Pulse=1, AccelUnit=0. Apply=true"));
        Assert.Contains(log.Entries, entry => entry.Message.Contains("axis 10 unit settings applied and read back successfully"));
    }

    [Fact]
    public async Task TeachingMovesJogHomeAndFeedbackDoNotReadOrWriteUnitsAfterInitialization()
    {
        using var controller = new AjinController(new());
        foreach (var axis in new[] { 9, 10, 11 })
            AjinSdk.MotionAxes[axis] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1);
        var z = new AxisHardware { Number = 11, MoveUnit = 10, MovePulse = 100 };
        var motion = new AjinMotionService(
            controller, new() { Number = 9 }, new() { Number = 10 }, z,
            new(), new(), new());
        motion.Initialize();
        Assert.Equal(10, AjinSdk.MotionAxes[11].Unit);
        Assert.Equal(100, AjinSdk.MotionAxes[11].Pulse);
        Assert.Single(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmMotSetMoveUnitPerPulse));

        // Configuration edits and SDK scale changes are handled only by the next initialization.
        z.MoveUnit = 2;
        z.MovePulse = 200;
        AjinSdk.MotionAxes[11] = AjinSdk.MotionAxes[11] with { Unit = 1, Pulse = 1 };
        AjinSdk.Results[new(nameof(CAXM.AxmMotGetMoveUnitPerPulse), Axis: 11)] =
            (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;
        AjinSdk.Results[new(nameof(CAXM.AxmMotGetAccelUnit), Axis: 11)] =
            (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;
        AjinSdk.Results[new(nameof(CAXM.AxmMoveStartPos), Axis: 11)] = 0;
        AjinSdk.Results[new(nameof(CAXM.AxmMoveStartMultiPos))] = 0;
        AjinSdk.Results[new(nameof(CAXM.AxmMoveVel), Axis: 11)] = 0;
        AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: 11)] = 0;
        AjinSdk.Results[new(nameof(CAXM.AxmHomeSetVel), Axis: 11)] = 0;
        AjinSdk.Results[new(nameof(CAXM.AxmHomeSetStart), Axis: 11)] = 0;
        using var cancellation = new CancellationTokenSource();
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation is nameof(CAXM.AxmMoveStartPos) or nameof(CAXM.AxmMoveStartMultiPos))
            {
                var move = AjinSdk.Moves.Last();
                for (var index = 0; index < move.Axes.Length; index++)
                {
                    var axis = move.Axes[index];
                    AjinSdk.MotionAxes[axis] = AjinSdk.MotionAxes[axis] with { Position = move.Positions![index] };
                }
            }
            if (call.Operation == nameof(CAXM.AxmMoveVel))
                cancellation.Cancel();
        };
        AjinSdk.Calls.Clear();

        Assert.True(motion.IsReady);
        var status = new MotionStatus(motion);
        status.RefreshMonitorFeedback();
        status.RefreshControlFeedback();
        await motion.AdjustAxisAsync(MotionAxis.Z, 2.5, 3);
        Assert.Equal(2.5, motion.Position.Z);
        await motion.MoveToXYAsync(2, 3, 3);
        Assert.Equal((2.0, 3.0, 2.5), motion.Position);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            motion.JogAsync(MotionAxis.Z, 3, cancellation.Token));
        Assert.True(await motion.HomeAsync(MotionAxis.Z, 3));

        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation is
            nameof(CAXM.AxmMotGetMoveUnitPerPulse) or nameof(CAXM.AxmMotSetMoveUnitPerPulse)
            or nameof(CAXM.AxmMotGetAccelUnit) or nameof(CAXM.AxmMotSetAccelUnit));
        Assert.Equal(1, AjinSdk.MotionAxes[11].Unit);
        Assert.Equal(1, AjinSdk.MotionAxes[11].Pulse);
    }

    [Fact]
    public void InitializationRejectsUnitsThatWereNotRetainedByTheSdk()
    {
        using var controller = new AjinController(new());
        AjinSdk.MotionAxes[6] = new();
        var motion = new AjinMotionService(
            controller, new() { Number = 6, MoveUnit = 10, MovePulse = 100 }, null, null,
            new(), new(), new());
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == nameof(CAXM.AxmMotSetAccelUnit))
                AjinSdk.MotionAxes[6] = AjinSdk.MotionAxes[6] with { Unit = 1, Pulse = 1 };
        };

        var error = Assert.Throws<MotionInterlockException>(motion.Initialize);

        Assert.Contains("SDK Unit=1, Pulse=1, AccelUnit=0", error.Message);
        Assert.Contains("configured Unit=10, Pulse=100, AccelUnit=0", error.Message);
        Assert.Single(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmMotSetMoveUnitPerPulse));
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation.StartsWith("AxmMove"));
    }

    [Theory]
    [InlineData(4150U)]
    [InlineData((uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN)]
    public void StopReachesEveryAxisWithoutAnApplicationMoveAndKeepsActualFeedback(uint stopError)
    {
        using var controller = new AjinController(new());
        var motion = new AjinMotionService(
            controller, new() { Number = 9 }, new() { Number = 10 }, null,
            new(), new(), new());
        AjinSdk.MotionAxes[9] = new(Mechanical: 1U << 4, ServoOn: 1, InMotion: 1);
        AjinSdk.MotionAxes[10] = new(InMotion: 1);
        AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: 9)] = stopError;
        AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: 10)] = 0;

        var error = Assert.Throws<MotionException>(motion.Stop);

        Assert.Contains("axis 9", error.ToString());
        Assert.Contains($"0x{stopError:X8}", error.ToString());
        Assert.Contains("ServoOn = True", error.ToString());
        Assert.Contains("Alarm = True", error.ToString());
        Assert.Contains("InMotion = True", error.ToString());
        Assert.Equal(new[] { 9, 10 }, AjinSdk.Calls
            .Where(call => call.Operation == nameof(CAXM.AxmMoveSStop))
            .Select(call => call.Axis!.Value));
        Assert.True(motion.IsMoving);
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmHomeSetResult));
    }

    [Fact]
    public void StopDiagnosticFailurePreservesTheStopErrorAndRunsAfterAllAxesStop()
    {
        using var controller = new AjinController(new());
        var motion = new AjinMotionService(
            controller, new() { Number = 9 }, new() { Number = 10 }, null,
            new(), new(), new());
        AjinSdk.MotionAxes[9] = new();
        AjinSdk.MotionAxes[10] = new();
        AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: 9)] = 4150;
        AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: 10)] = 0;
        AjinSdk.Results[new(nameof(CAXM.AxmStatusReadMechanical), Axis: 9)] =
            (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;

        var error = Assert.Throws<MotionException>(motion.Stop);

        var details = error.ToString();
        Assert.Contains("0x00001036", details);
        Assert.Contains("AxmStatusReadMechanical (axis=9)", details);
        Assert.Contains("feedback after STOP is unknown", details);
        Assert.DoesNotContain("InMotion = False", details);
        Assert.Equal(new[] { 9, 10 }, AjinSdk.Calls.Take(2).Select(call => call.Axis!.Value));
        Assert.All(AjinSdk.Calls.Take(2), call => Assert.Equal(nameof(CAXM.AxmMoveSStop), call.Operation));
    }

    [Theory]
    [InlineData(MotionAxis.X, HomeDirection.Positive, HomeSignal.HomeSensor, 4U)]
    [InlineData(MotionAxis.Y, HomeDirection.Negative, HomeSignal.NegativeLimit, 1U)]
    [InlineData(MotionAxis.Z, HomeDirection.Positive, HomeSignal.PositiveLimit, 0U)]
    [InlineData(MotionAxis.X, HomeDirection.Negative, HomeSignal.ControllerSetting, 1U)]
    public async Task HomeAppliesConfiguredDirectionSignalAndSpeedsBeforeStarting(
        MotionAxis axis, HomeDirection direction, HomeSignal signal, uint expectedSignal)
    {
        using var controller = new AjinController(new());
        var settings = new MotionSettings
        {
            HorizontalHome = new()
            {
                SearchSpeed = 7, DetectionSpeed = 2.5, ApproachSpeed = 0.8, FineSpeed = 0.06,
                SearchAccelerationSeconds = 0.4, DetectionAccelerationSeconds = 0.25,
            },
            ZHome = new()
            {
                SearchSpeed = 4, DetectionSpeed = 1.7, ApproachSpeed = 0.4, FineSpeed = 0.03,
                SearchAccelerationSeconds = 0.2, DetectionAccelerationSeconds = 0.5,
            },
        };
        AxisHardware x = new() { Number = 9 };
        AxisHardware y = new() { Number = 10 };
        AxisHardware z = new() { Number = 11 };
        var selected = axis switch { MotionAxis.X => x, MotionAxis.Y => y, _ => z };
        selected.HomeDirection = direction;
        selected.HomeSignal = signal;
        var motion = new AjinMotionService(
            controller, x, y, z, settings, new(), new());
        // Edits after construction apply only to the next driver instance.
        selected.HomeDirection = direction == HomeDirection.Positive ? HomeDirection.Negative : HomeDirection.Positive;
        selected.HomeSignal = signal == HomeSignal.HomeSensor ? HomeSignal.PositiveLimit : HomeSignal.HomeSensor;
        var method = new AjinSdk.HomeMethod(1, 1, 2, 25, 123);
        foreach (var axisNumber in new[] { 9, 10, 11 })
        {
            AjinSdk.MotionAxes[axisNumber] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1);
            AjinSdk.HomeMethods[axisNumber] = method;
            AjinSdk.Results[new(nameof(CAXM.AxmHomeSetVel), Axis: axisNumber)] = 0;
            AjinSdk.Results[new(nameof(CAXM.AxmHomeSetStart), Axis: axisNumber)] = 0;
        }

        var home = settings.Home(axis);
        Assert.True(await motion.HomeAsync(axis, home.SearchSpeed));

        var number = axis switch { MotionAxis.X => 9, MotionAxis.Y => 10, _ => 11 };
        Assert.Equal(
            method with { Direction = (int)direction, Signal = expectedSignal },
            AjinSdk.HomeMethods[number]);
        Assert.Equal(
            axis == MotionAxis.Z
                ? new double[] { 4000, 1700, 400, 30, 20000, 3400 }
                : new double[] { 7000, 2500, 800, 60, 17500, 10000 },
            AjinSdk.HomeVelocities[number]);
        Assert.Contains(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmHomeSetResult)
            && call.Axis == number && call.Value == (uint)AXT_MOTION_HOME_RESULT.HOME_ERR_UNKNOWN);
        var calls = AjinSdk.Calls.Where(call => call.Axis == number).Select(call => call.Operation).ToArray();
        Assert.True(Array.IndexOf(calls, nameof(CAXM.AxmHomeSetResult))
            < Array.IndexOf(calls, nameof(CAXM.AxmHomeSetMethod)));
        Assert.True(Array.IndexOf(calls, nameof(CAXM.AxmHomeSetMethod))
            < Array.IndexOf(calls, nameof(CAXM.AxmHomeSetVel)));
        Assert.True(Array.IndexOf(calls, nameof(CAXM.AxmHomeSetVel))
            < Array.IndexOf(calls, nameof(CAXM.AxmHomeSetStart)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleAxisHomeNeverHomesOrPositionsZ(bool zHomed)
    {
        using var controller = new AjinController(new());
        var settings = new MotionSettings
        {
            ZSpeed = double.NaN,
            ZHome = new() { SearchSpeed = double.NaN },
        };
        var motion = new AjinMotionService(
            controller, new() { Number = 9 }, null, new() { Number = 11 },
            settings, new(), new());
        foreach (var axis in new[] { 9, 11 })
        {
            AjinSdk.MotionAxes[axis] = new(Mechanical: 1U << 5, HomeResult: 0xFF, ServoOn: 1);
            AjinSdk.HomeMethods[axis] = new(0, 4, 0, 1000, 0);
            AjinSdk.Results[new(nameof(CAXM.AxmHomeSetVel), Axis: axis)] = 0;
            AjinSdk.Results[new(nameof(CAXM.AxmHomeSetStart), Axis: axis)] = 0;
        }
        AjinSdk.MotionAxes[11] = AjinSdk.MotionAxes[11] with { HomeResult = zHomed ? 1U : 0xFFU };
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == nameof(CAXM.AxmHomeSetStart))
                AjinSdk.MotionAxes[call.Axis!.Value] = AjinSdk.MotionAxes[call.Axis.Value] with { HomeResult = 1 };
        };

        Assert.True(await motion.HomeAsync(MotionAxis.X, 15));
        Assert.Equal(15000, AjinSdk.HomeVelocities[9][0]);

        Assert.False(AjinSdk.HomeVelocities.ContainsKey(11));
        Assert.Equal(zHomed ? 1U : 0xFFU, AjinSdk.MotionAxes[11].HomeResult);
        Assert.Equal(new[] { 9 }, AjinSdk.Calls
            .Where(call => call.Operation == nameof(CAXM.AxmHomeSetStart))
            .Select(call => call.Axis!.Value));
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmMoveStartPos));
    }

    [Fact]
    public async Task FailedHomeReturnsFalseAndKeepsTheSdkResultAfterStopping()
    {
        using var controller = new AjinController(new());
        var operations = new OperationCancellation();
        var motion = new AjinMotionService(
            controller, new() { Number = 9 }, null, null, new(), new(), operations);
        AjinSdk.MotionAxes[9] = new(Mechanical: 1U << 5, HomeResult: 0xFF, ServoOn: 1);
        AjinSdk.HomeMethods[9] = new(0, 4, 0, 1000, 0);
        AjinSdk.Results[new(nameof(CAXM.AxmHomeSetVel), Axis: 9)] = 0;
        AjinSdk.Results[new(nameof(CAXM.AxmHomeSetStart), Axis: 9)] = 0;
        AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: 9)] = 0;
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == nameof(CAXM.AxmHomeSetStart))
                AjinSdk.MotionAxes[9] = AjinSdk.MotionAxes[9] with { HomeResult = 0x12 };
        };

        Assert.False(await motion.HomeAsync(MotionAxis.X, 15));
        Assert.Single(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmMoveSStop));
        Assert.Equal(0x12U, AjinSdk.MotionAxes[9].HomeResult);
        Assert.False(operations.HasActiveOperations);
    }



    [Fact]
    public async Task CancellationDuringSecondHomeSetupDoesNotStartThatAxis()
    {
        using var controller = new AjinController(new());
        var operations = new OperationCancellation();
        var motion = new AjinMotionService(
            controller, new() { Number = 9 }, new() { Number = 10 }, null,
            new(), new(), operations);
        foreach (var axis in new[] { 9, 10 })
        {
            AjinSdk.MotionAxes[axis] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1);
            AjinSdk.HomeMethods[axis] = new(0, 4, 0, 0, 0);
            AjinSdk.Results[new(nameof(CAXM.AxmHomeSetVel), Axis: axis)] = 0;
            AjinSdk.Results[new(nameof(CAXM.AxmHomeSetStart), Axis: axis)] = 0;
            AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: axis)] = 0;
        }
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == nameof(CAXM.AxmHomeSetVel) && call.Axis == 10)
                operations.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => motion.HomeHorizontalAsync(15));

        Assert.Equal(9, Assert.Single(AjinSdk.Calls,
            call => call.Operation == nameof(CAXM.AxmHomeSetStart)).Axis);
        Assert.Equal(new[] { 9, 10 }, AjinSdk.Calls
            .Where(call => call.Operation == nameof(CAXM.AxmMoveSStop)).Select(call => call.Axis!.Value));
        Assert.False(operations.HasActiveOperations);
        Assert.Equal(MotionCommand.None, motion.Command);
    }

    [Fact]
    public async Task JogCancellationDuringStartNotificationDoesNotStartTheAxis()
    {
        using var controller = new AjinController(new());
        var operations = new OperationCancellation();
        var motion = new AjinMotionService(
            controller, new() { Number = 9 }, null, null, new(), new(), operations);
        AjinSdk.MotionAxes[9] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1);
        AjinSdk.Results[new(nameof(CAXM.AxmMoveVel), Axis: 9)] = 0;
        AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: 9)] = 0;
        motion.MovingChanged += moving =>
        {
            if (moving)
                operations.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => motion.JogAsync(MotionAxis.X, 1));

        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmMoveVel));
        Assert.Single(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmMoveSStop));
        Assert.False(operations.HasActiveOperations);
        Assert.Equal(MotionCommand.None, motion.Command);
    }

    [Theory]
    [InlineData(nameof(CAXM.AxmHomeSetResult))]
    [InlineData(nameof(CAXM.AxmHomeSetMethod))]
    [InlineData(nameof(CAXM.AxmHomeSetVel))]
    [InlineData(nameof(CAXM.AxmHomeSetStart))]
    public async Task HomeStartupFailureReturnsFalseAndStops(string operation)
    {
        using var controller = new AjinController(new());
        var motion = new AjinMotionService(
            controller, new() { Number = 9 }, null, new() { Number = 11 },
            new(), new(), new());
        foreach (var number in new[] { 9, 11 })
        {
            AjinSdk.MotionAxes[number] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1);
            AjinSdk.HomeMethods[number] = new(0, 4, 0, 1000, 0);
        }
        AjinSdk.Results[new(nameof(CAXM.AxmHomeSetVel), Axis: 11)] = 0;
        AjinSdk.Results[new(nameof(CAXM.AxmHomeSetStart), Axis: 11)] = 0;
        AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: 11)] = 0;
        uint? value = operation switch
        {
            nameof(CAXM.AxmHomeSetResult) => (uint)AXT_MOTION_HOME_RESULT.HOME_ERR_UNKNOWN,
            nameof(CAXM.AxmHomeSetMethod) => 0U,
            _ => null,
        };
        AjinSdk.Results[new(operation, Axis: 11, Value: value)] = (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;

        Assert.False(await motion.HomeAsync(MotionAxis.Z, 1));

        Assert.Single(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmMoveSStop));
        if (operation != nameof(CAXM.AxmHomeSetStart))
            Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmHomeSetStart));
        Assert.Equal(MotionCommand.None, motion.Command);
    }

    [Theory]
    [InlineData(nameof(CAXM.AxmMoveStartPos))]
    [InlineData(nameof(CAXM.AxmHomeSetStart))]
    public async Task CancellationStopFailureBelongsToMotionWithoutRetry(string command)
    {
        using var controller = new AjinController(new());
        controller.Initialize();
        AjinSdk.HomeMethods[9] = new(0, 4, 0, 0, 0);
        AjinSdk.MotionAxes[9] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1);
        AjinSdk.MotionAxes[10] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1);
        var operations = new OperationCancellation();
        var motion = new AjinMotionService(
            controller,
            new() { Number = 9 },
            new() { Number = 10 },
            null,
            new(),
            new(),
            operations);
        AjinSdk.Results[new(command, Axis: 9)] = 0;
        var failedStop = new AjinSdk.Call(nameof(CAXM.AxmMoveSStop), Axis: 9);
        AjinSdk.Results[failedStop] = (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;
        AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: 10)] = 0;
        AjinSdk.Results[new(nameof(CAXM.AxmHomeSetVel), Axis: 9)] = 0;
        Exception? cancellationFailure = null;
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == command)
            {
                cancellationFailure = Record.Exception(operations.Cancel);
            }
        };

        var failure = await Record.ExceptionAsync(() =>
            command == nameof(CAXM.AxmMoveStartPos)
                ? motion.MoveAxisAsync(MotionAxis.X, 10, 1)
                : motion.HomeAsync(MotionAxis.X, 1));

        Assert.Null(cancellationFailure);
        Assert.IsType<MotionException>(failure);
        Assert.Contains(nameof(CAXM.AxmMoveSStop), failure.ToString());
        Assert.Equal(
            new[] { 9 },
            AjinSdk.Calls.Where(call => call.Operation == nameof(CAXM.AxmMoveSStop)).Select(call => call.Axis!.Value));
        Assert.Equal(MotionCommand.None, motion.Command);
        Assert.False(operations.HasActiveOperations);
    }

    [Theory]
    [InlineData(nameof(CAXM.AxmMoveStartPos))]
    [InlineData(nameof(CAXM.AxmHomeSetStart))]
    public async Task MotionFailureSurvivesStopAndFeedbackFailures(string command)
    {
        using var controller = new AjinController(new());
        controller.Initialize();
        AjinSdk.HomeMethods[9] = new(0, 4, 0, 0, 0);
        AjinSdk.MotionAxes[9] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1);
        AjinSdk.MotionAxes[10] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1);
        var operations = new OperationCancellation();
        var motion = new AjinMotionService(
            controller,
            new() { Number = 9 },
            new() { Number = 10 },
            null,
            new(),
            new(),
            operations);
        AjinSdk.Results[new(command, Axis: 9)] =
            command == nameof(CAXM.AxmHomeSetStart)
                ? 0
                : (uint)AXT_FUNC_RESULT.AXT_RT_MOTION_ERROR_IN_ALARM;
        AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: 9)] = (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;
        AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: 10)] = 0;
        AjinSdk.Results[new(nameof(CAXM.AxmHomeSetVel), Axis: 9)] = 0;
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == command)
            {
                AjinSdk.Results[new(nameof(CAXM.AxmHomeGetResult), Axis: 9)] =
                    (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;
                AjinSdk.Results[new(nameof(CAXM.AxmStatusReadMechanical), Axis: 9)] =
                    (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;
            }
        };

        var error = await Assert.ThrowsAsync<MotionException>(() =>
            command == nameof(CAXM.AxmMoveStartPos)
                ? motion.MoveAxisAsync(MotionAxis.X, 10, 1)
                : motion.HomeAsync(MotionAxis.X, 1));

        var details = error.ToString();
        Assert.Contains(command == nameof(CAXM.AxmHomeSetStart) ? nameof(CAXM.AxmHomeGetResult) : command, details);
        Assert.Contains(nameof(CAXM.AxmMoveSStop), details);
        Assert.Contains(nameof(CAXM.AxmStatusReadMechanical), details);
        Assert.Equal(
            new[] { 9 },
            AjinSdk.Calls.Where(call => call.Operation == nameof(CAXM.AxmMoveSStop)).Select(call => call.Axis!.Value));
        Assert.Equal(MotionCommand.None, motion.Command);
        Assert.False(operations.HasActiveOperations);
    }

    [Fact]
    public async Task HorizontalHomePreservesTheOtherAxisStopFailure()
    {
        using var controller = new AjinController(new());
        controller.Initialize();
        var operations = new OperationCancellation();
        var motion = new AjinMotionService(
            controller, new() { Number = 9 }, new() { Number = 10 }, null,
            new(), new(), operations);
        foreach (var axis in new[] { 9, 10 })
        {
            AjinSdk.MotionAxes[axis] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1);
            AjinSdk.HomeMethods[axis] = new(0, 4, 0, 0, 0);
            AjinSdk.Results[new(nameof(CAXM.AxmHomeSetStart), Axis: axis)] = 0;
            AjinSdk.Results[new(nameof(CAXM.AxmHomeSetVel), Axis: axis)] = 0;
            AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: axis)] = 0;
        }
        var failedRead = new AjinSdk.Call(nameof(CAXM.AxmHomeGetResult), Axis: 9);
        AjinSdk.Results[failedRead] = 0;
        AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: 10)] = (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation != nameof(CAXM.AxmHomeSetStart))
                return;
            var axis = call.Axis!.Value;
            AjinSdk.MotionAxes[axis] = AjinSdk.MotionAxes[axis] with { HomeResult = 2 };
            if (axis == 10)
                AjinSdk.Results[failedRead] = (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;
        };

        var failure = await Assert.ThrowsAsync<MotionException>(() =>
            motion.HomeHorizontalAsync(1).WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Contains(nameof(CAXM.AxmHomeGetResult), failure.ToString());
        Assert.Contains("AxmMoveSStop (axis 10)", failure.ToString());
        Assert.False(operations.HasActiveOperations);
    }

    [Fact]
    public void InitializationReopensAFailedSessionWithoutResettingHealthyHardware()
    {
        using var controller = new AjinController(new());
        controller.Initialize();
        controller.Initialize();
        Assert.Single(AjinSdk.Calls, call => call.Operation == "AxlOpen");
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == "AxmMotLoadParaAll");
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == "AxlClose");

        AjinSdk.Results[new("AxdiReadInportWord", Module: 0, Offset: 0)] =
            (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;
        Assert.Throws<IOException>(() => controller.ReadRtexInputs(new uint[controller.RtexInputWordCount]));
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == "AxlOpen")
                AjinSdk.Results.Clear();
        };

        controller.Initialize();
        controller.ReadRtexInputs(new uint[controller.RtexInputWordCount]);
        Assert.Equal(2, AjinSdk.Calls.Count(call => call.Operation == "AxlOpen"));
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == "AxmMotLoadParaAll");
        Assert.Single(AjinSdk.Calls, call => call.Operation == "AxlClose");
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation is "AxlOpenNoReset" or "AxdoWriteOutportBit");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task InvalidPositionFeedbackCannotBeUsedForTeachingOrCoordinatedMotion(double position)
    {
        using var controller = new AjinController(new());
        controller.Initialize();
        AjinSdk.MotionAxes[9] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1, Position: position, Unit: 0.1);
        AjinSdk.MotionAxes[10] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1, Position: 10000);
        var operations = new OperationCancellation();
        var motion = new AjinMotionService(
            controller, new() { Number = 9 }, new() { Number = 10 }, null,
            new(), new(), operations);
        var status = new MotionStatus(motion);

        status.RefreshMonitorFeedback();

        Assert.Null(status.Position.X);
        Assert.Equal(10, status.Position.Y);
        Assert.IsType<IOException>(status.MonitorAxes[MotionAxis.X].Sample.ReadError);
        Assert.False(status.IsFeedbackAvailable);
        Assert.Throws<IOException>(() => motion.Position);
        await Assert.ThrowsAsync<IOException>(() => motion.MoveToXYAsync(20, 30, 1));
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation.StartsWith("AxmMove", StringComparison.Ordinal));
        Assert.False(operations.HasActiveOperations);
    }

    [Fact]
    public async Task SingleAxisMoveUsesInPositionWithoutAdditionalPositionTolerance()
    {
        using var controller = new AjinController(new());
        controller.Initialize();
        var operations = new OperationCancellation();
        var motion = new AjinMotionService(
            controller, new() { Number = 3 }, null, null,
            new(), new(), operations);
        AjinSdk.MotionAxes[3] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1);
        AjinSdk.Results[new(nameof(CAXM.AxmMoveStartPos), Axis: 3)] = 0;
        AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: 3)] = 0;
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == nameof(CAXM.AxmMoveStartPos))
                AjinSdk.MotionAxes[3] = AjinSdk.MotionAxes[3] with { Position = 189064 };
        };

        await motion.MoveAxisAsync(MotionAxis.X, 189.162, 1);

        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmMoveSStop));
        var move = Assert.Single(AjinSdk.Moves);
        Assert.Equal(new[] { 3 }, move.Axes);
        Assert.Equal(new double[] { 189162 }, move.Positions);
        Assert.Equal(189.064, motion.Position.X);
        Assert.False(operations.HasActiveOperations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopDuringMoveFeedbackDoesNotReportCompletionOrAxisFault(bool axisFault)
    {
        using var controller = new AjinController(new());
        controller.Initialize();
        var operations = new OperationCancellation();
        var motion = new AjinMotionService(
            controller, new() { Number = 3 }, null, null, new(), new(), operations);
        AjinSdk.MotionAxes[3] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1);
        AjinSdk.Results[new(nameof(CAXM.AxmMoveStartPos), Axis: 3)] = 0;
        AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: 3)] = 0;
        using var stop = new CancellationTokenSource();
        var started = false;
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == nameof(CAXM.AxmMoveStartPos))
            {
                started = true;
                AjinSdk.MotionAxes[3] = AjinSdk.MotionAxes[3] with
                {
                    Mechanical = (1U << 5) | (axisFault ? 1U << 4 : 0),
                };
            }
            if (started && call.Operation == nameof(CAXM.AxmStatusReadMechanical))
                stop.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => motion.MoveAxisAsync(MotionAxis.X, 10, 1, stop.Token));

        Assert.Single(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmMoveSStop));
        Assert.False(operations.HasActiveOperations);
        Assert.Equal(MotionCommand.None, motion.Command);
    }

    [Fact]
    public async Task XyMoveUsesInPositionWithoutAdditionalPositionTolerance()
    {
        using var controller = new AjinController(new());
        controller.Initialize();
        var operations = new OperationCancellation();
        var motion = new AjinMotionService(
            controller, new() { Number = 9 }, new() { Number = 10 }, null,
            new() { AccelerationSeconds = 0.25, DecelerationSeconds = 0.75,
                ZAccelerationSeconds = 0.1, ZDecelerationSeconds = 0.2 }, new(), operations);
        foreach (var axis in new[] { 9, 10 })
        {
            AjinSdk.MotionAxes[axis] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1);
            AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: axis)] = 0;
        }
        AjinSdk.Results[new(nameof(CAXM.AxmMoveStartMultiPos))] = 0;
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == nameof(CAXM.AxmMoveStartMultiPos))
            {
                AjinSdk.MotionAxes[9] = AjinSdk.MotionAxes[9] with { Position = 10000 };
                AjinSdk.MotionAxes[10] = AjinSdk.MotionAxes[10] with { Position = 19902 };
            }
        };

        await motion.MoveToXYAsync(10, 20, 1);

        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmMoveSStop));
        Assert.Single(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmMoveStartMultiPos));
        var move = Assert.Single(AjinSdk.Moves);
        Assert.Equal(new[] { 9, 10 }, move.Axes);
        Assert.Equal(new double[] { 10000, 20000 }, move.Positions);
        Assert.Equal(1000d / 3, move.Velocities[0], 9);
        Assert.Equal(2000d / 3, move.Velocities[1], 9);
        Assert.Equal(move.Velocities.Select(value => value / 0.25), move.Accelerations);
        Assert.Equal(move.Velocities.Select(value => value / 0.75), move.Decelerations);
        Assert.Equal(19.902, motion.Position.Y);
        Assert.False(operations.HasActiveOperations);
    }

    [Fact]
    public void DiagnosticsReadUninitializedAxesAndIsolateStatusAndPositionFailuresWithoutWrites()
    {
        using var controller = new AjinController(new());
        controller.Initialize(); // The I/O connection is open, but this motion group is disabled.
        AjinSdk.MotionAxes[9] = new(Mechanical: (1U << 4) | (1U << 7), Position: 12340);
        AjinSdk.MotionAxes[10] = new(Mechanical: 1U << 5, HomeResult: 1, Position: -5670);
        var motion = new AjinMotionService(
            controller,
            new() { Number = 9 },
            new() { Number = 10 },
            null,
            new(),
            new(),
            new());
        var status = new MotionStatus(motion);
        AjinSdk.Calls.Clear();

        status.RefreshMonitorFeedback();

        Assert.True(motion.IsReady); // Live SDK communication is available without a local init flag.
        Assert.True(status.MonitorAxes[MotionAxis.X].Sample.State!.Value.Alarm);
        Assert.True(status.MonitorAxes[MotionAxis.X].Sample.State!.Value.HomeSensor);
        Assert.Equal(12.34, status.MonitorAxes[MotionAxis.X].Sample.Position);
        Assert.False(status.MonitorAxes[MotionAxis.Y].Sample.State!.Value.ServoOn);
        Assert.Equal(-5.67, status.MonitorAxes[MotionAxis.Y].Sample.Position);
        Assert.Equal(new MotionPosition(12.34, -5.67, null), status.Position);

        var stateRead = new AjinSdk.Call(nameof(CAXM.AxmStatusReadMechanical), Axis: 9);
        var positionRead = new AjinSdk.Call(nameof(CAXM.AxmStatusGetActPos), Axis: 10);
        var reportedErrors = 0;
        void ReportError(MotionAxis axis, Exception error)
        {
            reportedErrors++;
        }

        AjinSdk.Results[stateRead] = (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;
        AjinSdk.Results[positionRead] = (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;
        var notifications = 0;
        var exceptions = 0;
        foreach (var axis in status.MonitorAxes.Values)
            axis.PropertyChanged += (_, _) => notifications++;
        var pollingThread = Environment.CurrentManagedThreadId;
        void OnException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (Environment.CurrentManagedThreadId == pollingThread && args.Exception is IOException)
                exceptions++;
        }

        AppDomain.CurrentDomain.FirstChanceException += OnException;
        try
        {
            for (var scan = 0; scan < 20; scan++)
            {
                status.RefreshMonitorFeedback(ReportError);
                status.RefreshControlFeedback();
            }
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnException;
        }

        Assert.Equal(0, exceptions);
        Assert.True(notifications > 0); // Error samples can carry a new exception on each acquisition.
        Assert.All(status.Axes.Values, axis => Assert.Null(axis.State));
        Assert.Equal(2, reportedErrors); // Unchanged errors are reported only once.
        Assert.Null(status.MonitorAxes[MotionAxis.X].Sample.State);
        Assert.Equal(12.34, status.MonitorAxes[MotionAxis.X].Sample.Position);
        Assert.Contains("axis=9", status.MonitorAxes[MotionAxis.X].Sample.ReadError!.Message);
        Assert.NotNull(status.MonitorAxes[MotionAxis.Y].Sample.State);
        Assert.Null(status.MonitorAxes[MotionAxis.Y].Sample.Position);
        Assert.Equal(new MotionPosition(12.34, null, null), status.Position);

        Assert.Throws<IOException>(() => motion.GetAxisState(MotionAxis.X)); // Command reads still fail explicitly.

        AjinSdk.Results.Clear();
        AjinSdk.MotionAxes[10] = AjinSdk.MotionAxes[10] with { Position = -56.7, Unit = 1, Pulse = 100 };
        var notificationsBeforeRecovery = notifications;
        status.RefreshMonitorFeedback(ReportError);
        Assert.Equal(notificationsBeforeRecovery + 2, notifications); // Recovery publishes both axes.
        Assert.Equal(-0.0567, status.MonitorAxes[MotionAxis.Y].Sample.Position!.Value, 8);
        Assert.All(status.MonitorAxes.Values, axis => Assert.Null(axis.Sample.ReadError));
        AjinSdk.Results[stateRead] = (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;
        status.RefreshMonitorFeedback(ReportError);
        Assert.Equal(3, reportedErrors); // The same fault is logged again after recovery.
        Assert.All(
            AjinSdk.Calls,
            call =>
                Assert.Contains(
                    call.Operation,
                    new[]
                    {
                        nameof(CAXM.AxmStatusReadMechanical),
                        nameof(CAXM.AxmHomeGetResult),
                        nameof(CAXM.AxmSignalIsServoOn),
                        nameof(CAXM.AxmStatusGetActPos),
                        nameof(CAXM.AxmStatusReadInMotion)
                    }));
    }

    [Fact]
    public async Task MotionInitializationReadsAlarmsAndServoOffWithoutTurningServosOn()
    {
        using var controller = new AjinController(new());
        AjinSdk.MotionAxes[9] = new(Mechanical: (1U << 4) | (1U << 7), Position: 12340);
        AjinSdk.MotionAxes[10] = new(Mechanical: 1U << 5, HomeResult: 1, Position: -5670);
        var motion = new AjinMotionService(
            controller,
            new() { Number = 9 },
            new() { Number = 10 },
            null,
            new(),
            new(),
            new());
        var status = new MotionStatus(motion);

        motion.Initialize();
        motion.Initialize();
        status.RefreshMonitorFeedback();
        status.RefreshControlFeedback();

        Assert.True(motion.IsReady);
        Assert.Equal(new MotionPosition(12.34, -5.67, null), status.Position);
        Assert.Equal(AxisCondition.Alarm, status.Axes[MotionAxis.X].Condition);
        Assert.True(status.Axes[MotionAxis.X].State!.Value.HomeSensor);
        Assert.Equal(AxisCondition.ServoOff, status.Axes[MotionAxis.Y].Condition);
        Assert.DoesNotContain(
            AjinSdk.Calls,
            call => call.Operation is nameof(CAXM.AxmSignalServoOn) or nameof(
                CAXM.AxmSignalServoAlarmReset));

        var error = Assert.Throws<IOException>(() => motion.SetServo(MotionAxis.X, true));
        Assert.Contains("AXT_RT_MOTION_ERROR_IN_ALARM", error.Message);
        Assert.Contains("axis=9", error.Message);
        status.RefreshControlFeedback();
        Assert.True(motion.IsReady); // An operator command failure must not hide feedback.
        Assert.Equal(AxisCondition.Alarm, status.Axes[MotionAxis.X].Condition);
        Assert.Equal(new MotionPosition(12.34, -5.67, null), status.Position);

        var reset = new AjinSdk.Call(nameof(CAXM.AxmSignalServoAlarmReset), Value: 1, Axis: 9);
        AjinSdk.Results[reset] = (uint)AXT_FUNC_RESULT.AXT_RT_MOTION_ERROR_IN_ALARM;
        await Assert.ThrowsAsync<IOException>(() => motion.ResetAsync());
        status.RefreshControlFeedback();
        Assert.Equal(AxisCondition.Alarm, status.Axes[MotionAxis.X].Condition);
        AjinSdk.Results.Remove(reset);
        await motion.ResetAsync(); // Reset clears the drive alarm; the machine sequence owns Servo ON.
        status.RefreshMonitorFeedback();
        status.RefreshControlFeedback();
        Assert.False(status.Axes[MotionAxis.X].State!.Value.Alarm);
        Assert.False(status.Axes[MotionAxis.X].State?.ServoOn);
        Assert.False(status.Axes[MotionAxis.Y].State?.ServoOn);
    }

    [Fact]
    public void InitializationUsesZeroSuccessAndLogsTheActualFiveModulesWithoutWritingOutputs()
    {
        var log = new ApplicationLog();
        using var loggerFactory = log.CreateLoggerFactory();
        using var controller = new AjinController(new(), loggerFactory.CreateLogger<AjinController>());

        controller.Initialize();
        controller.Initialize();

        Assert.Equal(new AjinSdk.Call("AxlOpen", Offset: 7), AjinSdk.Calls[0]);
        Assert.Equal("AxdInfoIsDIOModule", AjinSdk.Calls[1].Operation);
        Assert.Single(AjinSdk.Calls, call => call.Operation == "AxlOpen");
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation is "AxlOpenNoReset" or "AxmMotLoadParaAll");
        Assert.Contains(
            log.Entries,
            entry =>
                entry.Message.Contains("AxlOpen(interrupt=7)")
                    && entry.Message.Contains("AXT_RT_SUCCESS (0x00000000)"));
        Assert.Contains(log.Entries, entry => entry.Message.Contains("no .mot file loaded"));
        Assert.Equal(
            new int?[] { 0, 1, 2, 3, 4 },
            AjinSdk.Calls.Where(call => call.Operation == "AxdInfoGetModule")
                .Select(call => call.Module));
        Assert.Contains(
            log.Entries,
            entry => entry.Message.Contains("input modules=[0,1,4], output modules=[2,3,4]"));
        Assert.Contains(
            log.Entries,
            entry =>
                entry.Message.Contains("module=4, board=0, position=4, type=AXT_SIO_RDB32RTEX (0x86), DI=16, DO=16"));
        Assert.DoesNotContain(
            AjinSdk.Calls,
            call => call.Operation.StartsWith("AxdoWrite", StringComparison.Ordinal));
    }

    [Fact]
    public void ScanUsesTwoWordsForEach32PointInputAndOneForTheMixedModule()
    {
        using var controller = new AjinController(new());
        controller.Initialize();
        AjinSdk.Inputs[0] = 0x80010001;
        AjinSdk.Inputs[1] = 0x1234ABCD;
        AjinSdk.Inputs[4] = 0x8008;
        AjinSdk.Calls.Clear();
        var values = new uint[] { uint.MaxValue, uint.MaxValue, uint.MaxValue };

        controller.ReadRtexInputs(values);

        Assert.Equal(new uint[] { 0x80010001, 0x1234ABCD, 0x8008 }, values);
        Assert.Equal(
            new[]
            {
                new AjinSdk.Call("AxdiReadInportWord", 0, 0),
                new AjinSdk.Call("AxdiReadInportWord", 0, 1),
                new AjinSdk.Call("AxdiReadInportWord", 1, 0),
                new AjinSdk.Call("AxdiReadInportWord", 1, 1),
                new AjinSdk.Call("AxdiReadInportWord", 4, 0)
            },
            AjinSdk.Calls);
    }

    [Fact]
    public void WordReadsMaskUnusedBitsAndReorderedModulesKeepTheirLogicalSlots()
    {
        using var controller = new AjinController(new() { RtexInputModules = [4, 0, 1] });
        controller.Initialize();
        AjinSdk.InputWords[(4, 0)] = 0xFFFF0008;
        AjinSdk.InputWords[(0, 0)] = 0xAAAA1234;
        AjinSdk.InputWords[(0, 1)] = 0xFFFF8000;
        var values = new uint[3];

        controller.ReadRtexInputs(values);

        Assert.Equal(new uint[] { 8, 0x80001234, 0 }, values);
    }

    [Theory]
    [InlineData(0, 0, 2, 0)]
    [InlineData(31, 0, 2, 31)]
    [InlineData(32, 1, 3, 0)]
    [InlineData(79, 4, 4, 15)]
    public void BitIoUsesSeparateModuleListsAndTheExisting32BitSlotAddresses(
        int channel,
        int inputModule,
        int outputModule,
        int offset)
    {
        using var controller = new AjinController(new());
        controller.Initialize();
        AjinSdk.Inputs[inputModule] = 1U << offset;
        AjinSdk.Calls.Clear();

        Assert.True(controller.ReadRtexInput(channel));
        controller.WriteRtexOutput(channel, true);
        Assert.True(controller.ReadRtexOutput(channel));
        controller.WriteRtexOutput(channel, false);
        Assert.False(controller.ReadRtexOutput(channel));

        Assert.Equal(new AjinSdk.Call("AxdiReadInportBit", inputModule, offset), AjinSdk.Calls[0]);
        Assert.All(
            AjinSdk.Calls.Skip(1),
            call =>
            {
                Assert.Equal(outputModule, call.Module);
                Assert.Equal(offset, call.Offset);
            });
    }

    [Fact]
    public void MixedModuleBitsAbove15FailBeforeCallingNativeIo()
    {
        const int channel = 80;
        using var controller = new AjinController(new());
        controller.Initialize();
        AjinSdk.Calls.Clear();

        var inputError = Assert.Throws<IOException>(() => controller.ReadRtexInput(channel));
        var outputError = Assert.Throws<IOException>(() => controller.ReadRtexOutput(channel));
        Assert.Throws<IOException>(() => controller.WriteRtexOutput(channel, true));

        Assert.Contains($"module=4, offset={channel - 64}", inputError.Message);
        Assert.Contains("only 16 DI points", inputError.Message);
        Assert.Contains("only 16 DO points", outputError.Message);
        Assert.Empty(AjinSdk.Calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(96)]
    public void ChannelsOutsideTheConfiguredSlotsAreRejectedWithoutNativeCalls(int channel)
    {
        using var controller = new AjinController(new());
        controller.Initialize();
        AjinSdk.Calls.Clear();
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.ReadRtexInput(channel));
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.ReadRtexOutput(channel));
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.WriteRtexOutput(channel, true));
        Assert.Empty(AjinSdk.Calls);
    }

    [Fact]
    public void BufferSizeMustMatchTheCapturedInputModuleCount()
    {
        using var controller = new AjinController(new());
        Assert.Throws<ArgumentNullException>(() => controller.ReadRtexInputs(null!));
        Assert.Throws<ArgumentException>(() => controller.ReadRtexInputs(new uint[2]));
        Assert.Throws<ArgumentException>(() => controller.ReadRtexInputs(new uint[4]));
        Assert.Empty(AjinSdk.Calls);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(4, 0)]
    public void FailedWordReadThrowsWithModuleAndWordOffsetInsteadOfReturningOff(int module, int offset)
    {
        using var controller = new AjinController(new());
        controller.Initialize();
        AjinSdk.Results[new("AxdiReadInportWord", module, offset)] = (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;
        var error = Assert.Throws<IOException>(() => controller.ReadRtexInputs(new uint[3]));
        Assert.Contains($"AxdiReadInportWord (module={module}, offset={offset})", error.Message);
        Assert.Contains("AXT_RT_NOT_OPEN (0x0000041D)", error.Message);
    }

    [Fact]
    public void ZeroIsSuccessButNonzeroIoStatusesRemainFailures()
    {
        using var controller = new AjinController(new());
        controller.Initialize();
        Assert.False(controller.ReadRtexInput(0));
        AjinSdk.Results[new("AxdiReadInportBit", 0, 0)] = 1;
        Assert.Throws<IOException>(() => controller.ReadRtexInput(0));
        AjinSdk.Results[new("AxdoWriteOutportBit", 2, 0, 1)] = (uint)AXT_FUNC_RESULT.AXT_RT_DIO_INVALID_OFFSET_NO;
        Assert.Throws<IOException>(() => controller.WriteRtexOutput(0, true));
        Assert.False(controller.ReadRtexOutput(0));
    }

    [Fact]
    public void NoDioModulesFailsInitializationAndClosesTheLibrary()
    {
        using var controller = new AjinController(new());
        AjinSdk.Presence = (uint)AXT_EXISTENCE.STATUS_NOTEXIST;
        var error = Assert.Throws<IOException>(controller.Initialize);
        Assert.Contains("DIO modules were not detected", error.Message);
        Assert.Equal("AxlClose", AjinSdk.Calls[^1].Operation);
        Assert.Throws<IOException>(() => controller.ReadRtexInput(0));
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == "AxdInfoGetModule");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void MissingConfiguredModulesFailBeforeScanning(int count)
    {
        using var controller = new AjinController(new());
        AjinSdk.ModuleCount = count;
        Assert.Throws<IOException>(controller.Initialize);
        Assert.Equal("AxlClose", AjinSdk.Calls[^1].Operation);
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Module == 4);
        Assert.Throws<IOException>(() => controller.ReadRtexInputs(new uint[3]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DirectionMismatchIsReportedBeforeAnyIo(bool input)
    {
        var settings = new AjinSettings();
        if (input)
            settings.RtexInputModules = [2];
        else
            settings.RtexOutputModules = [0];
        using var controller = new AjinController(settings);
        var error = Assert.Throws<IOException>(controller.Initialize);
        Assert.Contains(
            input ? "input module=2 has 0 DI points" : "output module=0 has 0 DO points",
            error.Message);
        Assert.DoesNotContain(
            AjinSdk.Calls,
            call =>
                call.Operation.StartsWith("AxdiRead", StringComparison.Ordinal)
                    || call.Operation.StartsWith("AxdoWrite", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsupportedPointCountsAreRejected()
    {
        const int count = 64;
        using var controller = new AjinController(new());
        AjinSdk.Modules[4] = AjinSdk.Modules[4] with { Inputs = count };
        var error = Assert.Throws<IOException>(controller.Initialize);
        Assert.Contains($"input module=4 has {count} DI points", error.Message);
        Assert.Equal("AxlClose", AjinSdk.Calls[^1].Operation);
    }

    [Theory]
    [InlineData("AxdInfoGetModule")]
    [InlineData("AxdInfoGetInputCount")]
    [InlineData("AxdInfoGetOutputCount")]
    public void ModuleQueryErrorsRetainTheNativeErrorAndModuleNumber(string operation)
    {
        using var controller = new AjinController(new());
        AjinSdk.Results[new(operation, 4)] = (uint)AXT_FUNC_RESULT.AXT_RT_DIO_INVALID_MODULE_NO;
        var error = Assert.Throws<IOException>(controller.Initialize);
        Assert.Contains($"{operation} (module=4)", error.Message);
        Assert.Contains("AXT_RT_DIO_INVALID_MODULE_NO", error.Message);
        Assert.Equal("AxlClose", AjinSdk.Calls[^1].Operation);
    }

    [Fact]
    public void ConfigurationIdentityIsCapturedAndReinitializationRefreshesHardwareCounts()
    {
        var settings = new AjinSettings();
        using var controller = new AjinController(settings);
        settings.RtexInputModules[0] = 2;
        settings.RtexOutputModules[0] = 0;
        settings.RtexInputModules = [];
        settings.InterruptNumber = 99;
        Assert.Equal(3, controller.RtexInputWordCount);
        controller.Initialize();
        Assert.Equal(new AjinSdk.Call("AxlOpen", Offset: 7), AjinSdk.Calls[0]);
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == "AxmMotLoadParaAll");
        AjinSdk.Calls.Clear();
        controller.ReadRtexInput(0);
        controller.WriteRtexOutput(0, false);
        Assert.Equal(0, AjinSdk.Calls[0].Module);
        Assert.Equal(2, AjinSdk.Calls[1].Module);
        controller.Dispose();

        AjinSdk.Modules[0] = AjinSdk.Modules[0] with { Inputs = 16 };
        controller.Initialize();
        Assert.Throws<IOException>(() => controller.ReadRtexInput(16));
        AjinSdk.Calls.Clear();
        controller.ReadRtexInputs(new uint[3]);
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Module == 0 && call.Offset == 1);
    }

    [Fact]
    public void UninitializedAndDisposedControllersDoNotCallNativeIo()
    {
        using var controller = new AjinController(new());
        Assert.Throws<IOException>(() => controller.ReadRtexInput(0));
        Assert.Throws<IOException>(() => controller.ReadRtexOutput(0));
        Assert.Throws<IOException>(() => controller.WriteRtexOutput(0, true));
        Assert.Throws<IOException>(() => controller.ReadRtexInputs(new uint[3]));
        controller.Dispose();
        Assert.Empty(AjinSdk.Calls);

        controller.Initialize();
        AjinSdk.Calls.Clear();
        controller.Dispose();
        controller.Dispose();
        Assert.Equal("AxlClose", Assert.Single(AjinSdk.Calls).Operation);
        Assert.Throws<IOException>(() => controller.ReadRtexInput(0));
        Assert.Single(AjinSdk.Calls);
    }

    [Fact]
    public void FailedOpenDoesNotCloseAnUnownedLibraryAndCanBeRetried()
    {
        var log = new ApplicationLog();
        using var loggerFactory = log.CreateLoggerFactory();
        using var controller = new AjinController(new(), loggerFactory.CreateLogger<AjinController>());
        AjinSdk.Results[new("AxlOpen", Offset: 7)] = (uint)AXT_FUNC_RESULT.AXT_RT_OPEN_ERROR;
        var error = Assert.Throws<IOException>(controller.Initialize);
        Assert.Contains("AxlOpen", error.Message);
        Assert.Contains("AXT_RT_OPEN_ERROR", error.Message);
        Assert.Equal("AxlOpen", Assert.Single(AjinSdk.Calls).Operation);
        Assert.Contains(log.Entries, entry => entry.Message.Contains("AXT_RT_OPEN_ERROR"));
        Assert.Throws<IOException>(() => controller.ReadRtexInput(0));
        Assert.Throws<IOException>(() => controller.WriteRtexOutput(0, true));
        Assert.Single(AjinSdk.Calls);
        AjinSdk.Results.Clear();
        controller.Initialize();
        Assert.False(controller.ReadRtexInput(0));
    }

    [Fact]
    public void ModuleQueryFailureClosesAndPreservesCleanupErrors()
    {
        var log = new ApplicationLog();
        using var loggerFactory = log.CreateLoggerFactory();
        using var controller = new AjinController(new(), loggerFactory.CreateLogger<AjinController>());
        AjinSdk.Results[new("AxdInfoIsDIOModule")] = (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == "AxlClose")
                throw new IOException("Test cleanup failure");
        };
        var error = Assert.Throws<IOException>(controller.Initialize);
        Assert.Contains("AxdInfoIsDIOModule", error.Message);
        Assert.Contains("Test cleanup failure", Assert.IsType<string>(error.Data["AjinCloseError"]));
        Assert.Contains(log.Entries, entry => entry.Level == "ERROR");
        Assert.DoesNotContain(
            AjinSdk.Calls,
            call => call.Operation is "AxdInfoGetModule" or "AxlOpenNoReset" or "AxmMotLoadParaAll");
    }

    [Fact]
    public void FailedModuleValidationCanBeRetriedAfterConfigurationIsCorrectedInHardware()
    {
        using var controller = new AjinController(new());
        AjinSdk.ModuleCount = 4;
        Assert.Throws<IOException>(controller.Initialize);
        AjinSdk.ModuleCount = 5;
        controller.Initialize();
        Assert.False(controller.ReadRtexInput(79));
        Assert.Equal(2, AjinSdk.Calls.Count(call => call.Operation == "AxlOpen"));
    }

    [Fact]
    public void NullOrNegativeModuleListsAreRejectedBeforeOpeningTheSdk()
    {
        Assert.Throws<ArgumentNullException>(
            () => new AjinController(new() { RtexInputModules = null! }));
        Assert.Throws<ArgumentException>(() => new AjinController(new() { RtexOutputModules = [-1] }));
        Assert.Empty(AjinSdk.Calls);
    }

    [Fact]
    public void ConcurrentControllerCallsAreSerialized()
    {
        using var controller = new AjinController(new());
        Parallel.For(0, 40, _ => controller.Initialize());
        Assert.Single(AjinSdk.Calls, call => call.Operation == "AxlOpen");
        AjinSdk.Calls.Clear();
        Parallel.For(
            0,
            100,
            index =>
            {
                controller.ReadRtexInputs(new uint[3]);
                controller.ReadRtexOutput(index % 16);
                controller.WriteRtexOutput(index % 16, false);
            });
        Assert.Equal(700, AjinSdk.Calls.Count);
    }

    [Fact]
    public void NegativeInterruptIsRejectedBeforeOpeningTheSdk()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AjinController(new() { InterruptNumber = -1 }));
        Assert.Empty(AjinSdk.Calls);
    }

    [Fact]
    public async Task AxisMoveAndJogUseConfiguredAccelerationWithAnyWaveUnits()
    {
        using var controller = new AjinController(new());
        var settings = new MotionSettings { AccelerationSeconds = 0.2, DecelerationSeconds = 0.75,
            ZAccelerationSeconds = 0.1, ZDecelerationSeconds = 0.25 };
        var motion = CreateHorizontalHome(controller, hasY: false, settings: settings);
        using var cancellation = new CancellationTokenSource();
        AjinSdk.Results[new(nameof(CAXM.AxmMoveStartPos), Axis: 9)] = 0;
        AjinSdk.Results[new(nameof(CAXM.AxmMoveVel), Axis: 9)] = 0;
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == nameof(CAXM.AxmMoveStartPos))
                AjinSdk.MotionAxes[9] = AjinSdk.MotionAxes[9] with { Position = 2500 };
            if (call.Operation == nameof(CAXM.AxmMoveVel))
                cancellation.Cancel();
        };

        await motion.MoveAxisAsync(MotionAxis.X, 2.5, 3);
        settings.AccelerationSeconds = 0.6;
        settings.DecelerationSeconds = 0.3;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            motion.JogAsync(MotionAxis.X, -3, cancellation.Token));

        Assert.Equal(2, AjinSdk.Moves.Count);
        var move = AjinSdk.Moves[0];
        Assert.Equal(new double[] { 2500 }, move.Positions);
        Assert.Equal(new double[] { 3000 }, move.Velocities);
        Assert.Equal(new double[] { 15000 }, move.Accelerations);
        Assert.Equal(new double[] { 4000 }, move.Decelerations);
        var jog = AjinSdk.Moves[1];
        Assert.Null(jog.Positions);
        Assert.Equal(new double[] { -3000 }, jog.Velocities);
        Assert.Equal(new double[] { 5000 }, jog.Accelerations);
        Assert.Equal(new double[] { 10000 }, jog.Decelerations);
        Assert.Single(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmMoveSStop));
        Assert.Equal(MotionCommand.None, motion.Command);
    }

    [Fact]
    public async Task ZMovesAdjustmentAndJogUseSeparateTimesOrExistingTimesWhenUnset()
    {
        using var controller = new AjinController(new());
        var settings = new MotionSettings { AccelerationSeconds = 0.2, DecelerationSeconds = 0.75 };
        var motion = new AjinMotionService(
            controller, new() { Number = 9 }, null, new() { Number = 11 }, settings, new(), new());
        AjinSdk.MotionAxes[9] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1);
        AjinSdk.MotionAxes[11] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1);
        AjinSdk.Results[new(nameof(CAXM.AxmMoveStartPos), Axis: 11)] = 0;
        AjinSdk.Results[new(nameof(CAXM.AxmMoveVel), Axis: 11)] = 0;
        AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: 11)] = 0;
        using var cancellation = new CancellationTokenSource();
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == nameof(CAXM.AxmMoveStartPos))
                AjinSdk.MotionAxes[11] = AjinSdk.MotionAxes[11] with { Position = AjinSdk.Moves.Last().Positions![0] };
            if (call.Operation == nameof(CAXM.AxmMoveVel))
                cancellation.Cancel();
        };

        await motion.MoveAxisAsync(MotionAxis.Z, 2.5, 3);
        Assert.Equal(new double[] { 15000 }, AjinSdk.Moves[0].Accelerations);
        Assert.Equal(new double[] { 4000 }, AjinSdk.Moves[0].Decelerations);

        settings.ZAccelerationSeconds = 0.1;
        settings.ZDecelerationSeconds = 0.25;
        await motion.AdjustAxisAsync(MotionAxis.Z, 1, 3);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            motion.JogAsync(MotionAxis.Z, -3, cancellation.Token));
        Assert.Equal(3, AjinSdk.Moves.Count);
        Assert.All(AjinSdk.Moves.Skip(1), move =>
        {
            Assert.Equal(new[] { 11 }, move.Axes);
            Assert.Equal(new double[] { 30000 }, move.Accelerations);
            Assert.Equal(new double[] { 12000 }, move.Decelerations);
        });
        Assert.Equal(new double[] { -3000 }, AjinSdk.Moves[2].Velocities);
        Assert.Equal(MotionCommand.None, motion.Command);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task InvalidZTimesNeverStartMoveOrJog(double value)
    {
        using var controller = new AjinController(new());
        var settings = new MotionSettings { ZAccelerationSeconds = value };
        var motion = new AjinMotionService(
            controller, new() { Number = 9 }, null, new() { Number = 11 }, settings, new(), new());
        AjinSdk.MotionAxes[9] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1);
        AjinSdk.MotionAxes[11] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => motion.MoveAxisAsync(MotionAxis.Z, 2, 1));
        settings.ZAccelerationSeconds = 0.2;
        settings.ZDecelerationSeconds = value;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => motion.JogAsync(MotionAxis.Z, -1));
        Assert.Empty(AjinSdk.Moves);
        Assert.Equal(MotionCommand.None, motion.Command);
    }

    [Fact]
    public async Task HorizontalHomeStartsBothAxesBeforeWaitingAndWaitsForBothResults()
    {
        using var controller = new AjinController(new());
        var motion = CreateHorizontalHome(controller);
        var bothStarted = false;
        var yReads = 0;
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == nameof(CAXM.AxmHomeSetStart))
            {
                var axis = call.Axis!.Value;
                AjinSdk.MotionAxes[axis] = AjinSdk.MotionAxes[axis] with { HomeResult = 2 };
                if (axis == 10)
                {
                    bothStarted = true;
                    AjinSdk.MotionAxes[9] = AjinSdk.MotionAxes[9] with { HomeResult = 1 };
                }
            }
            if (call.Operation == nameof(CAXM.AxmHomeGetResult)
                && AjinSdk.Calls.Any(item => item.Operation == nameof(CAXM.AxmHomeSetStart)))
            {
                Assert.True(bothStarted);
                if (call.Axis == 10 && ++yReads == 2)
                    AjinSdk.MotionAxes[10] = AjinSdk.MotionAxes[10] with { HomeResult = 1 };
            }
        };

        Assert.True(await motion.HomeHorizontalAsync(1).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(yReads >= 3);
        Assert.Equal(new[] { 9, 10 }, AjinSdk.Calls
            .Where(call => call.Operation == nameof(CAXM.AxmHomeSetStart)).Select(call => call.Axis!.Value));
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmMoveSStop));
        Assert.Equal(new[] { 9, 10 }, AjinSdk.Calls
            .Where(call => call.Operation == nameof(CAXM.AxmHomeSetResult)).Select(call => call.Axis!.Value));
    }

    [Fact]
    public async Task InvalidAccelerationNeverStartsMotionOrHome()
    {
        using var controller = new AjinController(new());
        var settings = new MotionSettings { AccelerationSeconds = 0 };
        var motion = CreateHorizontalHome(controller, settings: settings);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => motion.MoveAxisAsync(MotionAxis.X, 2, 1));
        settings.HorizontalHome.SearchAccelerationSeconds = 0;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => motion.HomeHorizontalAsync(1));

        Assert.Empty(AjinSdk.Moves);
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmHomeSetStart));
        Assert.Equal(MotionCommand.None, motion.Command);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public async Task HorizontalHomeFailureStopsBothAxesAndKeepsTheFailedResult(int failedAxis)
    {
        using var controller = new AjinController(new());
        var motion = CreateHorizontalHome(controller);
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == nameof(CAXM.AxmHomeSetStart))
            {
                var axis = call.Axis!.Value;
                AjinSdk.MotionAxes[axis] = AjinSdk.MotionAxes[axis] with
                {
                    HomeResult = axis == failedAxis ? 0x12U : 2U,
                };
            }
        };

        Assert.False(await motion.HomeHorizontalAsync(1));
        Assert.Equal(0x12U, AjinSdk.MotionAxes[failedAxis].HomeResult);
        Assert.Equal(2, AjinSdk.Calls.Count(call => call.Operation == nameof(CAXM.AxmHomeSetResult)));
        Assert.Equal(new[] { 9, 10 }, AjinSdk.Calls
            .Where(call => call.Operation == nameof(CAXM.AxmMoveSStop)).Select(call => call.Axis!.Value));
    }

    [Fact]
    public async Task SecondHomeStartFailureStopsTheFirstAxisBeforeReturning()
    {
        using var controller = new AjinController(new());
        var motion = CreateHorizontalHome(controller);
        AjinSdk.Results[new(nameof(CAXM.AxmHomeSetStart), Axis: 10)] =
            (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;

        Assert.False(await motion.HomeHorizontalAsync(1));
        Assert.Equal(2, AjinSdk.Calls.Count(call => call.Operation == nameof(CAXM.AxmHomeSetResult)));
        Assert.Equal(new[] { 9, 10 }, AjinSdk.Calls
            .Where(call => call.Operation == nameof(CAXM.AxmMoveSStop)).Select(call => call.Axis!.Value));
        Assert.Equal(MotionCommand.None, motion.Command);
    }

    [Fact]
    public async Task HorizontalHomeCancellationWaitsForBothAxesToStop()
    {
        using var controller = new AjinController(new());
        var motion = CreateHorizontalHome(controller);
        using var cancellation = new CancellationTokenSource();
        var cancelled = false;
        var stopChecks = 0;
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == nameof(CAXM.AxmHomeSetStart))
            {
                var axis = call.Axis!.Value;
                AjinSdk.MotionAxes[axis] = AjinSdk.MotionAxes[axis] with { HomeResult = 2, InMotion = 1 };
                if (axis == 10)
                {
                    cancelled = true;
                    cancellation.Cancel();
                }
            }
            if (cancelled && call.Operation == nameof(CAXM.AxmStatusReadInMotion) && ++stopChecks == 4)
            {
                foreach (var axis in new[] { 9, 10 })
                    AjinSdk.MotionAxes[axis] = AjinSdk.MotionAxes[axis] with { InMotion = 0 };
            }
            if (call.Operation == nameof(CAXM.AxmMoveSStop))
            {
                var axis = call.Axis!.Value;
                AjinSdk.MotionAxes[axis] = AjinSdk.MotionAxes[axis] with
                {
                    HomeResult = (uint)AXT_MOTION_HOME_RESULT.HOME_ERR_USER_BREAK,
                };
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            motion.HomeHorizontalAsync(1, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(stopChecks >= 6);
        Assert.Equal(2, AjinSdk.Calls.Count(call => call.Operation == nameof(CAXM.AxmHomeSetResult)));
        foreach (var axis in new[] { 9, 10 })
            Assert.Equal((uint)AXT_MOTION_HOME_RESULT.HOME_ERR_USER_BREAK, AjinSdk.MotionAxes[axis].HomeResult);
        Assert.Equal(new[] { 9, 10 }, AjinSdk.Calls
            .Where(call => call.Operation == nameof(CAXM.AxmMoveSStop))
            .Select(call => call.Axis!.Value).Distinct());
        Assert.Equal(MotionCommand.None, motion.Command);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HomeFeedbackCannotCompleteAfterStop(bool duringStopConfirmation)
    {
        using var controller = new AjinController(new());
        var motion = CreateHorizontalHome(controller);
        using var stop = new CancellationTokenSource();
        var started = false;
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == nameof(CAXM.AxmHomeSetStart))
            {
                var axis = call.Axis!.Value;
                AjinSdk.MotionAxes[axis] = AjinSdk.MotionAxes[axis] with { HomeResult = 1, InMotion = 0 };
                started |= axis == 10;
            }
            if (started && call.Axis == 10
                && call.Operation == (duringStopConfirmation
                    ? nameof(CAXM.AxmStatusReadInMotion) : nameof(CAXM.AxmHomeGetResult)))
                stop.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => motion.HomeHorizontalAsync(1, stop.Token));

        Assert.Equal(new[] { 9, 10 }, AjinSdk.Calls
            .Where(call => call.Operation == nameof(CAXM.AxmMoveSStop)).Select(call => call.Axis!.Value));
        Assert.Equal(MotionCommand.None, motion.Command);
    }

    [Fact]
    public async Task SingleAxisHorizontalHomeNeverCommandsAMissingYAxis()
    {
        using var controller = new AjinController(new());
        var motion = CreateHorizontalHome(controller, hasY: false);
        Assert.True(await motion.HomeHorizontalAsync(1));
        Assert.Single(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmHomeSetStart));
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Axis == 10);
    }

    private static AjinMotionService CreateHorizontalHome(
        AjinController controller,
        bool hasY = true,
        MotionSettings? settings = null)
    {
        foreach (var axis in hasY ? new[] { 9, 10 } : new[] { 9 })
        {
            AjinSdk.MotionAxes[axis] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1);
            AjinSdk.HomeMethods[axis] = new(0, 4, 0, 0, 0);
            AjinSdk.Results[new(nameof(CAXM.AxmHomeSetStart), Axis: axis)] = 0;
            AjinSdk.Results[new(nameof(CAXM.AxmHomeSetVel), Axis: axis)] = 0;
            AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: axis)] = 0;
        }
        return new AjinMotionService(
            controller, new() { Number = 9 }, hasY ? new() { Number = 10 } : null,
            null, settings ?? new(), new(), new());
    }

    [Fact]
    public void AxisMappingAndScaleStayFixedUntilDriverIsRecreated()
    {
        using var controller = new AjinController(new());
        var axis = new AxisHardware { Number = 9, MoveUnit = 10, MovePulse = 100 };
        var motion = new AjinMotionService(controller, axis, null, null, new(), new(), new());
        AjinSdk.MotionAxes[9] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1);
        motion.Initialize();
        var unit = AjinSdk.MotionAxes[9];
        axis.Number = 10;
        axis.MoveUnit = 2;
        axis.MovePulse = 200;
        AjinSdk.Calls.Clear();

        motion.Initialize();

        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmMotSetMoveUnitPerPulse));
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Axis == 10);
        Assert.Equal(unit, AjinSdk.MotionAxes[9]);

        AjinSdk.MotionAxes[10] = new();
        var restarted = new AjinMotionService(controller, axis, null, null, new(), new(), new());
        restarted.Initialize();
        Assert.Equal(2, AjinSdk.MotionAxes[10].Unit);
        Assert.Equal(200, AjinSdk.MotionAxes[10].Pulse);
    }

    [Fact]
    public void ActualPositionUsesSdkUnitsWithoutRescalingFromStoredAxisSettings()
    {
        using var controller = new AjinController(new());
        var axis = new AxisHardware { Number = 9, MoveUnit = 10, MovePulse = 100 };
        var motion = new AjinMotionService(controller, axis, null, null, new(), new(), new());
        AjinSdk.MotionAxes[9] = new(Position: 12340, Unit: 10, Pulse: 100);
        Assert.Equal(12.34, motion.Position.X);

        // Editing configuration must not change a coordinate already scaled by the SDK.
        axis.MoveUnit = 1;
        axis.MovePulse = 1000;
        Assert.Equal(12.34, motion.Position.X);
        Assert.Equal(12.34, motion.ReadDiagnosticPosition(MotionAxis.X).Position);
        AjinSdk.MotionAxes[9] = AjinSdk.MotionAxes[9] with { Position = -5670, Unit = 0.1, Pulse = 1 };
        Assert.Equal(-5.67, motion.Position.X);
        Assert.All(AjinSdk.Calls, call => Assert.Equal(nameof(CAXM.AxmStatusGetActPos), call.Operation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PositionMovesUseAbsoluteTargetsEvenAfterExternalModeChanges(bool xy)
    {
        using var controller = new AjinController(new());
        var motion = new AjinMotionService(
            controller, new() { Number = 9 }, new() { Number = 10 }, new() { Number = 11 },
            new(), new(), new());
        foreach (var axis in new[] { 9, 10, 11 })
        {
            AjinSdk.MotionAxes[axis] = new(
                Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1, Position: -10000, AbsRelMode: 1);
            AjinSdk.Results[new(nameof(CAXM.AxmMovePos), Axis: axis)] = 0;
            AjinSdk.Results[new(nameof(CAXM.AxmMoveStartPos), Axis: axis)] = 0;
            AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: axis)] = 0;
        }
        AjinSdk.Results[new(nameof(CAXM.AxmMoveMultiPos))] = 0;
        AjinSdk.Results[new(nameof(CAXM.AxmMoveStartMultiPos))] = 0;
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation is not (nameof(CAXM.AxmMovePos) or nameof(CAXM.AxmMoveMultiPos)
                or nameof(CAXM.AxmMoveStartPos) or nameof(CAXM.AxmMoveStartMultiPos)))
                return;
            var move = AjinSdk.Moves.Last();
            for (var index = 0; index < move.Axes.Length; index++)
            {
                var axis = move.Axes[index];
                var state = AjinSdk.MotionAxes[axis];
                AjinSdk.MotionAxes[axis] = state with
                {
                    Position = move.Positions![index]
                        + (call.Operation == nameof(CAXM.AxmMoveMultiPos) || state.AbsRelMode == 0 ? 0 : state.Position),
                };
            }
        };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            // An external motion utility can change this after initialization or any previous move.
            foreach (var axis in new[] { 9, 10, 11 })
                AjinSdk.MotionAxes[axis] = AjinSdk.MotionAxes[axis] with { AbsRelMode = 1 };
            if (xy)
                await motion.MoveToXYAsync(20 + attempt, 30 + attempt, 10);
            else
                await motion.MoveAxisAsync(MotionAxis.Z, -30 - attempt, 10);
        }

        Assert.Equal(xy ? (21.0, 31.0, -10.0) : (-10.0, -10.0, -31.0), motion.Position);
        foreach (var axis in xy ? new[] { 9, 10 } : new[] { 11 })
            Assert.Equal(0U, AjinSdk.MotionAxes[axis].AbsRelMode);
    }

    [Theory]
    [InlineData(nameof(CAXM.AxmMotSetAbsRelMode))]
    [InlineData(nameof(CAXM.AxmMotGetAbsRelMode))]
    public async Task FailedAbsoluteModeSetupPreventsPositionCommand(string failedOperation)
    {
        using var controller = new AjinController(new());
        var motion = new AjinMotionService(
            controller, new() { Number = 9 }, new() { Number = 10 }, null,
            new(), new(), new());
        foreach (var axis in new[] { 9, 10 })
        {
            AjinSdk.MotionAxes[axis] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1, AbsRelMode: 1);
            AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: axis)] = 0;
        }
        AjinSdk.Results[new(failedOperation, Axis: 10,
            Value: failedOperation == nameof(CAXM.AxmMotSetAbsRelMode) ? 0U : null)] =
            (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;

        var failure = await Assert.ThrowsAsync<IOException>(() => motion.MoveToXYAsync(20, 30, 10));

        Assert.Contains(failedOperation, failure.Message);
        Assert.Empty(AjinSdk.Moves);
        Assert.Equal((0.0, 0.0, 0.0), motion.Position);
        Assert.Equal(MotionCommand.None, motion.Command);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PositionFeedbackFailuresStopAndReportMotionErrors(bool axisFault)
    {
        using var controller = new AjinController(new());
        var motion = new AjinMotionService(
            controller, new() { Number = 9 }, null, null,
            new(), new() { TimeoutMilliseconds = 20 }, new());
        AjinSdk.MotionAxes[9] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1);
        AjinSdk.Results[new(nameof(CAXM.AxmMoveStartPos), Axis: 9)] = 0;
        AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: 9)] = 0;
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == nameof(CAXM.AxmMoveStartPos))
                AjinSdk.MotionAxes[9] = AjinSdk.MotionAxes[9] with { Mechanical = axisFault ? 1U << 4 : 0 };
        };

        var error = await Record.ExceptionAsync(() => motion.MoveAxisAsync(MotionAxis.X, 10, 10));

        if (axisFault)
            Assert.IsType<MotionInterlockException>(error);
        else
            Assert.IsType<TimeoutException>(Assert.IsType<MotionException>(error).InnerException);
        Assert.Single(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmMoveSStop));
        Assert.Equal(MotionCommand.None, motion.Command);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PositionCancellationStopsBeforeNativeMoveCompletion(bool xy)
    {
        using var controller = new AjinController(new());
        var operations = new OperationCancellation();
        var motion = new AjinMotionService(
            controller, new() { Number = 9 }, new() { Number = 10 }, new() { Number = 11 },
            new(), new(), operations);
        var axes = xy ? new[] { 9, 10 } : new[] { 11 };
        foreach (var axis in new[] { 9, 10, 11 })
        {
            AjinSdk.MotionAxes[axis] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1);
            AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: axis)] = 0;
        }
        var blockingCommand = xy ? nameof(CAXM.AxmMoveMultiPos) : nameof(CAXM.AxmMovePos);
        var startCommand = xy ? nameof(CAXM.AxmMoveStartMultiPos) : nameof(CAXM.AxmMoveStartPos);
        AjinSdk.Results[new(blockingCommand, Axis: xy ? null : 11)] = 0;
        AjinSdk.Results[new(startCommand, Axis: xy ? null : 11)] = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseBlockingCommand = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == blockingCommand || call.Operation == startCommand)
            {
                foreach (var axis in axes)
                    AjinSdk.MotionAxes[axis] = AjinSdk.MotionAxes[axis] with { InMotion = 1, Position = -1000 };
                started.TrySetResult();
                // The legacy SDK call does not return until motion ends; the start API returns immediately.
                if (call.Operation == blockingCommand)
                    releaseBlockingCommand.Wait(TimeSpan.FromSeconds(5));
            }
            if (call.Operation == nameof(CAXM.AxmMoveSStop))
            {
                var axis = call.Axis!.Value;
                AjinSdk.MotionAxes[axis] = AjinSdk.MotionAxes[axis] with { InMotion = 0 };
                if (axes.All(number => AjinSdk.MotionAxes[number].InMotion == 0))
                    stopped.TrySetResult();
            }
        };

        var moving = xy
            ? motion.MoveToXYAsync(-20, -30, 10, cancellation.Token)
            : motion.MoveAxisAsync(MotionAxis.Z, -30, 10, cancellation.Token);
        var stoppedBeforeCompletion = false;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await Task.WhenAny(stopped.Task, Task.Delay(TimeSpan.FromSeconds(1)));
            stoppedBeforeCompletion = stopped.Task.IsCompletedSuccessfully;
        }
        finally
        {
            releaseBlockingCommand.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => moving.WaitAsync(TimeSpan.FromSeconds(2)));
        }

        Assert.True(stoppedBeforeCompletion, "Cancellation must stop the axes before the native move returns.");
        Assert.All(axes, axis => Assert.Equal(-1000, AjinSdk.MotionAxes[axis].Position));
        Assert.Equal(axes, AjinSdk.Calls.Where(call => call.Operation == nameof(CAXM.AxmMoveSStop))
            .Select(call => call.Axis!.Value));
        Assert.False(operations.HasActiveOperations);
        Assert.Equal(MotionCommand.None, motion.Command);
    }

    [Fact]
    public async Task AlarmResetReleasesOutputsWithoutServoEnableAndPreservesPositionAndHomeFeedback()
    {
        using var controller = new AjinController(new());
        AjinSdk.MotionAxes[9] = new(Mechanical: 1U << 4, HomeResult: 1, Position: 12340);
        AjinSdk.MotionAxes[10] = new(Mechanical: 1U << 4, HomeResult: 0, Position: -5670);
        var motion = new AjinMotionService(controller, new() { Number = 9 }, new() { Number = 10 },
            null, new(), new(), new());


        await motion.ResetAsync();
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmSignalServoOn));

        foreach (var axis in new[] { 9, 10 })
        {
            Assert.Contains(new AjinSdk.Call(nameof(CAXM.AxmSignalServoAlarmReset), Value: 1, Axis: axis), AjinSdk.Calls);
            Assert.Contains(new AjinSdk.Call(nameof(CAXM.AxmSignalServoAlarmReset), Value: 0, Axis: axis), AjinSdk.Calls);
            Assert.Equal(0U, AjinSdk.MotionAxes[axis].AlarmReset);
        }
        Assert.Equal((12.34, -5.67, 0), motion.Position);
        Assert.True(motion.GetAxisState(MotionAxis.X).Homed);
        Assert.False(motion.GetAxisState(MotionAxis.Y).Homed);
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmHomeSetResult));
    }

    [Fact]
    public async Task CancelledAlarmResetReleasesOutputsWithoutTurningServosOn()
    {
        using var controller = new AjinController(new());
        AjinSdk.MotionAxes[9] = new();
        AjinSdk.MotionAxes[10] = new();
        var motion = new AjinMotionService(controller, new() { Number = 9 }, new() { Number = 10 },
            null, new(), new(), new());
        using var stop = new CancellationTokenSource();
        AjinSdk.BeforeCall = call =>
        {
            if (call is { Operation: nameof(CAXM.AxmSignalServoAlarmReset), Axis: 10, Value: 1 })
                stop.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => motion.ResetAsync(stop.Token));

        Assert.All(AjinSdk.MotionAxes.Values, axis => Assert.Equal(0U, axis.AlarmReset));
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmSignalServoOn));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlarmResetReportsFailuresAndAttemptsToReleaseEveryAxis(bool failOnCommand)
    {
        using var controller = new AjinController(new());
        AjinSdk.MotionAxes[9] = new();
        AjinSdk.MotionAxes[10] = new();
        var motion = new AjinMotionService(controller, new() { Number = 9 }, new() { Number = 10 },
            null, new(), new(), new());
        if (failOnCommand)
            AjinSdk.Results[new(nameof(CAXM.AxmSignalServoAlarmReset), Value: 1, Axis: 9)] = 1;
        AjinSdk.Results[new(nameof(CAXM.AxmSignalServoAlarmReset), Value: 0, Axis: 9)] = 1;

        var error = await Assert.ThrowsAsync<AggregateException>(() => motion.ResetAsync());

        Assert.Equal(failOnCommand ? 2 : 1, error.InnerExceptions.Count);
        Assert.Contains("axis=9, on=0", error.ToString());
        if (failOnCommand)
            Assert.Contains("axis=9, on=1", error.ToString());
        Assert.Contains(new AjinSdk.Call(nameof(CAXM.AxmSignalServoAlarmReset), Value: 0, Axis: 10), AjinSdk.Calls);
        Assert.Equal(0U, AjinSdk.MotionAxes[10].AlarmReset);
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmSignalServoOn));
    }
}
