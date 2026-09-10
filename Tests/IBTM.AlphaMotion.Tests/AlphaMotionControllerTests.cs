using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IBTM.AlphaMotion;
using IBTM.Core;
using Shared;
using Xunit;

namespace IBTM.AlphaMotion.Tests;

public sealed class AlphaMotionControllerTests
{
    public AlphaMotionControllerTests()
    {
        TMCAEDLL.Reset();
    }

    private static TMCAEDLL.Call[] NativeCalls()
    {
        return TMCAEDLL.Calls.Where(call => call.Operation != "AIO_GetErrorCode").ToArray();
    }

    [Theory]
    [InlineData(0, 0xAE2EU, 0x13U)]
    [InlineData(1, 0U, 0U)]
    [InlineData(0, uint.MaxValue, uint.MaxValue)]
    public void InitializationUsesSampleApisAndProbesBothPortsBeforeReadiness(
        int result,
        uint model,
        uint communication)
    {
        TMCAEDLL.DefaultResult = result;
        TMCAEDLL.Model = model;
        TMCAEDLL.Communication = communication;
        TMCAEDLL.Inputs = 8;
        using var log = new ApplicationLog();
        using var controller = new AlphaMotionController(new(), log);
        controller.Initialize();
        controller.Initialize();

        Assert.Equal(
            new[]
            {
                "AIO_LoadDevice", "AIO_BoardInfo", "AIO_GetDIDWord", "AIO_GetDODWord",
                "AIO_GetDIDWord", "AIO_GetDODWord"
            },
            NativeCalls().Select(call => call.Operation));
        Assert.All(NativeCalls().Skip(1), call => Assert.Equal((ushort)0, call.Card));
        Assert.Contains(
            log.Entries,
            entry =>
                entry.Message.Contains("card=0, DI=16, DO=16")
                    && entry.Message.Contains("initial DI=0x00000008, DO=0x00000000"));
        Assert.Contains(
            log.Entries,
            entry =>
                entry.Message.Contains(
                    $"AIO_BoardInfo (card=0): result={result}, ERR_SUCCESS (0); model=0x{model:X}, communication=0x{communication:X}, DI=16, DO=16"));
        Assert.DoesNotContain(
            NativeCalls(),
            call => call.Operation.StartsWith("AIO_Put", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, 0U)]
    [InlineData(1, 0xFFFFU)]
    public void ValidPortValuesIncludeAllOffAndAllOn(int result, uint value)
    {
        using var controller = new AlphaMotionController(new());
        controller.Initialize();
        TMCAEDLL.Results["AIO_GetDIDWord"] = result;
        TMCAEDLL.Inputs = value;
        TMCAEDLL.Calls.Clear();
        Assert.Equal(value, controller.ReadInputs());
        Assert.Equal(new TMCAEDLL.Call("AIO_GetDIDWord", 0, Group: 0), Assert.Single(NativeCalls()));
    }

    [Fact]
    public void InitializationReloadsAFailedSessionButOnlyProbesAHealthySession()
    {
        using var controller = new AlphaMotionController(new());
        controller.Initialize();
        controller.Initialize();
        Assert.Single(NativeCalls(), call => call.Operation == "AIO_LoadDevice");
        Assert.DoesNotContain(NativeCalls(), call => call.Operation == "AIO_UnloadDevice");

        TMCAEDLL.Results["AIO_GetDIDWord"] = -1;
        Assert.Throws<IOException>(() => controller.ReadInputs());
        TMCAEDLL.BeforeCall = operation =>
        {
            if (operation == "AIO_LoadDevice")
                TMCAEDLL.Results.Clear();
        };

        controller.Initialize();
        Assert.Equal(0U, controller.ReadInputs());
        Assert.Equal(2, NativeCalls().Count(call => call.Operation == "AIO_LoadDevice"));
        Assert.Single(NativeCalls(), call => call.Operation == "AIO_UnloadDevice");
        Assert.DoesNotContain(NativeCalls(), call => call.Operation.StartsWith("AIO_Put"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    public void BitsUseTheSamplePortApisAndSelectedCard(int bit)
    {
        TMCAEDLL.DefaultResult = 0;
        using var controller = new AlphaMotionController(new() { ControllerNumber = 2 });
        controller.Initialize();
        TMCAEDLL.Inputs = TMCAEDLL.Outputs = 1U << bit;
        TMCAEDLL.Calls.Clear();
        Assert.True(controller.ReadInput(bit));
        Assert.True(controller.ReadOutput(bit));
        Assert.Equal(
            new[] { new TMCAEDLL.Call("AIO_GetDIDWord", 2, Group: 0), new TMCAEDLL.Call(
                "AIO_GetDODWord",
                2,
                Group: 0) },
            NativeCalls());
        TMCAEDLL.Inputs = TMCAEDLL.Outputs = 0;
        Assert.False(controller.ReadInput(bit));
        Assert.False(controller.ReadOutput(bit));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void OutputWritesChangeOnlyTheRequestedBitAndVerifyReadback(int result)
    {
        TMCAEDLL.DefaultResult = result;
        using var controller = new AlphaMotionController(new());
        controller.Initialize();
        TMCAEDLL.Outputs = 0x8001;
        TMCAEDLL.Calls.Clear();
        controller.WriteOutput(3, true);
        Assert.Equal(0x8009U, TMCAEDLL.Outputs);
        controller.WriteOutput(3, false);
        Assert.Equal(0x8001U, TMCAEDLL.Outputs);
        Assert.Equal(
            new[]
            {
                new TMCAEDLL.Call("AIO_PutDOBit", 0, 3, Value: 1),
                new TMCAEDLL.Call("AIO_GetDODWord", 0, Group: 0),
                new TMCAEDLL.Call("AIO_PutDOBit", 0, 3, Value: 0),
                new TMCAEDLL.Call("AIO_GetDODWord", 0, Group: 0)
            },
            NativeCalls());
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void SuccessfulLookingWriteWithoutMatchingReadbackIsRejected(int result, bool requested)
    {
        using var controller = new AlphaMotionController(new());
        controller.Initialize();
        TMCAEDLL.Results["AIO_PutDOBit"] = result;
        TMCAEDLL.SuppressOutputWrites = true;
        TMCAEDLL.Outputs = requested ? 0U : 8U;
        var error = Assert.Throws<IOException>(() => controller.WriteOutput(3, requested));
        Assert.Contains("AIO_PutDOBit (card=0, bit=3)", error.Message);
        Assert.Contains("Output readback mismatch", error.Message);
    }

    [Theory]
    [InlineData("AIO_BoardInfo", 0)]
    [InlineData("AIO_GetDIDWord", 0)]
    [InlineData("AIO_GetDODWord", 1)]
    public void UnwrittenRefParametersPreventInitialization(string operation, int result)
    {
        using var controller = new AlphaMotionController(new());
        TMCAEDLL.Results[operation] = result;
        TMCAEDLL.SkipRefWrites.Add(operation);
        var error = Assert.Throws<IOException>(controller.Initialize);
        Assert.Contains(operation, error.Message);
        Assert.Contains("Invalid or unchanged", error.Message);
        Assert.Contains("ERR_SUCCESS (0)", error.Message);
        Assert.Equal("AIO_UnloadDevice", NativeCalls()[^1].Operation);
        TMCAEDLL.Calls.Clear();
        Assert.Throws<IOException>(() => controller.WriteOutput(0, true));
        Assert.Empty(TMCAEDLL.Calls);
    }

    [Theory]
    [InlineData(0xAEU, 0U, 0U)]
    [InlineData(0xAE2EU, uint.MaxValue, 16U)]
    [InlineData(0xAE2EU, 16U, uint.MaxValue)]
    [InlineData(0xAE2EU, 65537U, 16U)]
    [InlineData(0xAE2EU, 16U, 65537U)]
    public void InvalidOrUnwrittenPointCountsPreventPortAccess(uint model, uint inputs, uint outputs)
    {
        using var controller = new AlphaMotionController(new());
        TMCAEDLL.Model = model;
        TMCAEDLL.InputCount = inputs;
        TMCAEDLL.OutputCount = outputs;
        var error = Assert.Throws<IOException>(controller.Initialize);
        Assert.Contains(
            $"model=0x{model:X}, communication=0x0, DI={inputs}, DO={outputs}",
            error.Message);
        Assert.DoesNotContain(
            NativeCalls(),
            call => call.Operation is "AIO_GetDIDWord" or "AIO_GetDODWord" or "AIO_PutDOBit");
    }

    [Theory]
    [InlineData(0U, 16U)]
    [InlineData(16U, 0U)]
    [InlineData(8U, 16U)]
    [InlineData(16U, 8U)]
    public void OnlyChannelsMissingFromTheReportedDirectionAreBlocked(uint inputs, uint outputs)
    {
        TMCAEDLL.InputCount = inputs;
        TMCAEDLL.OutputCount = outputs;
        using var controller = new AlphaMotionController(new());
        controller.Initialize();
        TMCAEDLL.Calls.Clear();
        if (inputs < 16)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => controller.ReadInput((int)inputs));
            Assert.Empty(TMCAEDLL.Calls);
            if (inputs == 0)
                Assert.Equal(0U, controller.ReadInputs());
            controller.WriteOutput(15, true);
            Assert.True(controller.ReadOutput(15));
        }
        else
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => controller.ReadOutput((int)outputs));
            Assert.Throws<ArgumentOutOfRangeException>(() => controller.WriteOutput((int)outputs, true));
            Assert.Empty(TMCAEDLL.Calls);
            TMCAEDLL.Inputs = 0x8000;
            Assert.True(controller.ReadInput(15));
        }
    }

    [Theory]
    [InlineData(0, 0x10000U)]
    [InlineData(1, uint.MaxValue)]
    public void WiderBoardValuesDoNotChangeTheSixteenChannelMapping(int result, uint value)
    {
        TMCAEDLL.DefaultResult = result;
        TMCAEDLL.InputCount = TMCAEDLL.OutputCount = 32;
        TMCAEDLL.Inputs = TMCAEDLL.Outputs = value;
        using var controller = new AlphaMotionController(new());
        controller.Initialize();
        Assert.Equal(value & 0xFFFFU, controller.ReadInputs());
        Assert.Equal((value & 8) != 0, controller.ReadInput(3));
        Assert.Equal((value & 8) != 0, controller.ReadOutput(3));
        controller.WriteOutput(3, true);
        Assert.Equal(value | 8U, TMCAEDLL.Outputs);
        TMCAEDLL.Calls.Clear();
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.ReadInput(16));
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.WriteOutput(16, true));
        Assert.Empty(TMCAEDLL.Calls);
    }

