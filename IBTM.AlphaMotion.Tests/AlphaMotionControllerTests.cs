using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using IBTM.AlphaMotion;
using IBTM.Core;
using Shared;
using Xunit;

namespace IBTM.AlphaMotion.Tests;

public sealed class AlphaMotionControllerTests
{
    public AlphaMotionControllerTests() => TMCAEDLL.Reset();

    [Fact]
    public void InitializationLoadsAseriesAndChecksCountsWithoutWritingOutputs()
    {
        using var log = new ApplicationLog();
        using var controller = new AlphaMotionController(new(), log);
        controller.Initialize();
        controller.Initialize();

        Assert.Equal(new[] { "AIO_LoadDevice", "AIO_GetDiNum", "AIO_GetDoNum" },
            TMCAEDLL.Calls.Select(call => call.Operation));
        Assert.All(TMCAEDLL.Calls.Skip(1), call => Assert.Equal((ushort)0, call.Card));
        Assert.Contains(log.ReadAfter(0), entry => entry.Message.Contains("card=0, DI=16, DO=16"));
    }

    [Theory]
    [InlineData(0x0008)]
    [InlineData(0x8000)]
    [InlineData(0xFFFF)]
    public void ReadsSixteenInputsFromWordGroupZero(int value)
    {
        using var controller = new AlphaMotionController(new());
        controller.Initialize();
        TMCAEDLL.Calls.Clear();
        TMCAEDLL.Inputs = (ushort)value;

        Assert.Equal((uint)value, controller.ReadInputs());
        Assert.Equal(new TMCAEDLL.Call("AIO_GetDIWord", 0, Group: 0), Assert.Single(TMCAEDLL.Calls));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(15)]
    public void ReadsBitsUsingCardAndChannelWithoutAStation(int bit)
    {
        using var controller = new AlphaMotionController(new() { ControllerNumber = 2 });
        controller.Initialize();
        TMCAEDLL.Calls.Clear();
        TMCAEDLL.Inputs = TMCAEDLL.Outputs = (ushort)(1 << bit);

        Assert.True(controller.ReadInput(bit));
        Assert.True(controller.ReadOutput(bit));
        Assert.Equal(new[] { new TMCAEDLL.Call("AIO_GetDIBit", 2, (ushort)bit),
            new TMCAEDLL.Call("AIO_GetDOBit", 2, (ushort)bit) }, TMCAEDLL.Calls);
        TMCAEDLL.Inputs = TMCAEDLL.Outputs = 0;
        Assert.False(controller.ReadInput(bit));
        Assert.False(controller.ReadOutput(bit));
    }

    [Fact]
    public void OutputWritesChangeOnlyTheRequestedBit()
    {
        using var controller = new AlphaMotionController(new());
        controller.Initialize();
        TMCAEDLL.Calls.Clear();
        TMCAEDLL.Outputs = 0x8001;

        controller.WriteOutput(3, true);
        Assert.Equal(0x8009, TMCAEDLL.Outputs);
        controller.WriteOutput(3, false);
        Assert.Equal(0x8001, TMCAEDLL.Outputs);
        Assert.Equal(new[] { new TMCAEDLL.Call("AIO_PutDOBit", 0, 3, Value: 1),
            new TMCAEDLL.Call("AIO_PutDOBit", 0, 3, Value: 0) }, TMCAEDLL.Calls);
    }

    [Fact]
    public void CardIdentityDoesNotChangeUntilANewControllerIsCreated()
    {
        var settings = new AlphaMotionSettings { ControllerNumber = 2 };
        using var controller = new AlphaMotionController(settings);
        controller.Initialize();
        settings.ControllerNumber = 5;
        TMCAEDLL.Calls.Clear();
        controller.ReadInput(1);
        controller.ReadOutput(1);
        controller.WriteOutput(1, false);
        Assert.All(TMCAEDLL.Calls, call => Assert.Equal((ushort)2, call.Card));
        controller.Dispose();

        using var next = new AlphaMotionController(settings);
        next.Initialize();
        TMCAEDLL.Calls.Clear();
        next.ReadInputs();
        Assert.Equal((ushort)5, Assert.Single(TMCAEDLL.Calls).Card);
    }

