using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IBTM.Ajin;
using IBTM.Core;
using IBTM.Device;
using Xunit;

namespace IBTM.Ajin.Tests;

public sealed class AjinControllerTests
{
    [Fact]
    public void MotionUsesLiveMovementPositionAndUnitsWithoutLocalInitializationState()
    {
        using var controller = new AjinController(new());
        controller.Initialize();
        AjinSdk.MotionAxes[9] = new(
            Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1, Position: 120, InMotion: 1);
        var motion = new AjinMotionService(
            controller, new() { Number = 9 }, null, null, 0.01, new(), new(), new(), null);
        var feedback = new MotionStatus(motion);

        Assert.True(motion.IsReady);
        Assert.True(motion.IsMoving);
        Assert.True(motion.IsMovingHorizontal);
        Assert.Equal(MotionCommand.None, motion.Command); // External motion, not an application command.
        feedback.RefreshMonitorFeedback();
        feedback.RefreshControlFeedback();
        Assert.True(feedback.IsMoving);
        Assert.Equal(AxisCondition.Moving, feedback.Axes[MotionAxis.X].Condition);
        Assert.Equal(1.2, motion.GetPosition().X);

        AjinSdk.MotionAxes[9] = AjinSdk.MotionAxes[9] with
        {
            InMotion = 0, Mechanical = 0, Position = 240
        };
        feedback.RefreshMonitorFeedback();
        feedback.RefreshControlFeedback();
        Assert.False(motion.IsMoving);
        Assert.False(feedback.IsMoving);
        Assert.Equal(AxisCondition.NotInPosition, feedback.Axes[MotionAxis.X].Condition);
        Assert.Equal(2.4, motion.GetPosition().X);

        AjinSdk.MotionAxes[9] = AjinSdk.MotionAxes[9] with { Position = 2.4, Pulse = 100 };
        Assert.False(motion.IsReady);
        Assert.Equal(2.4, motion.GetPosition().X);
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation.StartsWith("AxmMotSet"));

        motion.Initialize();
        Assert.True(motion.IsReady);
        var writes = AjinSdk.Calls.Count(call => call.Operation.StartsWith("AxmMotSet"));
        motion.Initialize();
        Assert.Equal(writes, AjinSdk.Calls.Count(call => call.Operation.StartsWith("AxmMotSet")));

