using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbSupply;
using IBTM.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class MachineStoreTests
{
    [Fact]
    public async Task SettingsBatchAndDatabaseBackupRestorePreserveValues()
    {
        var directory = CreateDirectory();
        var store = new MachineStore(Path.Combine(directory, "Machine.db"));
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
        var restored = new MachineStore(store.DatabaseFile);
        Assert.Equal(12, (await MachineSettings.LoadAsync(restored)).PcbSupply.RotationZ);
        Assert.Equal(new byte[] { 1, 2, 3 }, restored.LoadRecipeImage("Test", 1));
        var previous = new MachineStore(store.DatabaseFile + ".previous");
        Assert.Equal(30, (await MachineSettings.LoadAsync(previous)).PcbSupply.RotationZ);
        Assert.False(File.Exists(store.DatabaseFile + ".restore"));
    }

    [Fact]
    public async Task LegacyImportIsAtomicPreservesOpticsAndNeverReimports()
    {
        var directory = CreateDirectory();
        var settingsPath = Directory.CreateDirectory(Path.Combine(directory, "Settings")).FullName;
        var carrierPath = Directory.CreateDirectory(Path.Combine(directory, "Recipes", "Part", "Carrier")).FullName;
        File.WriteAllText(Path.Combine(settingsPath, "PcbSupplySettings.json"), "{\"RotationZ\":17}");
        File.WriteAllText(Path.Combine(settingsPath, "InspectionCameraSettings.json"), "{\"DeviceId\":\"Cam1\",\"ExposureMicroseconds\":750,\"Gain\":2}");
        File.WriteAllText(Path.Combine(settingsPath, "LightingSettings.json"), "{\"InspectionChannel\":2,\"InspectionLevel\":90}");
        File.WriteAllText(Path.Combine(settingsPath, "InspectionGantrySettings.json"), "{\"CarrierScanOverlapMillimeters\":3}");
        File.WriteAllText(Path.Combine(settingsPath, "BoltInspectionSettings.json"), "{\"RegionSizePixels\":200,\"MaskThreshold\":0.7}");
        File.WriteAllText(Path.Combine(settingsPath, "RecipeSelectionSettings.json"), "{\"LastRecipeName\":\"Part\"}");
        File.WriteAllText(Path.Combine(directory, "Recipes", "Part", "Recipe.json"),
            "{\"Name\":\"Part\",\"BoltInspection\":{\"MinimumMaskRatio\":0.03},\"CarrierImages\":[{\"Number\":1,\"Center\":{\"X\":12,\"Y\":34}}]}");
        var store = new MachineStore(Path.Combine(directory, "Machine.db"));
        var import = typeof(Recipe).Assembly.GetType("IBTM.LegacyMachineImport")!
            .GetMethod("Run", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.IsType<FileNotFoundException>(Assert.Throws<TargetInvocationException>(() =>
            import.Invoke(null, [store, directory])).InnerException);
        Assert.False(store.HasData);
        var imagePath = Path.Combine(carrierPath, "0001.png");
        File.WriteAllBytes(imagePath, [10, 20, 30]);
        import.Invoke(null, [store, directory]);
        var settings = await MachineSettings.LoadAsync(store);
        var recipe = store.LoadRecipe<Recipe>("Part");
        Assert.Equal(17, settings.PcbSupply.RotationZ);
        Assert.Equal("Cam1", settings.InspectionCamera.DeviceId);
        Assert.Equal(750, recipe.BoltInspection.ExposureMicroseconds);
        Assert.Equal(2, recipe.BoltInspection.Gain);
        Assert.Equal(90, recipe.BoltInspection.LightLevel);
        Assert.Equal(3, recipe.BoltInspection.CarrierScanOverlapMillimeters);
        Assert.Equal(200, recipe.BoltInspection.RegionSizePixels);
        Assert.Equal(0.03, recipe.BoltInspection.MinimumMaskRatio);
        Assert.Equal(12, recipe.CarrierImages.Single().Center.X);
        Assert.Equal(File.ReadAllBytes(imagePath), store.LoadRecipeImage("Part", 1));
        File.WriteAllText(Path.Combine(settingsPath, "PcbSupplySettings.json"), "{\"RotationZ\":99}");
        import.Invoke(null, [store, directory]);
        Assert.Equal(17, (await MachineSettings.LoadAsync(store)).PcbSupply.RotationZ);
        Assert.True(File.Exists(imagePath));
    }

    private static string CreateDirectory() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"IBTM-Storage-{Guid.NewGuid():N}")).FullName;
}
