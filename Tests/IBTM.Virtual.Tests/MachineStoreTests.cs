using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using IBTM.Device;
using IBTM.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class MachineStoreTests
{
    [Fact]
    public async Task SettingsAndRecipesAreWholeJsonObjectsInAnAutomaticallyCreatedDatabase()
    {
        var store = new MachineStore(Path.Combine(CreateDirectory(), "Machine.db"));
        Assert.False(store.HasData);
        var settings = new MachineSettings();
        settings.Conveyor.CarrierStopDelaySeconds = 0.75;
        settings.MachineHardware.Inputs[InputIo.AirPressureHigh] = 37;
        settings.ConveyorHardware.Outputs[OutputIo.MainConveyorForward].Number = 60;
        settings.ConveyorHardware.Outputs[OutputIo.PcbPlacementBackupPlateDown].Number = 25;
        settings.ConveyorHardware.Outputs[OutputIo.PcbPlacementBackupPlateDown].OffNumber = 26;
        settings.NgConveyorHardware.Outputs[OutputIo.NgConveyorStopperDown].Number = 77;
        settings.NgConveyorHardware.Outputs[OutputIo.NgConveyorStopperDown].OffNumber = 78;
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
                    '$.Outputs.NgConveyorStopperDown.Feedback',
                    json('{"OnInput":"NgConveyorStopperDown","OffInput":"NgConveyorStopperUp"}'))
                WHERE Key = 'NgConveyorHardwareSettings';
                UPDATE Settings SET Value = '{"RotationZ":17,"RemovedProperty":123}'
                WHERE Key = 'PcbSupplySettings';
                UPDATE Recipes SET Value = '{"Name":"Part","BoltInspection":{"LightLevel":90}}'
                WHERE Name = 'Part';
                """;
            command.ExecuteNonQuery();
            foreach (var (section, oldSignal, newSignal) in new[]
            {
                ("ConveyorHardwareSettings", "PcbPlacementBackupPlateUp", "PcbPlacementBackupPlateDown"),
                ("ConveyorHardwareSettings", "BoltFasteningBackupPlateUp", "BoltFasteningBackupPlateDown"),
                ("ConveyorHardwareSettings", "InspectionBackupPlateUp", "InspectionBackupPlateDown"),
                ("ConveyorHardwareSettings", "PcbPlacementStopperUp", "PcbPlacementStopperDown"),
                ("ConveyorHardwareSettings", "BoltFasteningStopperUp", "BoltFasteningStopperDown"),
                ("ConveyorHardwareSettings", "InspectionStopperUp", "InspectionStopperDown"),
                ("NgConveyorHardwareSettings", "NgConveyorStopperUp", "NgConveyorStopperDown"),
                ("NgCarrierTransferHardwareSettings", "NgCarrierPickupDown", "NgCarrierPickupUp"),
                ("NgCarrierTransferHardwareSettings", "NgCarrierGripperClose", "NgCarrierGripperOpen"),
                ("NgShuttleHardwareSettings", "NgShuttleDown", "NgShuttleUp"),
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
        // Opening again must not reverse the corrected input pairs.
        var loaded = await MachineSettings.LoadAsync(new MachineStore(reopened.DatabaseFile));
        Assert.Equal(0.75, loaded.Conveyor.CarrierStopDelaySeconds);
        Assert.Equal(37, loaded.MachineHardware.Inputs[InputIo.AirPressureHigh]);
        Assert.Equal(57, loaded.ConveyorHardware.Inputs[InputIo.PcbPlacementStopperDown]);
        Assert.Equal(58, loaded.ConveyorHardware.Inputs[InputIo.PcbPlacementStopperUp]);
        Assert.Equal(64, loaded.ConveyorHardware.Inputs[InputIo.BoltFasteningStopperDown]);
        Assert.Equal(65, loaded.ConveyorHardware.Inputs[InputIo.BoltFasteningStopperUp]);
        Assert.Equal(71, loaded.ConveyorHardware.Inputs[InputIo.InspectionStopperDown]);
        Assert.Equal(72, loaded.ConveyorHardware.Inputs[InputIo.InspectionStopperUp]);
        Assert.Equal(settings.NgConveyorHardware.Inputs.Count, loaded.NgConveyorHardware.Inputs.Count);
        Assert.Equal(60, loaded.ConveyorHardware.Outputs[OutputIo.MainConveyorForward].Number);
        foreach (var output in new[]
        {
            OutputIo.PcbPlacementBackupPlateDown,
            OutputIo.BoltFasteningBackupPlateDown,
            OutputIo.InspectionBackupPlateDown,
            OutputIo.PcbPlacementStopperDown,
            OutputIo.BoltFasteningStopperDown,
            OutputIo.InspectionStopperDown,
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
        var ngStopper = loaded.NgConveyorHardware.Outputs[OutputIo.NgConveyorStopperDown];
        Assert.Equal(77, ngStopper.Number);
        Assert.Equal(78, ngStopper.OffNumber);
        Assert.Equal(87, loaded.NgConveyorHardware.Inputs[InputIo.NgConveyorStopperDown]);
        Assert.Equal(88, loaded.NgConveyorHardware.Inputs[InputIo.NgConveyorStopperUp]);
        Assert.Equal(InputIo.NgConveyorStopperDown, ngStopper.Feedback!.OnInput);
        Assert.Equal(InputIo.NgConveyorStopperUp, ngStopper.Feedback.OffInput);
        foreach (var output in new[] { OutputIo.NgCarrierPickupUp, OutputIo.NgCarrierGripperOpen, OutputIo.NgShuttleUp })
        {
            var before = output == OutputIo.NgShuttleUp
                ? settings.NgShuttleHardware.Outputs[output]
                : settings.NgCarrierTransferHardware.Outputs[output];
            var after = output == OutputIo.NgShuttleUp
                ? loaded.NgShuttleHardware.Outputs[output]
                : loaded.NgCarrierTransferHardware.Outputs[output];
            Assert.Equal(before.Number, after.Number);
            Assert.Equal(before.OffNumber, after.OffNumber);
            Assert.Equal(before.Feedback!.OnInput, after.Feedback!.OnInput);
            Assert.Equal(before.Feedback.OffInput, after.Feedback.OffInput);
        }

        Assert.Equal(settings.ConveyorHardware.Outputs.Count, loaded.ConveyorHardware.Outputs.Count);
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
        hardware.Outputs[OutputIo.NgConveyorStopperDown].Feedback = null;
        hardware.Outputs[OutputIo.NgConveyorStopperDown].Number = 77;
        hardware.Outputs[OutputIo.NgConveyorStopperDown].OffNumber = 78;
        await settings.SaveAsync(store);

        var loaded = await MachineSettings.LoadAsync(new MachineStore(store.DatabaseFile));
        var restored = loaded.NgConveyorHardware;
        Assert.Equal(87, restored.Inputs[InputIo.NgConveyorStopperDown]);
        Assert.Equal(88, restored.Inputs[InputIo.NgConveyorStopperUp]);
        var stopper = restored.Outputs[OutputIo.NgConveyorStopperDown];
        Assert.Equal(77, stopper.Number);
        Assert.Equal(78, stopper.OffNumber);
        Assert.Equal(InputIo.NgConveyorStopperDown, stopper.Feedback!.OnInput);
        Assert.Equal(InputIo.NgConveyorStopperUp, stopper.Feedback.OffInput);
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
