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
    [Theory]
    [InlineData("\"Physical\"", LightDriver.Movs)]
    [InlineData("0", LightDriver.Virtual)]
    public async Task LightingMigrationPreservesOldSelectionAndComThenSavesIndependently(
        string oldControl, LightDriver expected)
    {
        var file = Path.Combine(CreateDirectory(), "Machine.db");
        var store = VirtualTest.OpenMachineStore(file);
        await new MachineSettings().SaveAsync(store);
        using (var connection = new SqliteConnection($"Data Source={file}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE Settings SET Value = json_set(json_remove(Value, '$.Light'), '$.Control', json($control))
                WHERE Key = 'DriverSettings';
                UPDATE Settings SET Value = '{"Connection":"COM8","InspectionChannel":3}'
                WHERE Key = 'LightingSettings';
                DELETE FROM __EFMigrationsHistory WHERE MigrationId = '20260909170000_IndependentLightDriver';
                """;
            command.Parameters.AddWithValue("$control", oldControl);
            command.ExecuteNonQuery();
        }
        var reopened = VirtualTest.OpenMachineStore(file);
        var loaded = await MachineSettings.LoadAsync(reopened);
        Assert.Equal(expected, loaded.Drivers.Light);
        Assert.Equal("COM8", loaded.Lighting.Connection);
        Assert.Equal(3, loaded.Lighting.InspectionChannel);
        Assert.Equal(19200, loaded.Lighting.BaudRate);
        Assert.Equal(8, loaded.Lighting.DataBits);
        Assert.Equal(System.IO.Ports.Parity.None, loaded.Lighting.Parity);
        Assert.Equal(System.IO.Ports.StopBits.One, loaded.Lighting.StopBits);
        loaded.Drivers.Control = ControlDriver.Physical;
        loaded.Drivers.Light = LightDriver.Virtual;
        loaded.Lighting.BaudRate = 9600;
        loaded.Lighting.DataBits = 7;
        loaded.Lighting.Parity = System.IO.Ports.Parity.Even;
        loaded.Lighting.StopBits = System.IO.Ports.StopBits.Two;
        loaded.Lighting.WriteTimeoutMilliseconds = 750;
        await loaded.SaveAsync(reopened);
        var again = await MachineSettings.LoadAsync(VirtualTest.OpenMachineStore(file));
        Assert.Equal(ControlDriver.Physical, again.Drivers.Control);
        Assert.Equal(LightDriver.Virtual, again.Drivers.Light);
        Assert.Equal("COM8", again.Lighting.Connection);
        Assert.Equal(9600, again.Lighting.BaudRate);
        Assert.Equal(7, again.Lighting.DataBits);
        Assert.Equal(System.IO.Ports.Parity.Even, again.Lighting.Parity);
        Assert.Equal(System.IO.Ports.StopBits.Two, again.Lighting.StopBits);
        Assert.Equal(750, again.Lighting.WriteTimeoutMilliseconds);
    }

    [Fact]
    public async Task MotionSettingsConvertLegacyRatiosOnceAndRemainIndependent()
    {
        var file = Path.Combine(CreateDirectory(), "Machine.db");
        var store = VirtualTest.OpenMachineStore(file);
        Assert.False(store.HasData);
        var settings = new MachineSettings();
        settings.PcbSupply.RotationZ = 17;
        settings.PcbSupplyHardware.MillimetersPerPulse = 0.005;
        await settings.SaveAsync(store);
        using (var connection = new SqliteConnection($"Data Source={file}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Settings (Key, Value) VALUES ('HomeSettings', '{"HorizontalSpeed":40,"ZSpeed":20}');
                UPDATE Settings SET Value = json_set(Value, '$.AccelerationMultiplier', 4.0,
                    '$.HomeSecondVelocityRatio', 0.25, '$.HomeThirdVelocityRatio', 0.125,
                    '$.HomeLastVelocityRatio', 0.05, '$.HomeSecondAccelerationRatio', 0.5)
                WHERE Key = 'AjinSettings';
                UPDATE Settings SET Value = json_remove(Value, '$.Motion.AccelerationSeconds',
                    '$.Motion.DecelerationSeconds', '$.Motion.HorizontalHome', '$.Motion.ZHome')
                WHERE Key IN ('PcbSupplySettings', 'PcbPlacementHandlerSettings', 'BoltFasteningSettings', 'InspectionGantrySettings');
                DELETE FROM __EFMigrationsHistory WHERE MigrationId = '20260909160000_MotionSettingsUnits';
                """;
            command.ExecuteNonQuery();
        }
        var reopened = VirtualTest.OpenMachineStore(file);
        var loaded = await MachineSettings.LoadAsync(reopened);
        foreach (var motion in new[] { loaded.PcbSupply.Motion, loaded.PcbPlacementHandler.Motion,
                     loaded.BoltFastening.Motion, loaded.InspectionGantry.Motion })
        {
            Assert.Equal(0.25, motion.AccelerationSeconds);
            Assert.Equal(0.25, motion.DecelerationSeconds);
            Assert.Equal((40, 10, 5, 2), (motion.HorizontalHome.SearchSpeed, motion.HorizontalHome.DetectionSpeed,
                motion.HorizontalHome.ApproachSpeed, motion.HorizontalHome.FineSpeed));
            Assert.Equal((20, 5, 2.5, 1), (motion.ZHome.SearchSpeed, motion.ZHome.DetectionSpeed,
                motion.ZHome.ApproachSpeed, motion.ZHome.FineSpeed));
            Assert.Equal(1, motion.HorizontalHome.SearchAccelerationSeconds);
            Assert.Equal(0.5, motion.HorizontalHome.DetectionAccelerationSeconds);
        }
        Assert.Equal(17, loaded.PcbSupply.RotationZ);
        Assert.Equal(0.005, loaded.PcbSupplyHardware.MillimetersPerPulse);
        loaded.PcbSupply.Motion.HorizontalHome.DetectionSpeed = 7;
        await loaded.SaveAsync(reopened);
        var again = await MachineSettings.LoadAsync(VirtualTest.OpenMachineStore(file));
        Assert.Equal(7, again.PcbSupply.Motion.HorizontalHome.DetectionSpeed);
        Assert.Equal(10, again.PcbPlacementHandler.Motion.HorizontalHome.DetectionSpeed);
    }

    [Fact]
    public async Task ConfirmedIoMapUpdatesSavedWiringOnceWithoutChangingTeachingOrRecipes()
    {
        var file = Path.Combine(CreateDirectory(), "Machine.db");
        var store = VirtualTest.OpenMachineStore(file);
        var settings = new MachineSettings();
        settings.PcbSupply.RotationZ = 17;
        settings.PcbSupplyHardware.Axes[MachineAxis.PcbSupplyY].Maximum = 350;
        settings.PcbSupplyHardware.MillimetersPerPulse = 0.005;
        await settings.SaveAsync(store);
        store.SaveRecipe("Part", new Recipe { Name = "Part" }, []);

        using (var connection = new SqliteConnection($"Data Source={file}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE Settings SET Value = json_set(
                    replace(replace(Value, 'PcbSupplyGripperClosed', 'PcbSupplyNestForward'),
                        'PcbSupplyGripperOpen', 'PcbSupplyNestBackward'),
                    '$.Inputs.PcbSupplyRotated', 22, '$.Inputs.PcbSupplyUnrotated', 23,
                    '$.Inputs.PcbSupplyNestForward', 20, '$.Inputs.PcbSupplyNestBackward', 21,
                    '$.Inputs.PcbSupplyIpmFixerForward', 26, '$.Inputs.PcbSupplyIpmFixerBackward', 27,
                    '$.Outputs.PcbSupplyRotate.Number', 22, '$.Outputs.PcbSupplyRotate.OffNumber', 23,
                    '$.Outputs.PcbSupplyNestForward.Number', 20, '$.Outputs.PcbSupplyNestForward.OffNumber', 21,
                    '$.Outputs.PcbSupplyIpmFixerForward.Number', 26, '$.Outputs.PcbSupplyIpmFixerForward.OffNumber', 27)
                WHERE Key = 'PcbSupplyHardwareSettings';
                UPDATE Settings SET Value = json_set(json_remove(Value, '$.Inputs.MainConveyorAutoMode'),
                    '$.Inputs.MainConveyorEntryCarrierDetected', 90,
                    '$.Inputs.MainConveyorExitCarrierDetected', 91)
                WHERE Key = 'ConveyorHardwareSettings';
                UPDATE Settings SET Value = json_set(Value,
                    '$.Outputs.NgCarrierPickupDown.Number', 63, '$.Outputs.NgCarrierPickupDown.OffNumber', 64,
                    '$.Outputs.NgCarrierGripperClose.Number', 65, '$.Outputs.NgCarrierGripperClose.OffNumber', 66)
                WHERE Key = 'NgCarrierTransferHardwareSettings';
                UPDATE Settings SET Value = json_set(Value,
                    '$.Outputs.NgShuttleDown.Number', 67, '$.Outputs.NgShuttleDown.OffNumber', 68)
                WHERE Key = 'NgShuttleHardwareSettings';
                UPDATE Settings SET Value = json_set(json_remove(Value, '$.Inputs.NgConveyorAutoMode'),
                    '$.Inputs.NgConveyorPosition1Occupied', 83, '$.Inputs.NgConveyorPosition2Occupied', 84,
                    '$.Inputs.NgConveyorPosition3Occupied', 85,
                    '$.Inputs.NgConveyorStopperUp', 86, '$.Inputs.NgConveyorStopperDown', 87,
                    '$.Inputs.NgCarrierEjectButton', 88, '$.Inputs.NgCarrierEjectCompleteButton', 89,
                    '$.Outputs.NgConveyorStopperUp.Number', 69, '$.Outputs.NgConveyorStopperUp.OffNumber', 70,
                    '$.Outputs.NgConveyorRun.Number', 71, '$.Outputs.NgConveyorReverse.Number', 72,
                    '$.Outputs.NgConveyorNormalSpeed.Number', 73,
                    '$.Outputs.NgCarrierEjectLamp.Number', 74, '$.Outputs.NgCarrierEjectCompleteLamp.Number', 75)
                WHERE Key = 'NgConveyorHardwareSettings';
                DELETE FROM __EFMigrationsHistory WHERE MigrationId = '20260908150000_IoMap260901';
                """;
            command.ExecuteNonQuery();
        }

        var reopened = VirtualTest.OpenMachineStore(file);
        var loaded = await MachineSettings.LoadAsync(reopened);
        foreach (var property in typeof(MachineSettings).GetProperties())
        {
            if (property.GetValue(settings) is not InputHardwareSettings expected) continue;
            var actual = (InputHardwareSettings)property.GetValue(loaded)!;
            Assert.Equal(expected.Inputs.OrderBy(pair => pair.Key), actual.Inputs.OrderBy(pair => pair.Key));
            if (expected is not IoHardwareSettings expectedIo) continue;
            var actualIo = (IoHardwareSettings)actual;
            Assert.Equal(expectedIo.Outputs.Keys.Order(), actualIo.Outputs.Keys.Order());
            foreach (var (signal, output) in expectedIo.Outputs)
            {
                var saved = actualIo.Outputs[signal];
                Assert.Equal(output.Number, saved.Number);
                Assert.Equal(output.OffNumber, saved.OffNumber);
                Assert.Equal(output.Feedback?.OnInput, saved.Feedback?.OnInput);
                Assert.Equal(output.Feedback?.OffInput, saved.Feedback?.OffInput);
            }
        }
        Assert.Equal(17, loaded.PcbSupply.RotationZ);
        Assert.Equal(350, loaded.PcbSupplyHardware.Axes[MachineAxis.PcbSupplyY].Maximum);
        Assert.Equal(0.005, loaded.PcbSupplyHardware.MillimetersPerPulse);
        Assert.Equal("Part", reopened.LoadRecipe<Recipe>("Part").Name);

        loaded.ConveyorHardware.Inputs[InputIo.MainConveyorEntryCarrierDetected] = 99;
        await loaded.SaveAsync(reopened);
        Assert.Equal(99, (await MachineSettings.LoadAsync(VirtualTest.OpenMachineStore(file)))
            .ConveyorHardware.Inputs[InputIo.MainConveyorEntryCarrierDetected]);
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

    [Fact]
    public async Task LegacyImportIsAtomicPreservesOpticsAndNeverReimports()
    {
        var directory = CreateDirectory();
        var settingsPath = Directory.CreateDirectory(Path.Combine(directory, "Settings")).FullName;
        var carrierPath = Directory.CreateDirectory(Path.Combine(directory, "Recipes", "Part", "Carrier")).FullName;
        File.WriteAllText(Path.Combine(settingsPath, "PcbSupplySettings.json"), "{\"RotationZ\":17}");
        File.WriteAllText(Path.Combine(settingsPath, "InspectionCameraSettings.json"), "{\"DeviceId\":\"Cam1\",\"ExposureMicroseconds\":750,\"Gain\":2}");
        File.WriteAllText(Path.Combine(settingsPath, "DriverSettings.json"), "{\"Control\":\"Physical\"}");
        File.WriteAllText(Path.Combine(settingsPath, "LightingSettings.json"), "{\"Connection\":\"COM8\",\"InspectionChannel\":2,\"InspectionLevel\":90}");
        File.WriteAllText(Path.Combine(settingsPath, "InspectionGantrySettings.json"), "{\"CarrierScanOverlapMillimeters\":3}");
        File.WriteAllText(Path.Combine(settingsPath, "BoltInspectionSettings.json"), "{\"RegionSizePixels\":200,\"MaskThreshold\":0.7}");
        File.WriteAllText(Path.Combine(settingsPath, "RecipeSelectionSettings.json"), "{\"LastRecipeName\":\"Part\"}");
        File.WriteAllText(Path.Combine(directory, "Recipes", "Part", "Recipe.json"),
            "{\"Name\":\"Part\",\"BoltInspection\":{\"MinimumMaskRatio\":0.03},\"CarrierImages\":[{\"Number\":1,\"Center\":{\"X\":12,\"Y\":34}}]}");
        var store = VirtualTest.OpenMachineStore(Path.Combine(directory, "Machine.db"));
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
        Assert.Equal(LightDriver.Movs, settings.Drivers.Light);
        Assert.Equal("COM8", settings.Lighting.Connection);
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
