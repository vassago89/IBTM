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
        settings.MachineHardware.Inputs[InputIo.AirPressureHigh] = 37;
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
                UPDATE Settings SET Value = '{"RotationZ":17,"RemovedProperty":123}'
                WHERE Key = 'PcbSupplySettings';
                UPDATE Recipes SET Value = '{"Name":"Part","BoltInspection":{"LightLevel":90}}'
                WHERE Name = 'Part';
                """;
            command.ExecuteNonQuery();
            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name = '__EFMigrationsHistory'";
            Assert.Equal(0L, command.ExecuteScalar());
        }

        var reopened = new MachineStore(store.DatabaseFile);
        var loaded = await MachineSettings.LoadAsync(reopened);
        Assert.Equal(37, loaded.MachineHardware.Inputs[InputIo.AirPressureHigh]);
        Assert.Equal(17, loaded.PcbSupply.RotationZ);
        Assert.NotNull(loaded.PcbSupply.Motion);
        var recipe = reopened.LoadRecipe<Recipe>("Part");
        Assert.Equal(90, recipe.BoltInspection.LightLevel);
        Assert.Equal(
            new Recipe().BoltInspection.ExposureMicroseconds,
            recipe.BoltInspection.ExposureMicroseconds);
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