    [Fact]
    public void UninitializedAccessDoesNotCallNativeFunctions()
    {
        using var controller = new AlphaMotionController(new());
        Assert.Throws<IOException>(() => controller.ReadInput(0));
        Assert.Throws<IOException>(() => controller.ReadInputs());
        Assert.Throws<IOException>(() => controller.ReadOutput(0));
        Assert.Throws<IOException>(() => controller.WriteOutput(0, true));
        controller.Dispose();
        Assert.Empty(TMCAEDLL.Calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(16)]
    [InlineData(65536)]
    public void InvalidChannelsAreRejectedWithoutWrappingOrCallingNativeFunctions(int bit)
    {
        using var controller = new AlphaMotionController(new());
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.ReadInput(bit));
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.ReadOutput(bit));
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.WriteOutput(bit, true));
        Assert.Empty(TMCAEDLL.Calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void InvalidCardsAreRejectedWithoutAddressWrapping(int card)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlphaMotionController(new() { ControllerNumber = card }));
        Assert.Empty(TMCAEDLL.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void NonnegativeLoadResultIsABoardCountMinusOneAndAllowsInputScanning(int result)
    {
        using var log = new ApplicationLog();
        using var controller = new AlphaMotionController(new(), log);
        TMCAEDLL.Results["AIO_LoadDevice"] = result;
        TMCAEDLL.ErrorCode = tmcDef.ERR_SUCCESS;
        TMCAEDLL.Inputs = 0x0008;

        controller.Initialize();

        Assert.Equal(0x0008U, controller.ReadInputs());
        Assert.Equal(new[] { "AIO_LoadDevice", "AIO_GetDiNum", "AIO_GetDoNum", "AIO_GetDIWord" },
            TMCAEDLL.Calls.Select(call => call.Operation));
        Assert.Contains(log.ReadAfter(0), entry => entry.Message.Contains($"loaded boards={result + 1}"));
    }

    [Theory]
    [InlineData(-1, tmcDef.ERR_DEVICE_LOAD, "ERR_DEVICE_LOAD")]
    [InlineData(-100, tmcDef.ERR_INVALID_HANDLE, "ERR_INVALID_HANDLE")]
    [InlineData(-9999, tmcDef.ERR_UNKNOWN, "ERR_UNKNOWN")]
    [InlineData(-1, tmcDef.ERR_SUCCESS, "ERR_SUCCESS")]
    public void NegativeLoadResultFailsWithoutQueryingCardsOrAllowingIo(int result, int errorCode, string errorName)
    {
        using var controller = new AlphaMotionController(new());
        TMCAEDLL.Results["AIO_LoadDevice"] = result;
        TMCAEDLL.ErrorCode = errorCode;
        var error = Assert.Throws<IOException>(controller.Initialize);

        Assert.Contains("AIO_LoadDevice (card=0)", error.Message);
        Assert.Contains($"result {result}; {errorName} ({errorCode})", error.Message);
        Assert.Throws<IOException>(() => controller.ReadInputs());
        Assert.Throws<IOException>(() => controller.WriteOutput(0, true));
        controller.Dispose();
        Assert.Equal(new[] { "AIO_LoadDevice", "AIO_GetErrorCode" }, TMCAEDLL.Calls.Select(call => call.Operation));
    }

    [Fact]
    public void FailedLoadCanBeRetriedWithTheSingleBoardSuccessResult()
    {
        using var controller = new AlphaMotionController(new());
        TMCAEDLL.Results["AIO_LoadDevice"] = -1;
        Assert.Throws<IOException>(controller.Initialize);

        TMCAEDLL.Results["AIO_LoadDevice"] = 0;
        controller.Initialize();
        Assert.Equal(0U, controller.ReadInputs());
        Assert.Equal(2, TMCAEDLL.Calls.Count(call => call.Operation == "AIO_LoadDevice"));
    }

    [Fact]
    public void ZeroInputScanStatusStillFailsEvenWhenTheLastErrorIsSuccess()
    {
        using var controller = new AlphaMotionController(new());
        controller.Initialize();
        TMCAEDLL.Results["AIO_GetDIWord"] = tmcDef.TMC_ST_FALSE;
        TMCAEDLL.ErrorCode = tmcDef.ERR_SUCCESS;

        var error = Assert.Throws<IOException>(() => controller.ReadInputs());

        Assert.Contains("AIO_GetDIWord (card=0)", error.Message);
        Assert.Contains("result 0; ERR_SUCCESS (0)", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2)]
    public void OnlyOneIsAcceptedAsSuccessAndInputErrorsAreNotReturnedAsOff(int result)
    {
        using var controller = new AlphaMotionController(new() { ControllerNumber = 3 });
        controller.Initialize();
        TMCAEDLL.Results["AIO_GetDIBit"] = result;
        TMCAEDLL.ErrorCode = tmcDef.ERR_INVALID_CHANNEL;
        var error = Assert.Throws<IOException>(() => controller.ReadInput(7));

        Assert.Contains("AIO_GetDIBit (card=3, bit=7)", error.Message);
        Assert.Contains($"result {result}; ERR_INVALID_CHANNEL (-200)", error.Message);
    }

    [Theory]
    [InlineData(32, 16)]
    [InlineData(16, 32)]
    [InlineData(0, 0)]
    public void UnexpectedChannelCountsFailAndUnloadWithoutAnOutputWrite(int inputs, int outputs)
    {
        using var controller = new AlphaMotionController(new());
        TMCAEDLL.InputCount = (ushort)inputs;
        TMCAEDLL.OutputCount = (ushort)outputs;
        var error = Assert.Throws<IOException>(controller.Initialize);

        Assert.Contains($"found {inputs} DI / {outputs} DO", error.Message);
        Assert.Equal("AIO_UnloadDevice", TMCAEDLL.Calls[^1].Operation);
        Assert.DoesNotContain(TMCAEDLL.Calls, call => call.Operation.StartsWith("AIO_Put", StringComparison.Ordinal));
        Assert.Throws<IOException>(() => controller.ReadInputs());
    }

    [Fact]
    public void CleanupFailureDoesNotReplaceTheOriginalInitializationFailure()
    {
        using var log = new ApplicationLog();
        using var controller = new AlphaMotionController(new(), log);
        TMCAEDLL.Results["AIO_GetDiNum"] = 0;
        TMCAEDLL.Results["AIO_UnloadDevice"] = 0;
        TMCAEDLL.ErrorCode = tmcDef.ERR_INVALID_BOARD_ID;
        TMCAEDLL.BeforeCall = operation =>
        {
            if (operation == "AIO_UnloadDevice") TMCAEDLL.ErrorCode = tmcDef.ERR_UNKNOWN;
        };
        var error = Assert.Throws<IOException>(controller.Initialize);

        Assert.Contains("AIO_GetDiNum", error.Message);
        Assert.Contains("ERR_INVALID_BOARD_ID (-400)", error.Message);
        Assert.Contains("ERR_UNKNOWN", Assert.IsType<string>(error.Data["AlphaMotionUnloadError"]));
        Assert.Contains(log.ReadAfter(0), entry => entry.Level == "ERROR" && entry.Detail!.Contains("AIO_UnloadDevice"));
    }

    [Fact]
    public void DisposalUnloadsOnceAndAllowsLaterReinitialization()
    {
        using var controller = new AlphaMotionController(new());
        controller.Initialize();
        TMCAEDLL.Calls.Clear();
        controller.Dispose();
        controller.Dispose();
        Assert.Equal("AIO_UnloadDevice", Assert.Single(TMCAEDLL.Calls).Operation);
        Assert.Throws<IOException>(() => controller.ReadInput(0));
        controller.Initialize();
        Assert.Equal(0U, controller.ReadInputs());
    }

    [Fact]
    public void DisposalFailureDoesNotPermitSubsequentReads()
    {
        using var controller = new AlphaMotionController(new());
        controller.Initialize();
        TMCAEDLL.Results["AIO_UnloadDevice"] = 0;
        Assert.Throws<IOException>(controller.Dispose);
        TMCAEDLL.Calls.Clear();
        Assert.Throws<IOException>(() => controller.ReadInputs());
        Assert.Empty(TMCAEDLL.Calls);
    }

    [Fact]
    public void ConcurrentReadsAndWritesUseOneControllerSafely()
    {
        using var controller = new AlphaMotionController(new());
        controller.Initialize();
        TMCAEDLL.Calls.Clear();
        Parallel.For(0, 100, index =>
        {
            controller.ReadInputs();
            controller.ReadOutput(index % 16);
            controller.WriteOutput(index % 16, false);
        });
        Assert.Equal(300, TMCAEDLL.Calls.Count);
        Assert.All(TMCAEDLL.Calls, call => Assert.Equal((ushort)0, call.Card));
    }

    [Fact]
    public void LegacySettingsRetainCardNumberAndIgnoreOldMotionnetProperties()
    {
        var settings = JsonSerializer.Deserialize<AlphaMotionSettings>(
            """{"ControllerNumber":2,"StationNumber":1,"CommunicationSpeed":"Mbps20"}""")!;
        Assert.Equal(2, settings.ControllerNumber);
        Assert.Equal("""{"ControllerNumber":2}""", JsonSerializer.Serialize(settings));
        Assert.Equal(0, new AlphaMotionSettings().ControllerNumber);
    }
}
