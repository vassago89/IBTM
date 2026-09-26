using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.BoltFeeder;
using IBTM.Conveyor;
using IBTM.Device;
using IBTM.Core;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbPlacement;
using IBTM.Storage;
using IBTM.Virtual;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class MachineStoreTests
{
    [Fact]
    public async Task InspectionWaitingAndPickupAreSavedIndependently()
    {
        var store = VirtualTest.OpenMachineStore();
        var legacy = JsonSerializer.Deserialize<NgCarrierTransferSettings>(
            """{"PickupSafeX":157.283,"CarrierPickupPosition":{"X":999,"Y":456.789}}""")!;
        store.SaveSettings([legacy]);

        var settings = await MachineSettings.LoadAsync(store);
        var transfer = settings.NgCarrierTransfer;
        Assert.NotSame(transfer.CarrierPickupPosition, transfer.WaitingPosition);
        Assert.Equal((157.283, 456.789), (transfer.WaitingPosition!.X, transfer.WaitingPosition.Y));
        var waiting = VirtualTest.CreateTeachingPoint(
            new(TeachingTarget.InspectionWaiting, MotionGroup.InspectionGantry, TeachMode.XYOnly), settings);
        var pickup = VirtualTest.CreateTeachingPoint(
            new(TeachingTarget.NgCarrierPickup, MotionGroup.InspectionGantry, TeachMode.XYOnly), settings);

        pickup.Teach(160, 460, 0);
        Assert.Equal((157.283, 456.789), (waiting.Coordinates!.X, waiting.Coordinates!.Y));
        waiting.Teach(120, 400, 0);
        Assert.Equal((160, 460), (pickup.Coordinates!.X, pickup.Coordinates!.Y));
        await store.SaveSettingsAsync(settings.Sections);

        var reloaded = (await MachineSettings.LoadAsync(store)).NgCarrierTransfer;
        Assert.Equal((120, 400), (reloaded.WaitingPosition!.X, reloaded.WaitingPosition.Y));
        var reloadedPickup = reloaded.CarrierPickupPosition!;
        Assert.Equal((160, 460), (reloadedPickup.X, reloadedPickup.Y));
    }

    [Theory]
    [InlineData("MainConveyorExitCarrierDetected")]
    [InlineData("80")]
    public void RemovedMainConveyorExitSensorDoesNotReturnFromSavedHardware(string exitInput)
    {
        Assert.DoesNotContain(InputIo.Unused80, new ConveyorHardwareSettings().Inputs.Keys);
        var hardware = JsonSerializer.Deserialize<ConveyorHardwareSettings>($$$"""
            {"Inputs":{"{{{exitInput}}}":68,"MainConveyorEntryCarrierDetected":156,"MainConveyorReadyFromRear":118}}
            """)!;
        Assert.Equal(2, hardware.Inputs.Count);
        Assert.Equal(156, hardware.Inputs[InputIo.MainConveyorEntryCarrierDetected]);
        Assert.Equal(118, hardware.Inputs[InputIo.MainConveyorReadyFromRear]);
        Assert.DoesNotContain(InputIo.Unused80, hardware.Inputs.Keys);
        Assert.DoesNotContain("\"80\":", JsonSerializer.Serialize(hardware));
    }

    [Theory]
    [InlineData("PcbPlacementIpmGripperClosed", "PcbPlacementIpmGripperOpen", "PcbPlacementIpmGripperClose")]
    [InlineData("7", "8", "3")]
    public void RemovedPlacementGripperDoesNotReturnFromSavedHardware(
        string closedInput, string openInput, string closeOutput)
    {
        var hardware = new PcbPlacementHandlerHardwareSettings();
        Assert.DoesNotContain(InputIo.Unused7, hardware.Inputs.Keys);
        Assert.DoesNotContain(InputIo.Unused8, hardware.Inputs.Keys);
        Assert.DoesNotContain(OutputIo.Unused3, hardware.Outputs.Keys);

        hardware = JsonSerializer.Deserialize<PcbPlacementHandlerHardwareSettings>($$$"""
            {"Inputs":{"{{{closedInput}}}":36,"{{{openInput}}}":37,"PcbPlacementIpmDown":134,"PcbPlacementIpmUp":135},
             "Outputs":{"{{{closeOutput}}}":{"Number":34,"OffNumber":35},
                        "PcbPlacementIpmDown":{"Number":132,"OffNumber":133}}
            }
            """)!;
        Assert.Equal(2, hardware.Inputs.Count);
        Assert.Equal(134, hardware.Inputs[InputIo.PcbPlacementIpmDown]);
        Assert.Equal(135, hardware.Inputs[InputIo.PcbPlacementIpmUp]);
        var press = Assert.Single(hardware.Outputs);
        Assert.Equal(OutputIo.PcbPlacementIpmDown, press.Key);
        Assert.Equal(132, press.Value.Number);
        Assert.Equal(133, press.Value.OffNumber);
        Assert.Equal(InputIo.PcbPlacementIpmDown, press.Value.Feedback!.OnInput);
        Assert.Equal(InputIo.PcbPlacementIpmUp, press.Value.Feedback.OffInput);
    }

    [Fact]
    public void OldHeadOutputNamesKeepAddressesAndUseDownFeedback()
    {
        var hardware = JsonSerializer.Deserialize<BoltFasteningHardwareSettings>("""
            {"Inputs":{"PickupHeadDown":142,"PickupHeadUp":143},"Outputs":{
                "PickupHeadUp":{"Number":139,"OffNumber":140,"Feedback":{"OnInput":"PickupHeadUp","OffInput":"PickupHeadDown"}},
                "ShootingHeadUp":{"Number":141,"OffNumber":142,"Feedback":{"OnInput":"ShootingHeadUp","OffInput":"ShootingHeadDown"}}
            }}
            """)!;
        foreach (var (output, down, up, channel, id) in new[]
        {
            (OutputIo.PickupHeadDown, InputIo.PickupHeadDown, InputIo.PickupHeadUp, 139, "12"),
            (OutputIo.ShootingHeadDown, InputIo.ShootingHeadDown, InputIo.ShootingHeadUp, 141, "13"),
        })
        {
            var head = hardware.Outputs[output];
            Assert.Equal(channel, head.Number);
            Assert.Equal(channel + 1, head.OffNumber);
            Assert.Equal(down, head.Feedback!.OnInput);
            Assert.Equal(up, head.Feedback.OffInput);
            Assert.Equal(id, JsonSerializer.Serialize(output));
        }
        Assert.Equal(142, hardware.Inputs[InputIo.PickupHeadDown]);
        Assert.Equal(4, hardware.Inputs.Count); // Only the two new table inputs are added.
        Assert.Equal(40, hardware.Inputs[InputIo.PickupTableDown]);
        Assert.Equal(41, hardware.Inputs[InputIo.PickupTableUp]);
        var table = hardware.Outputs[OutputIo.PickupTableDown];
        Assert.Equal(37, table.Number);
        Assert.Equal(38, table.OffNumber);
        table.Number = 137;
        table.OffNumber = 138;
        hardware.Inputs[InputIo.PickupTableDown] = 140;
        hardware.Inputs[InputIo.PickupTableUp] = 141;
        var reopened = JsonSerializer.Deserialize<BoltFasteningHardwareSettings>(
            JsonSerializer.Serialize(hardware))!;
        Assert.Equal(139, reopened.Outputs[OutputIo.PickupHeadDown].Number);
        Assert.Equal(InputIo.ShootingHeadDown,
            reopened.Outputs[OutputIo.ShootingHeadDown].Feedback!.OnInput);
        Assert.Equal(137, reopened.Outputs[OutputIo.PickupTableDown].Number);
        Assert.Equal(138, reopened.Outputs[OutputIo.PickupTableDown].OffNumber);
        Assert.Equal(140, reopened.Inputs[InputIo.PickupTableDown]);
        Assert.Equal(141, reopened.Inputs[InputIo.PickupTableUp]);
        Assert.Equal(InputIo.PickupTableDown, reopened.Outputs[OutputIo.PickupTableDown].Feedback!.OnInput);
        Assert.Equal(InputIo.PickupTableUp, reopened.Outputs[OutputIo.PickupTableDown].Feedback!.OffInput);
    }

    [Theory]
    [InlineData("NgCarrierPickupUp", "NgCarrierGripperOpen")]
    [InlineData("NgCarrierPickupDown", "NgCarrierGripperClose")]
    public async Task NamedIoSettingsLoadWithFixedIdsAndStationFeedback(string pickupName, string gripperName)
    {
        var store = new MachineStore(Path.Combine(CreateDirectory(), "Machine.db"));
        var json = $$$"""
            {"Outputs":{
                "{{{pickupName}}}":{"Number":101,"OffNumber":102,"Feedback":{"OnInput":"NgCarrierPickupUp","OffInput":"NgCarrierPickupDown"}},
                "{{{gripperName}}}":{"Number":103,"OffNumber":104,"Feedback":{"OnInput":"NgCarrierGripperOpen","OffInput":"NgCarrierGripperClosed"}}
            },"Inputs":{"NgCarrierPickupDown":91,"NgCarrierPickupUp":92,"NgCarrierGripperClosed":93,"NgCarrierGripperOpen":94,"NgCarrierDetected":95}}
            """;
        using var connection = new SqliteConnection($"Data Source={store.DatabaseFile}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Settings (Key, Value) VALUES ('NgCarrierTransferHardwareSettings', $value)";
        command.Parameters.AddWithValue("$value", json);
        command.ExecuteNonQuery();

        var loaded = await MachineSettings.LoadAsync(store);
        var hardware = loaded.NgCarrierTransferHardware;
        var pickup = hardware.Outputs[OutputIo.NgCarrierPickupDown];
        var gripper = hardware.Outputs[OutputIo.NgCarrierGripperClose];
        Assert.Equal(101, pickup.Number);
        Assert.Equal(102, pickup.OffNumber);
        Assert.Equal(103, gripper.Number);
        Assert.Equal(104, gripper.OffNumber);
        Assert.Equal(91, hardware.Inputs[InputIo.NgCarrierPickupDown]);
        Assert.Equal(92, hardware.Inputs[InputIo.NgCarrierPickupUp]);
        Assert.Equal(InputIo.NgCarrierPickupDown, pickup.Feedback!.OnInput);
        Assert.Equal(InputIo.NgCarrierPickupUp, pickup.Feedback.OffInput);
        Assert.Equal(InputIo.NgCarrierGripperClosed, gripper.Feedback!.OnInput);
        Assert.Equal(InputIo.NgCarrierGripperOpen, gripper.Feedback.OffInput);

        var io = new VirtualIoService(hardware.Outputs, new() { TimeoutMilliseconds = 100 })
        {
            AutoResponseEnabled = false,
        };
        var transfer = VirtualTest.CreateNgTransfer(io);
        io.SetInput(InputIo.NgCarrierPickupUp, true);
        io.SetInput(InputIo.NgCarrierPickupDown, false);
        var lowering = transfer.SetLiftUpAsync(false);
        Assert.True(io.GetOutput(OutputIo.NgCarrierPickupDown));
        Assert.False(lowering.IsCompleted);
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        await lowering;

        command.Parameters.Clear();
        command.CommandText = "SELECT Value FROM Settings WHERE Key = 'NgCarrierTransferHardwareSettings'";
        Assert.Equal(json, command.ExecuteScalar()); // Loading never rewrites the database.
        await store.SaveSettingsAsync(loaded.Sections);
        using var saved = JsonDocument.Parse((string)command.ExecuteScalar()!);
        var outputs = saved.RootElement.GetProperty("Outputs");
        Assert.Equal(101, outputs.GetProperty("20").GetProperty("Number").GetInt32());
        Assert.Equal(103, outputs.GetProperty("21").GetProperty("Number").GetInt32());
        Assert.False(outputs.GetProperty("20").TryGetProperty("Feedback", out _));
        var reopened = await MachineSettings.LoadAsync(new MachineStore(store.DatabaseFile));
        Assert.Equal(101, reopened.NgCarrierTransferHardware.Outputs[OutputIo.NgCarrierPickupDown].Number);
        Assert.Equal(InputIo.NgCarrierPickupDown,
            reopened.NgCarrierTransferHardware.Outputs[OutputIo.NgCarrierPickupDown].Feedback!.OnInput);
    }

    [Fact]
    public void SignalIdsRemainIndependentOfNamesAndRejectUnknownSignals()
    {
        Assert.Equal("20", JsonSerializer.Serialize(OutputIo.NgCarrierPickupDown));
        Assert.Equal("21", JsonSerializer.Serialize(OutputIo.NgCarrierGripperClose));
        Assert.Equal("23", JsonSerializer.Serialize(OutputIo.ShootingFeederOff));
        Assert.Equal(OutputIo.ShootingFeederOff, JsonSerializer.Deserialize<OutputIo>("\"ShootingFeederRunSignal\""));
        var feeder = JsonSerializer.Deserialize<BoltFeederHardwareSettings>("""
            {"Outputs":{"ShootingFeederRunSignal":{"Number":145}}}
            """)!;
        Assert.Equal(145, feeder.Outputs[OutputIo.ShootingFeederOff].Number);
        Assert.Equal(OutputIo.NgCarrierPickupDown, JsonSerializer.Deserialize<OutputIo>("20"));
        Assert.Equal(OutputIo.NgCarrierPickupDown, JsonSerializer.Deserialize<OutputIo>("\"NgCarrierPickupUp\""));
        Assert.Equal(OutputIo.NgCarrierPickupDown, JsonSerializer.Deserialize<OutputIo>("\"NgCarrierPickupDown\""));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OutputIo>("9999"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<NgCarrierTransferHardwareSettings>(
            """{"Outputs":{"UnknownOutput":{"Number":101}}}"""));
    }

    [Fact]
    public void OldConveyorNamesUseCurrentDefinitionsAndKeepEditedAddresses()
    {
        var hardware = JsonSerializer.Deserialize<ConveyorHardwareSettings>("""
            {"Inputs":{"MainConveyorAutoMode":153},"Outputs":{
                "PcbPlacementStopperDown":{"Number":125,"OffNumber":126,"Feedback":{"OnInput":"PcbPlacementStopperDown","OffInput":"PcbPlacementStopperUp"}},
                "PcbPlacementBackupPlateDown":{"Number":127,"OffNumber":128,"Feedback":{"OnInput":"PcbPlacementBackupPlateDown","OffInput":"PcbPlacementBackupPlateUp"}}
            }}
            """)!;
        Assert.Equal(153, hardware.Inputs[InputIo.MainConveyorManualMode]);
        Assert.Equal(3, hardware.Outputs.Count); // Only the new Normal Speed output is added.
        Assert.Equal(62, hardware.Outputs[OutputIo.MainConveyorNormalSpeed].Number);
        var stopper = hardware.Outputs[OutputIo.PcbPlacementStopperUp];
        Assert.Equal(125, stopper.Number);
        Assert.Equal(126, stopper.OffNumber);
        Assert.Equal(InputIo.PcbPlacementStopperUp, stopper.Feedback!.OnInput);
        Assert.Equal(InputIo.PcbPlacementStopperDown, stopper.Feedback.OffInput);
        var plate = hardware.Outputs[OutputIo.PcbPlacementBackupPlateUp];
        Assert.Equal(127, plate.Number);
        Assert.Equal(128, plate.OffNumber);
        Assert.Equal(InputIo.PcbPlacementBackupPlateUp, plate.Feedback!.OnInput);
        Assert.Equal(InputIo.PcbPlacementBackupPlateDown, plate.Feedback.OffInput);

        var ng = JsonSerializer.Deserialize<NgConveyorHardwareSettings>("""
            {"Outputs":{"NgConveyorRun":{"Number":172}}}
            """)!;
        Assert.Equal(2, ng.Outputs.Count);
        Assert.Equal(172, ng.Outputs[OutputIo.NgConveyorRun].Number);
        Assert.Equal(74, ng.Outputs[OutputIo.NgConveyorNormalSpeed].Number);
        hardware.Outputs[OutputIo.MainConveyorNormalSpeed].Number = 162;
        ng.Outputs[OutputIo.NgConveyorNormalSpeed].Number = 174;
        Assert.Equal(162, JsonSerializer.Deserialize<ConveyorHardwareSettings>(
            JsonSerializer.Serialize(hardware))!.Outputs[OutputIo.MainConveyorNormalSpeed].Number);
        Assert.Equal(174, JsonSerializer.Deserialize<NgConveyorHardwareSettings>(
            JsonSerializer.Serialize(ng))!.Outputs[OutputIo.NgConveyorNormalSpeed].Number);
    }

    [Fact]
    public void OpeningDatabaseDoesNotRewriteSavedJson()
    {
        var store = new MachineStore(Path.Combine(CreateDirectory(), "Machine.db"));
        var saved = new Dictionary<string, string>
        {
            ["BoltFasteningSettings"] = """{"SafeZ":5,"FasteningZ":12,"ShootingHead":{},"PickupHead":{}}""",
            ["ConveyorHardwareSettings"] = """{"Inputs":{"MainConveyorAutoMode":153},"Outputs":{"MainConveyorReverse":{"Number":60},"PcbPlacementStopperDown":{"Number":25,"Feedback":{"OnInput":"PcbPlacementStopperDown","OffInput":"PcbPlacementStopperUp"}}}}""",
            ["NgConveyorHardwareSettings"] = """{"Inputs":{},"Outputs":{"NgConveyorStopperUp":{"Number":77,"Feedback":null}}}""",
        };
        using var connection = new SqliteConnection($"Data Source={store.DatabaseFile}");
        connection.Open();
        using var command = connection.CreateCommand();
        foreach (var (key, value) in saved)
        {
            command.CommandText = "INSERT INTO Settings (Key, Value) VALUES ($key, $value)";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }

        var reopened = new MachineStore(store.DatabaseFile);
        var values = reopened.LoadSettings();
        Assert.Throws<JsonException>(() => values.Get<ConveyorHardwareSettings>());
        command.Parameters.Clear();
        command.CommandText = "SELECT Key, Value FROM Settings";
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            Assert.Equal(saved[reader.GetString(0)], reader.GetString(1));
            count++;
        }
        Assert.Equal(saved.Count, count);
    }

    [Fact]
    public async Task SavedSettingsAndRecipesSurviveRestartUnchanged()
    {
        var store = new MachineStore(Path.Combine(CreateDirectory(), "Machine.db"));
        Assert.False(store.HasData);
        var settings = new MachineSettings();
        settings.PcbSupplyHardware.Inputs[InputIo.PcbSupplyPcbDetected] = 28;
        settings.PcbSupplyHardware.Inputs[InputIo.PcbSupplyGripperClosed] = 22;
        settings.PcbSupplyHardware.Inputs[InputIo.PcbSupplyGripperOpen] = 23;
        settings.PcbSupplyHardware.Inputs[InputIo.PcbSupplyIpmFixerForward] = 24;
        settings.PcbSupplyHardware.Inputs[InputIo.PcbSupplyIpmFixerBackward] = 25;
        settings.PcbSupplyHardware.Outputs[OutputIo.PcbSupplyIpmFixerForward].OffNumber = 25;
        settings.ConveyorHardware.Inputs[InputIo.MainConveyorEntryCarrierDetected] = 91;
        settings.ConveyorHardware.Inputs[InputIo.MainConveyorReadyFromRear] = 92;
        settings.Conveyor.RearSmemaOffDelaySeconds = 0.8;
        settings.ConveyorHardware.Inputs[InputIo.PcbPlacementStopperUp] = 57;
        settings.ConveyorHardware.Inputs[InputIo.PcbPlacementStopperDown] = 58;
        settings.ConveyorHardware.Outputs[OutputIo.PcbPlacementStopperUp].Number = 96;
        settings.ConveyorHardware.Outputs[OutputIo.PcbPlacementStopperUp].OffNumber = 97;
        settings.NgConveyorHardware.Inputs.Remove(InputIo.NgConveyorStopperUp);
        settings.NgConveyorHardware.Inputs.Remove(InputIo.NgConveyorStopperDown);
        settings.BoltFasteningStationHardware.Inputs[InputIo.BoltFasteningHeatSink1Present] = 61;
        settings.BoltFasteningStationHardware.Inputs[InputIo.BoltFasteningHeatSink2Present] = 62;
        settings.InspectionStationHardware.Inputs[InputIo.InspectionHeatSink1Present] = 68;
        settings.InspectionStationHardware.Inputs[InputIo.InspectionHeatSink2Present] = 69;
        settings.PcbPlacementHandlerHardware.GetAxis(MotionAxis.Y)!.MoveUnit = 0.1;
        settings.PcbPlacementHandlerHardware.GetAxis(MotionAxis.Y)!.MovePulse = 10;
        settings.PcbPlacementHandlerHardware.GetAxis(MotionAxis.Y)!.HomeDirection = HomeDirection.Positive;
        var motion = settings.PcbPlacementHandler.Motion;
        motion.AccelerationSeconds = 0.3;
        motion.DecelerationSeconds = 0.7;
        motion.HorizontalHome.SearchSpeed = 8;
        motion.HorizontalHome.DetectionSpeed = 2.5;
        motion.HorizontalHome.ApproachSpeed = 0.8;
        motion.HorizontalHome.FineSpeed = 0.06;
        motion.HorizontalHome.SearchAccelerationSeconds = 0.4;
        motion.HorizontalHome.DetectionAccelerationSeconds = 0.25;
        motion.ZHome.SearchSpeed = 4;
        motion.ZHome.DetectionSpeed = 1.7;
        motion.ZHome.ApproachSpeed = 0.4;
        motion.ZHome.FineSpeed = 0.03;
        motion.ZHome.SearchAccelerationSeconds = 0.2;
        motion.ZHome.DetectionAccelerationSeconds = 0.5;
        settings.BoltFastening.SafeZ = 7;
        Assert.Equal(3, settings.BoltFastening.PickupRetryCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.BoltFastening.PickupRetryCount = -1);
        settings.BoltFastening.PickupRetryCount = 4;
        settings.BoltFastening.ShootingDetectionTimeoutMilliseconds = 1_750;
        settings.BoltFastening.ShootingArrivalDelaySeconds = 0.75;
        settings.BoltFastening.ShootingHead.FasteningZ = 14;
        settings.BoltFastening.PickupHead.FasteningZ = 18;
        settings.Lighting.StabilizationDelayMilliseconds = 375;
        settings.Drivers.Bolt = BoltDriver.Io;
        settings.IoBoltHardware.Outputs[OutputIo.ShootingBoltStart].Number = 115;
        await store.SaveSettingsAsync(settings.Sections);
        var recipe = new Recipe { Name = "Part", CarrierImages = [new() { Number = 1 }] };
        recipe.BoltInspection.LightLevel = 90;
        store.SaveRecipe(recipe, images: [new(1, [1, 2, 3])]);

        _ = new MachineStore(store.DatabaseFile);
        var reopened = new MachineStore(store.DatabaseFile);
        var loaded = await MachineSettings.LoadAsync(reopened);
        foreach (var (expected, actual) in settings.Sections.Zip(loaded.Sections))
        {
            Assert.Equal(
                JsonSerializer.Serialize(expected, expected.GetType()),
                JsonSerializer.Serialize(actual, actual.GetType()));
        }
        Assert.Equal(
            JsonSerializer.Serialize(recipe),
            JsonSerializer.Serialize(reopened.LoadRecipe("Part")));
        Assert.Equal(new byte[] { 1, 2, 3 }, reopened.LoadRecipeImage("Part", 1));
    }

    [Fact]
    public async Task FailedSettingsBatchDoesNotSavePartialChanges()
    {
        var store = new MachineStore(Path.Combine(CreateDirectory(), "Machine.db"));
        var settings = new MachineSettings();
        settings.PcbSupply.RotationZ = 12;
        settings.Conveyor.CarrierStopDelaySeconds = 45;
        settings.Conveyor.TransferTimeoutSeconds = 7;
        await store.SaveSettingsAsync(settings.Sections);

        using (var connection = new SqliteConnection($"Data Source={store.DatabaseFile}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER FailSetting BEFORE UPDATE ON Settings WHEN NEW.Key = 'PcbSupplySettings' BEGIN SELECT RAISE(ABORT, 'test failure'); END";
            command.ExecuteNonQuery();
            settings.Conveyor.CarrierStopDelaySeconds = 100;
            settings.Conveyor.TransferTimeoutSeconds = 9;
            settings.PcbSupply.RotationZ = 30;
            await Assert.ThrowsAsync<DbUpdateException>(() => store.SaveSettingsAsync(settings.Sections));
            command.CommandText = "DROP TRIGGER FailSetting";
            command.ExecuteNonQuery();
        }

        var loaded = await MachineSettings.LoadAsync(new MachineStore(store.DatabaseFile));
        Assert.Equal(45, loaded.Conveyor.CarrierStopDelaySeconds);
        Assert.Equal(7, loaded.Conveyor.TransferTimeoutSeconds);
        Assert.Equal(12, loaded.PcbSupply.RotationZ);
        await store.SaveSettingsAsync(settings.Sections);
        loaded = await MachineSettings.LoadAsync(new MachineStore(store.DatabaseFile));
        Assert.Equal(100, loaded.Conveyor.CarrierStopDelaySeconds);
        Assert.Equal(9, loaded.Conveyor.TransferTimeoutSeconds);
        Assert.Equal(30, loaded.PcbSupply.RotationZ);
    }

    private static string CreateDirectory()
    {
        return Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"IBTM-Storage-{Guid.NewGuid():N}"))
            .FullName;
    }
}