        var read = new AjinSdk.Call(nameof(CAXM.AxmStatusGetActPos), Axis: 9);
        AjinSdk.Results[read] = (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;
        Assert.Throws<IOException>(() => motion.GetPosition()); // Never substitute zero.

        AjinSdk.Results.Clear();
        AjinSdk.Results[new(nameof(CAXM.AxmMotGetAccelUnit), Axis: 9)] =
            (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;
        var error = Assert.Throws<IOException>(() => motion.IsReady);
        Assert.Contains("AxmMotGetAccelUnit (axis=9)", error.Message);
        Assert.Throws<IOException>(motion.Initialize);
        Assert.Equal(writes, AjinSdk.Calls.Count(call => call.Operation.StartsWith("AxmMotSet")));
    }

    public AjinControllerTests()
    {
        AjinSdk.Reset();
    }

    [Theory]
    [InlineData(nameof(CAXM.AxmMovePos))]
    [InlineData(nameof(CAXM.AxmHomeSetStart))]
    public async Task MotionFailureSurvivesStopFeedbackAndFinalPositionFailures(string command)
    {
        using var controller = new AjinController(new());
        controller.Initialize();
        AjinSdk.MotionAxes[9] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1);
        var operations = new OperationCancellation();
        var motion = new AjinMotionService(
            controller,
            new() { Number = 9 },
            null,
            null,
            0.01,
            new(),
            new(),
            operations,
            null);
        AjinSdk.Results[new(command, Axis: 9)] =
            (uint)AXT_FUNC_RESULT.AXT_RT_MOTION_ERROR_IN_ALARM;
        AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: 9)] = 0;
        AjinSdk.Results[new(nameof(CAXM.AxmHomeSetResult), Axis: 9, Value: 0xFF)] = 0;
        AjinSdk.Results[new(nameof(CAXM.AxmHomeSetVel), Axis: 9)] = 0;
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == command)
            {
                AjinSdk.Results[new(nameof(CAXM.AxmStatusReadMechanical), Axis: 9)] =
                    (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;
                AjinSdk.Results[new(nameof(CAXM.AxmStatusGetActPos), Axis: 9)] =
                    (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;
            }
        };

        var error = await Assert.ThrowsAsync<MotionException>(() =>
            command == nameof(CAXM.AxmMovePos)
                ? motion.MoveAxisAsync(MotionAxis.X, 10, 1)
                : motion.HomeAsync(MotionAxis.X, 1));

        var details = error.ToString();
        Assert.Contains(command, details);
        Assert.Contains(nameof(CAXM.AxmStatusReadMechanical), details);
        Assert.Contains(nameof(CAXM.AxmStatusGetActPos), details);
        Assert.Single(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmMoveSStop));
        Assert.Equal(MotionCommand.None, motion.Command);
        Assert.False(operations.HasActiveOperations);
    }

    [Fact]
    public void InitializationReopensAFailedSessionWithoutResettingHealthyHardware()
    {
        using var controller = new AjinController(new());
        controller.Initialize();
        controller.Initialize();
        Assert.Single(AjinSdk.Calls, call => call.Operation == "AxlOpenNoReset");
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == "AxlClose");

        AjinSdk.Results[new("AxdiReadInportWord", Module: 0, Offset: 0)] =
            (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;
        Assert.Throws<IOException>(() => controller.ReadRtexInputs(new uint[controller.RtexInputWordCount]));
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == "AxlOpenNoReset")
                AjinSdk.Results.Clear();
        };

        controller.Initialize();
        controller.ReadRtexInputs(new uint[controller.RtexInputWordCount]);
        Assert.Equal(2, AjinSdk.Calls.Count(call => call.Operation == "AxlOpenNoReset"));
        Assert.Single(AjinSdk.Calls, call => call.Operation == "AxlClose");
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation is "AxlOpen" or "AxdoWriteOutportBit");
    }

    [Fact]
    public void DiagnosticsReadUninitializedAxesAndIsolateStatusAndPositionFailuresWithoutWrites()
    {
        using var controller = new AjinController(new());
        controller.Initialize(); // The I/O connection is open, but this motion group is disabled.
        AjinSdk.MotionAxes[9] = new(Mechanical: (1U << 4) | (1U << 7), Position: 1234);
        AjinSdk.MotionAxes[10] = new(Mechanical: 1U << 5, HomeResult: 1, Position: -567);
        var motion = new AjinMotionService(
            controller,
            new() { Number = 9 },
            new() { Number = 10 },
            null,
            0.01,
            new(),
            new(),
            new(),
            null);
        var status = new MotionStatus(motion);
        AjinSdk.Calls.Clear();

        status.RefreshMonitorFeedback();

        Assert.True(motion.IsReady); // The existing hardware parameters already match; no local init flag is needed.
        Assert.True(status.MonitorAxes[MotionAxis.X].Snapshot.State!.Value.Alarm);
        Assert.True(status.MonitorAxes[MotionAxis.X].Snapshot.State!.Value.HomeSensor);
        Assert.Equal(12.34, status.MonitorAxes[MotionAxis.X].Snapshot.Position);
        Assert.False(status.MonitorAxes[MotionAxis.Y].Snapshot.State!.Value.ServoOn);
        Assert.Equal(-5.67, status.MonitorAxes[MotionAxis.Y].Snapshot.Position);
        Assert.Equal(new MotionPosition(12.34, -5.67, null), status.Position);

        var stateRead = new AjinSdk.Call(nameof(CAXM.AxmStatusReadMechanical), Axis: 9);
        var positionRead = new AjinSdk.Call(nameof(CAXM.AxmStatusGetActPos), Axis: 10);
        var reportedErrors = 0;
        void ReportError(MotionAxis _, Exception __)
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
        Assert.Equal(2, notifications); // A repeated SDK failure is not a new UI state.
        Assert.All(status.Axes.Values, axis => Assert.Null(axis.State));
        Assert.Equal(2, reportedErrors); // Unchanged errors are reported only once.
        Assert.Null(status.MonitorAxes[MotionAxis.X].Snapshot.State);
        Assert.Equal(12.34, status.MonitorAxes[MotionAxis.X].Snapshot.Position);
        Assert.Contains("axis=9", status.MonitorAxes[MotionAxis.X].Snapshot.ReadError!.Message);
        Assert.NotNull(status.MonitorAxes[MotionAxis.Y].Snapshot.State);
        Assert.Null(status.MonitorAxes[MotionAxis.Y].Snapshot.Position);
        Assert.Equal(new MotionPosition(12.34, null, null), status.Position);

        Assert.Throws<IOException>(() => motion.GetAxisState(MotionAxis.X)); // Command reads still fail explicitly.

        AjinSdk.Results.Clear();
        AjinSdk.MotionAxes[10] = AjinSdk.MotionAxes[10] with { Position = -5.67, Unit = 1, Pulse = 100 };
        status.RefreshMonitorFeedback(ReportError);
        Assert.Equal(4, notifications); // Recovery publishes fresh state for both axes.
        Assert.Equal(-5.67, status.MonitorAxes[MotionAxis.Y].Snapshot.Position!.Value, 8);
        Assert.All(status.MonitorAxes.Values, axis => Assert.Null(axis.Snapshot.ReadError));
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
                        nameof(CAXM.AxmMotGetMoveUnitPerPulse),
                        nameof(CAXM.AxmMotGetAccelUnit),
                        nameof(CAXM.AxmStatusReadInMotion)
                    }));
    }

    [Fact]
    public void MotionInitializationReadsAlarmsAndServoOffWithoutTurningServosOn()
    {
        using var controller = new AjinController(new());
        AjinSdk.MotionAxes[9] = new(Mechanical: (1U << 4) | (1U << 7), Position: 1234);
        AjinSdk.MotionAxes[10] = new(Mechanical: 1U << 5, HomeResult: 1, Position: -567);
        var motion = new AjinMotionService(
            controller,
            new() { Number = 9 },
            new() { Number = 10 },
            null,
            0.01,
            new(),
            new(),
            new(),
            null);
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
        Assert.Throws<IOException>(motion.Reset);
        status.RefreshControlFeedback();
        Assert.Equal(AxisCondition.Alarm, status.Axes[MotionAxis.X].Condition);
        AjinSdk.Results.Remove(reset);
        motion.Reset(); // Explicit RESET can now reach alarm reset before requesting Servo ON.
        status.RefreshMonitorFeedback();
        status.RefreshControlFeedback();
        Assert.False(status.Axes[MotionAxis.X].State!.Value.Alarm);
        Assert.True(status.Axes[MotionAxis.X].ServoOn);
        Assert.True(status.Axes[MotionAxis.Y].ServoOn);
    }

    [Fact]
    public void InitializationUsesZeroSuccessAndLogsTheActualFiveModulesWithoutWritingOutputs()
    {
        using var log = new ApplicationLog();
        using var controller = new AjinController(new(), log);

        controller.Initialize();
        controller.Initialize();

        Assert.Equal(new AjinSdk.Call("AxlOpenNoReset", Offset: 7), AjinSdk.Calls[0]);
        Assert.Equal("AxdInfoIsDIOModule", AjinSdk.Calls[1].Operation);
        Assert.Single(AjinSdk.Calls, call => call.Operation == "AxlOpenNoReset");
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation is "AxlOpen" or "AxmMotLoadParaAll");
        Assert.Contains(
            log.Entries,
            entry =>
                entry.Message.Contains("AxlOpenNoReset(interrupt=7)")
                    && entry.Message.Contains("AXT_RT_SUCCESS (0x00000000)"));
        Assert.Contains(log.Entries, entry => entry.Message.Contains(".mot loading is skipped"));
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
    [InlineData(63, 1, 3, 31)]
    [InlineData(64, 4, 4, 0)]
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

    [Theory]
    [InlineData(80)]
    [InlineData(95)]
    public void MixedModuleBitsAbove15FailBeforeCallingNativeIo(int channel)
    {
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
    [InlineData(-1)]
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

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(64)]
    public void UnsupportedPointCountsAreRejected(int count)
    {
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
        Assert.Equal(new AjinSdk.Call("AxlOpenNoReset", Offset: 7), AjinSdk.Calls[0]);
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
        using var log = new ApplicationLog();
        using var controller = new AjinController(new(), log);
        AjinSdk.Results[new("AxlOpenNoReset", Offset: 7)] = (uint)AXT_FUNC_RESULT.AXT_RT_OPEN_ERROR;
        var error = Assert.Throws<IOException>(controller.Initialize);
        Assert.Contains("AxlOpenNoReset", error.Message);
        Assert.Contains("AXT_RT_OPEN_ERROR", error.Message);
        Assert.Equal("AxlOpenNoReset", Assert.Single(AjinSdk.Calls).Operation);
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
        using var log = new ApplicationLog();
        using var controller = new AjinController(new(), log);
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
            call => call.Operation is "AxdInfoGetModule" or "AxlOpen" or "AxmMotLoadParaAll");
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
        Assert.Equal(2, AjinSdk.Calls.Count(call => call.Operation == "AxlOpenNoReset"));
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
        Assert.Single(AjinSdk.Calls, call => call.Operation == "AxlOpenNoReset");
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
    public void NegativeInterruptCannotWrapIntoTheUnsignedNoResetArgument()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AjinController(new() { InterruptNumber = -1 }));
        Assert.Empty(AjinSdk.Calls);
    }
}
