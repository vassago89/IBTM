using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using IBTM.Conveyor;
using IBTM.Device;
using IBTM.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class MachineStoreTests
{
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
        settings.PcbSupplyHardware.Outputs[OutputIo.PcbSupplyIpmFixerForward].Feedback = new(
            InputIo.PcbSupplyIpmFixerForward, InputIo.PcbSupplyIpmFixerBackward);
        settings.ConveyorHardware.Inputs[InputIo.MainConveyorEntryCarrierDetected] = 91;
        settings.ConveyorHardware.Inputs[InputIo.MainConveyorExitCarrierDetected] = 92;
        settings.ConveyorHardware.Inputs[InputIo.PcbPlacementStopperUp] = 57;
        settings.ConveyorHardware.Inputs[InputIo.PcbPlacementStopperDown] = 58;
        settings.ConveyorHardware.Outputs[OutputIo.PcbPlacementStopperUp].Number = 96;
        settings.ConveyorHardware.Outputs[OutputIo.PcbPlacementStopperUp].OffNumber = 97;
        settings.NgConveyorHardware.Inputs.Remove(InputIo.NgConveyorStopperUp);
        settings.NgConveyorHardware.Inputs.Remove(InputIo.NgConveyorStopperDown);
        settings.NgConveyorHardware.Outputs[OutputIo.NgConveyorStopperUp].Feedback = null;
        settings.BoltFasteningStationHardware.Inputs[InputIo.BoltFasteningHeatSink1Present] = 61;
        settings.BoltFasteningStationHardware.Inputs[InputIo.BoltFasteningHeatSink2Present] = 62;
        settings.InspectionStationHardware.Inputs[InputIo.InspectionHeatSink1Present] = 68;
        settings.InspectionStationHardware.Inputs[InputIo.InspectionHeatSink2Present] = 69;
        settings.PcbPlacementHandlerHardware.MillimetersPerUnit = 0.002;
        settings.PcbPlacementHandlerHardware.GetAxis(MotionAxis.Y)!.MoveUnit = 0.1;
        settings.PcbPlacementHandlerHardware.GetAxis(MotionAxis.Y)!.MovePulse = 10;
        settings.PcbPlacementHandlerHardware.GetAxis(MotionAxis.Y)!.HomeDirection = HomeDirection.Positive;
        settings.BoltFastening.SafeZ = 7;
        settings.BoltFastening.ShootingHead.FasteningZ = 14;
        settings.BoltFastening.PickupHead.FasteningZ = 18;
        settings.Drivers.Bolt = BoltDriver.Io;
        settings.IoBoltHardware.Inputs[InputIo.PickupBoltReady] = 112;
        settings.IoBoltHardware.Outputs[OutputIo.ShootingBoltStart].Number = 115;
        await settings.SaveAsync(store);
        var recipe = new Recipe { Name = "Part" };
        recipe.BoltInspection.LightLevel = 90;
        store.SaveRecipe("Part", recipe, [1], images: [new(1, [1, 2, 3])]);

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
            JsonSerializer.Serialize(reopened.LoadRecipe<Recipe>("Part")));
        Assert.Equal(new byte[] { 1, 2, 3 }, reopened.LoadRecipeImage("Part", 1));
    }

    [Fact]
    public async Task FailedSettingsBatchDoesNotSavePartialChanges()
    {
        var store = new MachineStore(Path.Combine(CreateDirectory(), "Machine.db"));
        var settings = new MachineSettings();
        settings.PcbSupply.RotationZ = 12;
        settings.PcbBuffer.SupplyBoundary1 = 45;
        await settings.SaveAsync(store);

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

        var loaded = await MachineSettings.LoadAsync(new MachineStore(store.DatabaseFile));
        Assert.Equal(45, loaded.PcbBuffer.SupplyBoundary1);
        Assert.Equal(12, loaded.PcbSupply.RotationZ);
        await settings.SaveAsync(store);
        loaded = await MachineSettings.LoadAsync(new MachineStore(store.DatabaseFile));
        Assert.Equal(100, loaded.PcbBuffer.SupplyBoundary1);
        Assert.Equal(30, loaded.PcbSupply.RotationZ);
    }

    private static string CreateDirectory()
    {
        return Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"IBTM-Storage-{Guid.NewGuid():N}"))
            .FullName;
    }
}
