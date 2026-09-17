using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.Device;
using IBTM.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class MachineStoreTests
{
    [Theory]
    [InlineData(null, null, 5d, 5d)]
    [InlineData(12d, null, 12d, 12d)]
    [InlineData(12d, 0d, 12d, 0d)]
    public void HeadFasteningHeightsMigrateOnceAndSaveIndependently(
        double? previousCommonZ,
        double? existingPickupZ,
        double expectedShootingZ,
        double expectedPickupZ)
    {
        var store = new MachineStore(Path.Combine(CreateDirectory(), "Machine.db"));
        var original = new BoltFasteningSettings { SafeZ = 5, PickupPosition = new() { Z = 10 } };
        store.SaveSettings([original]);
        var oldData = JsonSerializer.SerializeToNode(original)!;
        oldData["ShootingHead"]!.AsObject().Remove("FasteningZ");
        oldData["PickupHead"]!.AsObject().Remove("FasteningZ");
        if (previousCommonZ.HasValue)
            oldData["FasteningZ"] = previousCommonZ.Value;
        if (existingPickupZ.HasValue)
            oldData["PickupHead"]!["FasteningZ"] = existingPickupZ.Value;
        using (var connection = new SqliteConnection($"Data Source={store.DatabaseFile}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE Settings SET Value = $value WHERE Key = 'BoltFasteningSettings'";
            command.Parameters.AddWithValue("$value", oldData.ToJsonString());
            command.ExecuteNonQuery();
        }

        store = new MachineStore(store.DatabaseFile);
        var migrated = store.LoadSettings().Get<BoltFasteningSettings>();
        Assert.Equal(expectedShootingZ, migrated.ShootingHead.FasteningZ);
        Assert.Equal(expectedPickupZ, migrated.PickupHead.FasteningZ);
        Assert.Equal(10, migrated.PickupPosition.Z);
        migrated.SafeZ = 7;
        migrated.ShootingHead.FasteningZ = 14;
        migrated.PickupHead.FasteningZ = 18;
        store.SaveSettings([migrated]);
        var loaded = new MachineStore(store.DatabaseFile).LoadSettings().Get<BoltFasteningSettings>();
        Assert.Equal(7, loaded.SafeZ);
        Assert.Equal(14, loaded.ShootingHead.FasteningZ);
        Assert.Equal(18, loaded.PickupHead.FasteningZ);
        Assert.Equal(10, loaded.PickupPosition.Z);
    }

    [Fact]
    public async Task SavedIoAddressesSurviveRestartEvenWhenTheyMatchOldDefaults()
    {
        var store = new MachineStore(Path.Combine(CreateDirectory(), "Machine.db"));
        var settings = new MachineSettings();
        settings.PcbSupplyHardware.Inputs[InputIo.PcbSupplyPcbDetected] = 28;
        settings.PcbSupplyHardware.Inputs[InputIo.PcbSupplyGripperClosed] = 22;
        settings.PcbSupplyHardware.Inputs[InputIo.PcbSupplyGripperOpen] = 23;
        settings.PcbSupplyHardware.Inputs[InputIo.PcbSupplyIpmFixerForward] = 24;
        settings.PcbSupplyHardware.Inputs[InputIo.PcbSupplyIpmFixerBackward] = 25;
        settings.PcbSupplyHardware.Outputs[OutputIo.PcbSupplyIpmFixerForward].OffNumber = 25;
        settings.PcbSupplyHardware.Outputs[OutputIo.PcbSupplyIpmFixerForward].Feedback = new(
            InputIo.PcbSupplyIpmFixerForward, InputIo.PcbSupplyIpmFixerBackward);
        settings.ConveyorHardware.Inputs[InputIo.MainConveyorEntryCarrierDetected] = 91;
        settings.ConveyorHardware.Inputs[InputIo.MainConveyorExitCarrierDetected] = 92;
        settings.ConveyorHardware.Inputs[InputIo.PcbPlacementStopperUp] = 57;
        settings.ConveyorHardware.Inputs[InputIo.PcbPlacementStopperDown] = 58;
        settings.NgConveyorHardware.Inputs[InputIo.NgConveyorStopperUp] = 87;
        settings.NgConveyorHardware.Inputs[InputIo.NgConveyorStopperDown] = 88;
        settings.BoltFasteningStationHardware.Inputs[InputIo.BoltFasteningHeatSink1Present] = 61;
        settings.BoltFasteningStationHardware.Inputs[InputIo.BoltFasteningHeatSink2Present] = 62;
        settings.InspectionStationHardware.Inputs[InputIo.InspectionHeatSink1Present] = 68;
        settings.InspectionStationHardware.Inputs[InputIo.InspectionHeatSink2Present] = 69;
        await settings.SaveAsync(store);

        // Reopening the database must not reinterpret an operator's saved channel numbers.
        _ = new MachineStore(store.DatabaseFile);
        var loaded = await MachineSettings.LoadAsync(new MachineStore(store.DatabaseFile));
        // The removed IPM backward sensor/output is still retired, independent of addresses.
        settings.PcbSupplyHardware.Outputs[OutputIo.PcbSupplyIpmFixerForward].OffNumber = null;
        settings.PcbSupplyHardware.Outputs[OutputIo.PcbSupplyIpmFixerForward].Feedback = new(
            InputIo.PcbSupplyIpmFixerForward);
        settings.PcbSupplyHardware.Inputs.Remove(InputIo.PcbSupplyIpmFixerBackward);
        foreach (var (expectedSection, actualSection) in settings.HardwareSections.Zip(loaded.HardwareSections))
        {
            Assert.Equal(
                JsonSerializer.Serialize(expectedSection, expectedSection.GetType()),
                JsonSerializer.Serialize(actualSection, actualSection.GetType()));
        }
    }

    [Fact]
    public async Task SettingsAndRecipesAreWholeJsonObjectsInAnAutomaticallyCreatedDatabase()
    {
        var store = new MachineStore(Path.Combine(CreateDirectory(), "Machine.db"));
        Assert.False(store.HasData);
        var settings = new MachineSettings();
        settings.PcbPlacementHandlerHardware.MillimetersPerUnit = 0.002;
        settings.PcbPlacementHandlerHardware.GetAxis(MotionAxis.Y)!.MoveUnit = 0.1;
        settings.PcbPlacementHandlerHardware.GetAxis(MotionAxis.Y)!.MovePulse = 10;
        settings.PcbPlacementHandlerHardware.GetAxis(MotionAxis.X)!.HomeDirection = HomeDirection.Negative;
        settings.PcbPlacementHandlerHardware.GetAxis(MotionAxis.Y)!.HomeDirection = HomeDirection.Positive;
        settings.Conveyor.CarrierStopDelaySeconds = 0.75;
        settings.ConveyorHardware.Inputs[InputIo.MainConveyorManualMode] = 153;
        settings.NgConveyorHardware.Inputs[InputIo.NgConveyorManualMode] = 183;
        settings.MachineHardware.Inputs[InputIo.AirPressureHigh] = 37;
        settings.ConveyorHardware.Outputs[OutputIo.MainConveyorForward].Number = 60;
        settings.ConveyorHardware.Outputs[OutputIo.PcbPlacementBackupPlateUp].Number = 25;
        settings.ConveyorHardware.Outputs[OutputIo.PcbPlacementBackupPlateUp].OffNumber = 26;
        settings.NgConveyorHardware.Outputs[OutputIo.NgConveyorStopperUp].Number = 77;
        settings.NgConveyorHardware.Outputs[OutputIo.NgConveyorStopperUp].OffNumber = 78;
        settings.BoltFasteningHardware.Outputs[OutputIo.PickupHeadUp].Number = 139;
        settings.BoltFasteningHardware.Outputs[OutputIo.PickupHeadUp].OffNumber = 140;
        settings.Drivers.Bolt = BoltDriver.Io;
        settings.IoBoltHardware.Inputs[InputIo.PickupBoltReady] = 112;
        settings.IoBoltHardware.Outputs[OutputIo.ShootingBoltStart].Number = 115;
        await settings.SaveAsync(store);
        store.SaveRecipe("Part", new Recipe { Name = "Part" }, []);

        using (var connection = new SqliteConnection($"Data Source={store.DatabaseFile}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM Settings";
            Assert.Equal((long)settings.Sections.Length, (long)command.ExecuteScalar()!);
            command.CommandText = "SELECT Value FROM Settings WHERE Key = 'MachineHardwareSettings'";
            using var json = JsonDocument.Parse((string)command.ExecuteScalar()!);
            Assert.Equal(
                37,
                json.RootElement.GetProperty("Inputs").GetProperty("AirPressureHigh").GetInt32());
            Assert.Equal(
                4,
                json.RootElement.GetProperty("Outputs").GetProperty("MachineLight")
                    .GetProperty("Number").GetInt32());

            command.CommandText = """
                UPDATE Settings
                SET Value = json_remove(
                    json_set(Value, '$.Outputs.MainConveyorReverse', json_extract(Value, '$.Outputs.MainConveyorForward'),
                        '$.Outputs.MainConveyorNormalSpeed', json('{"Number":62}'),
                        '$.Inputs.PcbPlacementStopperUp', 57, '$.Inputs.PcbPlacementStopperDown', 58,
                        '$.Inputs.BoltFasteningStopperUp', 64, '$.Inputs.BoltFasteningStopperDown', 65,
                        '$.Inputs.InspectionStopperUp', 71, '$.Inputs.InspectionStopperDown', 72),
                    '$.Outputs.MainConveyorForward')
                WHERE Key = 'ConveyorHardwareSettings';
                UPDATE Settings
                SET Value = json_set(Value, '$.Outputs.NgConveyorNormalSpeed', json('{"Number":74}'),
                    '$.Inputs.NgConveyorStopperUp', 87, '$.Inputs.NgConveyorStopperDown', 88,
                    '$.Outputs.NgConveyorStopperUp.Feedback',
                    json('{"OnInput":"NgConveyorStopperUp","OffInput":"NgConveyorStopperDown"}'))
                WHERE Key = 'NgConveyorHardwareSettings';
                UPDATE Settings SET Value = '{"RotationZ":17,"RemovedProperty":123}'
                WHERE Key = 'PcbSupplySettings';
                UPDATE Recipes SET Value = '{"Name":"Part","BoltInspection":{"LightLevel":90}}'
                WHERE Name = 'Part';
                """;
            command.ExecuteNonQuery();
            foreach (var (section, oldSignal, newSignal) in new[]
            {
                ("ConveyorHardwareSettings", "MainConveyorAutoMode", "MainConveyorManualMode"),
                ("NgConveyorHardwareSettings", "NgConveyorAutoMode", "NgConveyorManualMode"),
            })
            {
                command.CommandText = """
                    UPDATE Settings
                    SET Value = json_remove(json_set(Value, $old, json_extract(Value, $new)), $new)
                    WHERE Key = $section;
                    """;
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$old", $"$.Inputs.{oldSignal}");
                command.Parameters.AddWithValue("$new", $"$.Inputs.{newSignal}");
                command.Parameters.AddWithValue("$section", section);
                command.ExecuteNonQuery();
            }
            foreach (var (section, oldSignal, newSignal) in new[]
            {
                ("ConveyorHardwareSettings", "PcbPlacementBackupPlateDown", "PcbPlacementBackupPlateUp"),
                ("ConveyorHardwareSettings", "BoltFasteningBackupPlateDown", "BoltFasteningBackupPlateUp"),
                ("ConveyorHardwareSettings", "InspectionBackupPlateDown", "InspectionBackupPlateUp"),
                ("ConveyorHardwareSettings", "PcbPlacementStopperDown", "PcbPlacementStopperUp"),
                ("ConveyorHardwareSettings", "BoltFasteningStopperDown", "BoltFasteningStopperUp"),
                ("ConveyorHardwareSettings", "InspectionStopperDown", "InspectionStopperUp"),
                ("NgConveyorHardwareSettings", "NgConveyorStopperDown", "NgConveyorStopperUp"),
                ("NgCarrierTransferHardwareSettings", "NgCarrierPickupDown", "NgCarrierPickupUp"),
                ("NgCarrierTransferHardwareSettings", "NgCarrierGripperClose", "NgCarrierGripperOpen"),
                ("NgShuttleHardwareSettings", "NgShuttleUp", "NgShuttleDown"),
                ("BoltFasteningHardwareSettings", "PickupHeadDown", "PickupHeadUp"),
                ("BoltFasteningHardwareSettings", "ShootingHeadDown", "ShootingHeadUp"),
            })
            {
                command.CommandText = """
                    UPDATE Settings
                    SET Value = json_remove(
                        json_set(Value, $old, json_set(json_extract(Value, $new),
                            '$.Feedback.OnInput', json_extract(Value, $new || '.Feedback.OffInput'),
                            '$.Feedback.OffInput', json_extract(Value, $new || '.Feedback.OnInput'))),
                        $new)
                    WHERE Key = $section;
                    """;
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$old", $"$.Outputs.{oldSignal}");
                command.Parameters.AddWithValue("$new", $"$.Outputs.{newSignal}");
                command.Parameters.AddWithValue("$section", section);
                command.ExecuteNonQuery();
            }

            command.Parameters.Clear();
            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name = '__EFMigrationsHistory'";
            Assert.Equal(0L, command.ExecuteScalar());
        }

        var reopened = new MachineStore(store.DatabaseFile);
        // Key migrations must preserve the saved input pairs on every reopen.
        var loaded = await MachineSettings.LoadAsync(new MachineStore(reopened.DatabaseFile));
        Assert.Equal(BoltDriver.Io, loaded.Drivers.Bolt);
        Assert.Equal(112, loaded.IoBoltHardware.Inputs[InputIo.PickupBoltReady]);
        Assert.Equal(115, loaded.IoBoltHardware.Outputs[OutputIo.ShootingBoltStart].Number);
        Assert.Equal(0.002, loaded.PcbPlacementHandlerHardware.MillimetersPerUnit);
        Assert.Equal(0.1, loaded.PcbPlacementHandlerHardware.GetAxis(MotionAxis.Y)!.MoveUnit);
        Assert.Equal(10, loaded.PcbPlacementHandlerHardware.GetAxis(MotionAxis.Y)!.MovePulse);
        Assert.Equal(1, loaded.PcbPlacementHandlerHardware.GetAxis(MotionAxis.X)!.MoveUnit);
        Assert.Equal(1, loaded.PcbPlacementHandlerHardware.GetAxis(MotionAxis.X)!.MovePulse);
        Assert.Equal(HomeDirection.Negative, loaded.PcbPlacementHandlerHardware.GetAxis(MotionAxis.X)!.HomeDirection);
        Assert.Equal(HomeDirection.Positive, loaded.PcbPlacementHandlerHardware.GetAxis(MotionAxis.Y)!.HomeDirection);
        Assert.Equal(HomeDirection.Negative, loaded.PcbPlacementHandlerHardware.GetAxis(MotionAxis.Z)!.HomeDirection);
        Assert.Equal(0.75, loaded.Conveyor.CarrierStopDelaySeconds);
        Assert.Equal(153, loaded.ConveyorHardware.Inputs[InputIo.MainConveyorManualMode]);
        Assert.Equal(183, loaded.NgConveyorHardware.Inputs[InputIo.NgConveyorManualMode]);
        Assert.Equal(37, loaded.MachineHardware.Inputs[InputIo.AirPressureHigh]);
        Assert.Equal(58, loaded.ConveyorHardware.Inputs[InputIo.PcbPlacementStopperDown]);
        Assert.Equal(57, loaded.ConveyorHardware.Inputs[InputIo.PcbPlacementStopperUp]);
        Assert.Equal(65, loaded.ConveyorHardware.Inputs[InputIo.BoltFasteningStopperDown]);
        Assert.Equal(64, loaded.ConveyorHardware.Inputs[InputIo.BoltFasteningStopperUp]);
        Assert.Equal(72, loaded.ConveyorHardware.Inputs[InputIo.InspectionStopperDown]);
        Assert.Equal(71, loaded.ConveyorHardware.Inputs[InputIo.InspectionStopperUp]);
        Assert.Equal(settings.NgConveyorHardware.Inputs.Count, loaded.NgConveyorHardware.Inputs.Count);
        Assert.Equal(60, loaded.ConveyorHardware.Outputs[OutputIo.MainConveyorForward].Number);
        foreach (var output in new[]
        {
            OutputIo.PcbPlacementBackupPlateUp,
            OutputIo.BoltFasteningBackupPlateUp,
            OutputIo.InspectionBackupPlateUp,
            OutputIo.PcbPlacementStopperUp,
            OutputIo.BoltFasteningStopperUp,
            OutputIo.InspectionStopperUp,
        })
        {
            var expected = settings.ConveyorHardware.Outputs[output];
            var actual = loaded.ConveyorHardware.Outputs[output];
            Assert.Equal(expected.Number, actual.Number);
            Assert.Equal(expected.OffNumber, actual.OffNumber);
            Assert.Equal(expected.Feedback!.OnInput, actual.Feedback!.OnInput);
            Assert.Equal(expected.Feedback.OffInput, actual.Feedback.OffInput);
        }

        Assert.Equal(73, loaded.NgConveyorHardware.Outputs[OutputIo.NgConveyorReverse].Number);
        var ngStopper = loaded.NgConveyorHardware.Outputs[OutputIo.NgConveyorStopperUp];
        Assert.Equal(77, ngStopper.Number);
        Assert.Equal(78, ngStopper.OffNumber);
        Assert.Equal(88, loaded.NgConveyorHardware.Inputs[InputIo.NgConveyorStopperDown]);
        Assert.Equal(87, loaded.NgConveyorHardware.Inputs[InputIo.NgConveyorStopperUp]);
        Assert.Equal(InputIo.NgConveyorStopperUp, ngStopper.Feedback!.OnInput);
        Assert.Equal(InputIo.NgConveyorStopperDown, ngStopper.Feedback.OffInput);
        foreach (var output in new[] { OutputIo.NgCarrierPickupUp, OutputIo.NgCarrierGripperOpen, OutputIo.NgShuttleDown })
        {
            var before = output == OutputIo.NgShuttleDown
                ? settings.NgShuttleHardware.Outputs[output]
                : settings.NgCarrierTransferHardware.Outputs[output];
            var after = output == OutputIo.NgShuttleDown
                ? loaded.NgShuttleHardware.Outputs[output]
                : loaded.NgCarrierTransferHardware.Outputs[output];
            Assert.Equal(before.Number, after.Number);
            Assert.Equal(before.OffNumber, after.OffNumber);
            Assert.Equal(before.Feedback!.OnInput, after.Feedback!.OnInput);
            Assert.Equal(before.Feedback.OffInput, after.Feedback.OffInput);
        }

        Assert.Equal(settings.ConveyorHardware.Outputs.Count, loaded.ConveyorHardware.Outputs.Count);
        foreach (var output in new[] { OutputIo.PickupHeadUp, OutputIo.ShootingHeadUp })
        {
            var expected = settings.BoltFasteningHardware.Outputs[output];
            var actual = loaded.BoltFasteningHardware.Outputs[output];
            Assert.Equal(expected.Number, actual.Number);
            Assert.Equal(expected.OffNumber, actual.OffNumber);
            Assert.Equal(expected.Feedback!.OnInput, actual.Feedback!.OnInput);
            Assert.Equal(expected.Feedback.OffInput, actual.Feedback.OffInput);
        }

        Assert.Equal(settings.NgConveyorHardware.Outputs.Count, loaded.NgConveyorHardware.Outputs.Count);
        Assert.Equal(17, loaded.PcbSupply.RotationZ);
        Assert.NotNull(loaded.PcbSupply.Motion);
        var recipe = reopened.LoadRecipe<Recipe>("Part");
        Assert.Equal(90, recipe.BoltInspection.LightLevel);
        Assert.Equal(
            new Recipe().BoltInspection.ExposureMicroseconds,
            recipe.BoltInspection.ExposureMicroseconds);
    }

    [Fact]
    public async Task NgStopperMappingsReturnAfterSensorlessSettingsWereSaved()
    {
        var store = new MachineStore(Path.Combine(CreateDirectory(), "Machine.db"));
        var settings = new MachineSettings();
        var hardware = settings.NgConveyorHardware;
        hardware.Inputs.Remove(InputIo.NgConveyorStopperDown);
        hardware.Inputs.Remove(InputIo.NgConveyorStopperUp);
        hardware.Outputs[OutputIo.NgConveyorStopperUp].Feedback = null;
        hardware.Outputs[OutputIo.NgConveyorStopperUp].Number = 77;
        hardware.Outputs[OutputIo.NgConveyorStopperUp].OffNumber = 78;
        await settings.SaveAsync(store);

        var loaded = await MachineSettings.LoadAsync(new MachineStore(store.DatabaseFile));
        var restored = loaded.NgConveyorHardware;
        Assert.Equal(87, restored.Inputs[InputIo.NgConveyorStopperDown]);
        Assert.Equal(88, restored.Inputs[InputIo.NgConveyorStopperUp]);
        var stopper = restored.Outputs[OutputIo.NgConveyorStopperUp];
        Assert.Equal(77, stopper.Number);
        Assert.Equal(78, stopper.OffNumber);
        Assert.Equal(InputIo.NgConveyorStopperUp, stopper.Feedback!.OnInput);
        Assert.Equal(InputIo.NgConveyorStopperDown, stopper.Feedback.OffInput);
    }

    [Fact]
    public async Task SettingsBatchAndDatabaseBackupRestorePreserveValues()
    {
        var directory = CreateDirectory();
        var store = VirtualTest.OpenMachineStore(Path.Combine(directory, "Machine.db"));
        var settings = new MachineSettings();
        settings.PcbSupply.RotationZ = 12;
        settings.PcbSupplyHardware.Axes[MachineAxis.PcbSupplyY].Number = 27;
        settings.PcbBuffer.SupplyBoundary1 = 45;
        await settings.SaveAsync(store);
        store.SaveRecipe("Test", new Recipe { Name = "Test" }, [1], images: [new(1, [1, 2, 3])]);
        var loaded = await MachineSettings.LoadAsync(store);
        Assert.Equal(12, loaded.PcbSupply.RotationZ);
        Assert.Equal(27, loaded.PcbSupplyHardware.Axes[MachineAxis.PcbSupplyY].Number);

        using (var connection = new SqliteConnection($"Data Source={store.DatabaseFile}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER FailSetting BEFORE UPDATE ON Settings WHEN NEW.Key = 'PcbSupplySettings' BEGIN SELECT RAISE(ABORT, 'test failure'); END";
            command.ExecuteNonQuery();
            settings.PcbBuffer.SupplyBoundary1 = 100;
            settings.PcbSupply.RotationZ = 30;
            await Assert.ThrowsAsync<DbUpdateException>(() => settings.SaveAsync(store));
            command.CommandText = "DROP TRIGGER FailSetting";
            command.ExecuteNonQuery();
        }

        loaded = await MachineSettings.LoadAsync(store);
        Assert.Equal(45, loaded.PcbBuffer.SupplyBoundary1);
        Assert.Equal(12, loaded.PcbSupply.RotationZ);

        var backup = Path.Combine(directory, "Backup.db");
        store.Backup(backup);
        await settings.SaveAsync(store);
        // A failed copy can leave a created but empty restore destination.
        File.WriteAllBytes(store.DatabaseFile + ".restore", []);
        Assert.Throws<InvalidDataException>(() => MachineStore.RestorePending(store.DatabaseFile));
        Assert.Equal(30, (await MachineSettings.LoadAsync(store)).PcbSupply.RotationZ);
        Assert.False(File.Exists(store.DatabaseFile + ".previous"));

        store.PrepareRestore(backup);
        Assert.Equal(30, (await MachineSettings.LoadAsync(store)).PcbSupply.RotationZ);
        MachineStore.RestorePending(store.DatabaseFile);
        var restored = VirtualTest.OpenMachineStore(store.DatabaseFile);
        Assert.Equal(12, (await MachineSettings.LoadAsync(restored)).PcbSupply.RotationZ);
        Assert.Equal(new byte[] { 1, 2, 3 }, restored.LoadRecipeImage("Test", 1));
        var previous = VirtualTest.OpenMachineStore(store.DatabaseFile + ".previous");
        Assert.Equal(30, (await MachineSettings.LoadAsync(previous)).PcbSupply.RotationZ);
        Assert.False(File.Exists(store.DatabaseFile + ".restore"));
    }

    private static string CreateDirectory()
    {
        return Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"IBTM-Storage-{Guid.NewGuid():N}"))
            .FullName;
    }
}