    [Theory]
    [InlineData("AIO_GetDIDWord", 0)]
    [InlineData("AIO_GetDODWord", 1)]
    public void WiderBoardsStillRejectUnwrittenReadBuffers(string operation, int result)
    {
        TMCAEDLL.DefaultResult = result;
        TMCAEDLL.InputCount = TMCAEDLL.OutputCount = 32;
        TMCAEDLL.SkipRefWrites.Add(operation);
        using var controller = new AlphaMotionController(new());
        var error = Assert.Throws<IOException>(controller.Initialize);
        Assert.Contains("Invalid or unchanged port data", error.Message);
        Assert.DoesNotContain(NativeCalls(), call => call.Operation == "AIO_PutDOBit");
    }

    [Fact]
    public void ReinitializationUsesTheNewPointCounts()
    {
        using var controller = new AlphaMotionController(new());
        controller.Initialize();
        controller.Dispose();
        TMCAEDLL.InputCount = TMCAEDLL.OutputCount = 8;
        controller.Initialize();
        TMCAEDLL.Calls.Clear();
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.ReadInput(8));
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.WriteOutput(8, true));
        Assert.Empty(TMCAEDLL.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PortValidationUsesEachDirectionsActualCount(bool input)
    {
        TMCAEDLL.InputCount = input ? 8U : 16U;
        TMCAEDLL.OutputCount = input ? 16U : 8U;
        TMCAEDLL.Inputs = input ? 0xFFU : 0xFFFFU;
        TMCAEDLL.Outputs = input ? 0xFFFFU : 0xFFU;
        using var controller = new AlphaMotionController(new());
        controller.Initialize();
        Assert.True(controller.ReadInput(7));
        Assert.True(controller.ReadOutput(7));
        if (input)
            TMCAEDLL.Inputs = 0x100;
        else
            TMCAEDLL.Outputs = 0x100;
        var error = Assert.Throws<IOException>(
            () =>
            {
                if (input)
                    controller.ReadInputs();
                else
                    controller.ReadOutput(7);
            });
        Assert.Contains("Reported channel count=8", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FullWidthAllOnCanTransitionToAllOffDuringBufferValidation(bool input)
    {
        TMCAEDLL.InputCount = TMCAEDLL.OutputCount = 32;
        using var controller = new AlphaMotionController(new());
        controller.Initialize();
        var operation = input ? "AIO_GetDIDWord" : "AIO_GetDODWord";
        var reads = 0;
        TMCAEDLL.BeforeCall = name =>
        {
            if (name != operation)
                return;
            var value = ++reads == 1 ? uint.MaxValue : 0U;
            if (input)
                TMCAEDLL.Inputs = value;
            else
                TMCAEDLL.Outputs = value;
        };
        if (input)
            Assert.Equal(0U, controller.ReadInputs());
        else
            Assert.False(controller.ReadOutput(0));
        Assert.Equal(3, reads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BufferValidationDoesNotHideAnSdkFailureOnTheRepeatedRead(bool input)
    {
        TMCAEDLL.InputCount = TMCAEDLL.OutputCount = 32;
        using var controller = new AlphaMotionController(new());
        controller.Initialize();
        TMCAEDLL.Inputs = TMCAEDLL.Outputs = uint.MaxValue;
        var operation = input ? "AIO_GetDIDWord" : "AIO_GetDODWord";
        var reads = 0;
        TMCAEDLL.BeforeCall = name =>
        {
            if (name == operation && ++reads == 2)
                TMCAEDLL.ErrorCode = tmcDef.ERR_INVALID_GROUP;
        };
        var error = Assert.Throws<IOException>(
            () =>
            {
                if (input)
                    controller.ReadInputs();
                else
                    controller.ReadOutput(0);
            });
        Assert.Contains("ERR_INVALID_GROUP", error.Message);
        Assert.Equal(2, reads);
    }

    [Theory]
    [InlineData(true, 0x10000U)]
    [InlineData(false, uint.MaxValue)]
    public void InvalidPortValuesAreNeverMaskedIntoValidSignals(bool input, uint value)
    {
        using var controller = new AlphaMotionController(new());
        controller.Initialize();
        if (input)
            TMCAEDLL.Inputs = value;
        else
            TMCAEDLL.Outputs = value;
        var error = Assert.Throws<IOException>(
            () =>
            {
                if (input)
                    controller.ReadInput(3);
                else
                    controller.ReadOutput(3);
            });
        Assert.Contains($"0x{value:X8}", error.Message);
        Assert.Contains("Invalid or unchanged port data", error.Message);
    }

    [Theory]
    [InlineData("AIO_BoardInfo", 0, tmcDef.ERR_INVALID_BOARD_ID)]
    [InlineData("AIO_GetDIDWord", 0, tmcDef.ERR_INVALID_GROUP)]
    [InlineData("AIO_GetDODWord", 1, tmcDef.ERR_INVALID_GROUP)]
    [InlineData("AIO_BoardInfo", -1, tmcDef.ERR_SUCCESS)]
    [InlineData("AIO_GetDIDWord", 2, tmcDef.ERR_SUCCESS)]
    public void SdkErrorsAndUnknownResultsStillPreventReadiness(
        string operation,
        int result,
        int errorCode)
    {
        using var controller = new AlphaMotionController(new());
        TMCAEDLL.Results[operation] = result;
        TMCAEDLL.Errors[operation] = errorCode;
        var error = Assert.Throws<IOException>(controller.Initialize);
        Assert.Contains($"{operation} (card=0)", error.Message);
        Assert.Contains($"result {result};", error.Message);
        Assert.Contains($"({errorCode})", error.Message);
        Assert.DoesNotContain(NativeCalls(), call => call.Operation == "AIO_PutDOBit");
    }

    [Fact]
    public void FailedInitializationCanBeRetriedAfterTheApiFillsItsData()
    {
        TMCAEDLL.DefaultResult = 0;
        using var controller = new AlphaMotionController(new());
        TMCAEDLL.SkipRefWrites.Add("AIO_BoardInfo");
        Assert.Throws<IOException>(controller.Initialize);
        Assert.Throws<IOException>(() => controller.ReadInputs());
        TMCAEDLL.SkipRefWrites.Clear();
        TMCAEDLL.Inputs = 8;
        controller.Initialize();
        Assert.Equal(8U, controller.ReadInputs());
    }

    [Fact]
    public void WriteSdkErrorIsNotHiddenEvenIfTheOutputAlreadyMatches()
    {
        using var controller = new AlphaMotionController(new());
        controller.Initialize();
        TMCAEDLL.Outputs = 8;
        TMCAEDLL.Results["AIO_PutDOBit"] = 0;
        TMCAEDLL.Errors["AIO_PutDOBit"] = tmcDef.ERR_INVALID_CHANNEL;
        TMCAEDLL.Calls.Clear();
        var error = Assert.Throws<IOException>(() => controller.WriteOutput(3, true));
        Assert.Contains("ERR_INVALID_CHANNEL (-200)", error.Message);
        Assert.Equal("AIO_PutDOBit", Assert.Single(NativeCalls()).Operation);
    }

    [Fact]
    public void WriteReadbackFailureIsNotReportedAsACompletedOutputCommand()
    {
        using var controller = new AlphaMotionController(new());
        controller.Initialize();
        TMCAEDLL.SkipRefWrites.Add("AIO_GetDODWord");
        var error = Assert.Throws<IOException>(() => controller.WriteOutput(3, true));
        Assert.Contains("AIO_GetDODWord", error.Message);
        Assert.Contains("Invalid or unchanged port data", error.Message);
    }

    [Theory]
    [InlineData(-1, tmcDef.ERR_DEVICE_LOAD)]
    [InlineData(-1, tmcDef.ERR_SUCCESS)]
    public void NegativeLoadResultsNeverProceedToCardQueries(int result, int errorCode)
    {
        using var controller = new AlphaMotionController(new());
        TMCAEDLL.Results["AIO_LoadDevice"] = result;
        TMCAEDLL.Errors["AIO_LoadDevice"] = errorCode;
        Assert.Throws<IOException>(controller.Initialize);
        Assert.Equal("AIO_LoadDevice", Assert.Single(NativeCalls()).Operation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void UnloadWithNoErrorCanReinitialize(int result)
    {
        using var controller = new AlphaMotionController(new());
        controller.Initialize();
        TMCAEDLL.Results["AIO_UnloadDevice"] = result;
        TMCAEDLL.Calls.Clear();
        controller.Dispose();
        controller.Dispose();
        Assert.Equal("AIO_UnloadDevice", Assert.Single(NativeCalls()).Operation);
        Assert.Throws<IOException>(() => controller.ReadInputs());
        controller.Initialize();
        Assert.Equal(0U, controller.ReadInputs());
    }

    [Fact]
    public void CleanupFailurePreservesTheOriginalBoardInfoError()
    {
        using var log = new ApplicationLog();
        using var controller = new AlphaMotionController(new(), log);
        TMCAEDLL.Errors["AIO_BoardInfo"] = tmcDef.ERR_INVALID_BOARD_ID;
        TMCAEDLL.Errors["AIO_UnloadDevice"] = tmcDef.ERR_UNKNOWN;
        var error = Assert.Throws<IOException>(controller.Initialize);
        Assert.Contains("AIO_BoardInfo", error.Message);
        Assert.Contains("ERR_INVALID_BOARD_ID (-400)", error.Message);
        Assert.Contains("ERR_UNKNOWN", Assert.IsType<string>(error.Data["AlphaMotionUnloadError"]));
        Assert.Contains(log.Entries, entry => entry.Level == "ERROR");
    }

    [Fact]
    public void DisposalFailureDoesNotPermitSubsequentReads()
    {
        using var controller = new AlphaMotionController(new());
        controller.Initialize();
        TMCAEDLL.Errors["AIO_UnloadDevice"] = tmcDef.ERR_UNKNOWN;
        Assert.Throws<IOException>(controller.Dispose);
        TMCAEDLL.Calls.Clear();
        Assert.Throws<IOException>(() => controller.ReadInputs());
        Assert.Empty(TMCAEDLL.Calls);
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
        Assert.All(NativeCalls(), call => Assert.Equal((ushort)2, call.Card));
        controller.Dispose();
        using var next = new AlphaMotionController(settings);
        next.Initialize();
        TMCAEDLL.Calls.Clear();
        next.ReadInputs();
        Assert.Equal((ushort)5, Assert.Single(NativeCalls()).Card);
    }

    [Fact]
    public void RepeatedPollingDoesNotFloodTheLog()
    {
        using var log = new ApplicationLog();
        using var controller = new AlphaMotionController(new(), log);
        controller.Initialize();
        var sequence = log.LatestSequence;
        for (var index = 0; index < 100; index++)
            controller.ReadInputs();
        Assert.Equal(sequence, log.LatestSequence);
        TMCAEDLL.Results["AIO_GetDIDWord"] = 0;
        TMCAEDLL.Inputs = 8;
        for (var index = 0; index < 100; index++)
            controller.ReadInputs();
        Assert.Contains("DI=0x00000008", Assert.Single(log.Entries, entry => entry.Sequence > sequence).Message);
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
    public void InvalidChannelsAreRejectedWithoutNativeCalls(int bit)
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
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AlphaMotionController(new() { ControllerNumber = card }));
        Assert.Empty(TMCAEDLL.Calls);
    }

    [Fact]
    public void ConcurrentReadsAndWritesUseOneControllerSafely()
    {
        using var controller = new AlphaMotionController(new());
        controller.Initialize();
        TMCAEDLL.Calls.Clear();
        Parallel.For(
            0,
            100,
            index =>
            {
                controller.ReadInputs();
                controller.ReadOutput(index % 16);
                controller.WriteOutput(index % 16, false);
            });
        Assert.Equal(400, NativeCalls().Length);
        Assert.All(NativeCalls(), call => Assert.Equal((ushort)0, call.Card));
    }

}
