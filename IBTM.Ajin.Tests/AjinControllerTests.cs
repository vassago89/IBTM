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
    public AjinControllerTests() => AjinSdk.Reset();

    [Fact]
    public void MotionInitializationReadsAlarmsAndServoOffWithoutTurningServosOn()
    {
        using var controller = new AjinController(new());
        AjinSdk.MotionAxes[9] = new(Mechanical: (1U << 4) | (1U << 7), Position: 1234);
        AjinSdk.MotionAxes[10] = new(Mechanical: 1U << 5, HomeResult: 1, Position: -567);
        var motion = new AjinMotionService(controller, new() { Number = 9 }, new() { Number = 10 }, null,
            0.01, new(), new(), new(), null);
        var status = new MotionStatus(motion);

        motion.Initialize();
        motion.Initialize();
        status.RefreshAxes();

        Assert.True(motion.IsReady);
        Assert.Equal(new MotionPosition(12.34, -5.67, 0), status.Position);
        Assert.Equal(AxisCondition.Alarm, status.Axes[MotionAxis.X].Condition);
        Assert.True(status.Axes[MotionAxis.X].State!.Value.HomeSensor);
        Assert.Equal(AxisCondition.ServoOff, status.Axes[MotionAxis.Y].Condition);
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation is
            nameof(CAXM.AxmSignalServoOn) or nameof(CAXM.AxmSignalServoAlarmReset));

        var error = Assert.Throws<IOException>(() => motion.SetServo(MotionAxis.X, true));
        Assert.Contains("AXT_RT_MOTION_ERROR_IN_ALARM", error.Message);
        Assert.Contains("axis=9", error.Message);
        status.RefreshAxes();
        Assert.True(motion.IsReady); // An operator command failure must not hide feedback.
        Assert.Equal(AxisCondition.Alarm, status.Axes[MotionAxis.X].Condition);
        Assert.Equal(new MotionPosition(12.34, -5.67, 0), status.Position);

        var reset = new AjinSdk.Call(nameof(CAXM.AxmSignalServoAlarmReset), Value: 1, Axis: 9);
        AjinSdk.Results[reset] = (uint)AXT_FUNC_RESULT.AXT_RT_MOTION_ERROR_IN_ALARM;
        Assert.Throws<IOException>(motion.Reset);
        status.RefreshAxes();
        Assert.Equal(AxisCondition.Alarm, status.Axes[MotionAxis.X].Condition);
        AjinSdk.Results.Remove(reset);
        motion.Reset(); // Explicit RESET can now reach alarm reset before requesting Servo ON.
        status.RefreshAxes();
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
        Assert.Contains(log.ReadAfter(0), entry => entry.Message.Contains("AxlOpenNoReset(interrupt=7)")
            && entry.Message.Contains("AXT_RT_SUCCESS (0x00000000)"));
        Assert.Contains(log.ReadAfter(0), entry => entry.Message.Contains(".mot loading is skipped"));
        Assert.Equal(new int?[] { 0, 1, 2, 3, 4 }, AjinSdk.Calls.Where(call => call.Operation == "AxdInfoGetModule").Select(call => call.Module));
        Assert.Contains(log.ReadAfter(0), entry => entry.Message.Contains("input modules=[0,1,4], output modules=[2,3,4]"));
        Assert.Contains(log.ReadAfter(0), entry => entry.Message.Contains("module=4, board=0, position=4, type=AXT_SIO_RDB32RTEX (0x86), DI=16, DO=16"));
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation.StartsWith("AxdoWrite", StringComparison.Ordinal));
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
        Assert.Equal(new[] {
            new AjinSdk.Call("AxdiReadInportWord", 0, 0), new AjinSdk.Call("AxdiReadInportWord", 0, 1),
            new AjinSdk.Call("AxdiReadInportWord", 1, 0), new AjinSdk.Call("AxdiReadInportWord", 1, 1),
            new AjinSdk.Call("AxdiReadInportWord", 4, 0) }, AjinSdk.Calls);
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
    public void BitIoUsesSeparateModuleListsAndTheExisting32BitSlotAddresses(int channel, int inputModule, int outputModule, int offset)
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
        Assert.All(AjinSdk.Calls.Skip(1), call => { Assert.Equal(outputModule, call.Module); Assert.Equal(offset, call.Offset); });
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
    [InlineData(int.MaxValue)]
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
        if (input) settings.RtexInputModules = [2];
        else settings.RtexOutputModules = [0];
        using var controller = new AjinController(settings);
        var error = Assert.Throws<IOException>(controller.Initialize);
        Assert.Contains(input ? "input module=2 has 0 DI points" : "output module=0 has 0 DO points", error.Message);
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation.StartsWith("AxdiRead", StringComparison.Ordinal)
            || call.Operation.StartsWith("AxdoWrite", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(24)]
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
        settings.MotionParameterFile = "changed.mot";
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
        Assert.Contains(log.ReadAfter(0), entry => entry.Message.Contains("AXT_RT_OPEN_ERROR"));
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
        AjinSdk.BeforeCall = call => { if (call.Operation == "AxlClose") throw new IOException("Test cleanup failure"); };
        var error = Assert.Throws<IOException>(controller.Initialize);
        Assert.Contains("AxdInfoIsDIOModule", error.Message);
        Assert.Contains("Test cleanup failure", Assert.IsType<string>(error.Data["AjinCloseError"]));
        Assert.Contains(log.ReadAfter(0), entry => entry.Level == "ERROR");
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation is "AxdInfoGetModule" or "AxlOpen" or "AxmMotLoadParaAll");
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
        Assert.Throws<ArgumentNullException>(() => new AjinController(new() { RtexInputModules = null! }));
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
        Parallel.For(0, 100, index =>
        {
            controller.ReadRtexInputs(new uint[3]);
            controller.ReadRtexOutput(index % 16);
            controller.WriteRtexOutput(index % 16, false);
        });
        Assert.Equal(700, AjinSdk.Calls.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("missing.mot")]
    public void SavedMotionParameterPathIsNotUsed(string? path)
    {
        using var controller = new AjinController(new() { MotionParameterFile = path! });
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation is "AxlOpen" or "AxmMotLoadParaAll")
                throw new InvalidOperationException("Reset or parameter loading must not be attempted.");
        };
        controller.Initialize();
        Assert.False(controller.ReadRtexInput(0));
        controller.Dispose();
        controller.Initialize();
        Assert.Equal(2, AjinSdk.Calls.Count(call => call.Operation == "AxlOpenNoReset"));
    }

    [Fact]
    public void NegativeInterruptCannotWrapIntoTheUnsignedNoResetArgument()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AjinController(new() { InterruptNumber = -1 }));
        Assert.Empty(AjinSdk.Calls);
    }
}
