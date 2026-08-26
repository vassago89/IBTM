using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Ajin;
using IBTM.AlphaMotion;
using IBTM.BoltFeeder;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Hantas;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbBuffer;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;

namespace IBTM;

public sealed class MachineStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _recipeDirectory =
        Path.Combine(AppContext.BaseDirectory, "Recipes");

    public MachineStore() => Directory.CreateDirectory(_recipeDirectory);

    public async Task SaveRecipeAsync(
        Recipe recipe,
        CancellationToken cancellationToken = default)
    {
        var recipeDirectory = GetRecipeDirectory(recipe.Name);
        Directory.CreateDirectory(recipeDirectory);
        var filePath = Path.Combine(recipeDirectory, "Recipe.json");
        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(
            stream,
            recipe,
            JsonOptions,
            cancellationToken);
    }

    public async Task<Recipe> LoadRecipeAsync(
        string recipeName,
        CancellationToken cancellationToken = default)
    {
        var filePath = Path.Combine(
            GetRecipeDirectory(recipeName),
            "Recipe.json");
        await using var stream = File.OpenRead(filePath);
        var recipe = await JsonSerializer.DeserializeAsync<Recipe>(
            stream,
            JsonOptions,
            cancellationToken);
        return recipe
            ?? throw new InvalidDataException(
                $"Recipe '{recipeName}' is empty or invalid.");
    }

    public IReadOnlyList<string> GetRecipeNames() =>
        Directory.GetDirectories(_recipeDirectory)
            .Where(path => File.Exists(Path.Combine(path, "Recipe.json")))
            .Select(path => new DirectoryInfo(path).Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public string GetRecipeImageDirectory(string recipeName) =>
        Path.Combine(
            GetRecipeDirectory(recipeName),
            "Carrier");

    public string GetRecipeImagePath(string recipeName, int number) =>
        Path.Combine(
            GetRecipeImageDirectory(recipeName),
            $"{number:D4}.png");

    public Task SaveSettingsAsync(
        MachineSettings settings,
        CancellationToken cancellationToken = default) =>
        Task.WhenAll(settings.HardwareSections
            .Select(setting => setting.SaveAsync(cancellationToken))
            .Concat([
            settings.Drivers.SaveAsync(cancellationToken),
            settings.Units.SaveAsync(cancellationToken),
            settings.Options.SaveAsync(cancellationToken),
            settings.Home.SaveAsync(cancellationToken),
            settings.Ajin.SaveAsync(cancellationToken),
            settings.AlphaMotion.SaveAsync(cancellationToken),
            settings.InspectionCamera.SaveAsync(cancellationToken),
            settings.BoltInspection.SaveAsync(cancellationToken),
            settings.Lighting.SaveAsync(cancellationToken),
            settings.Hantas.SaveAsync(cancellationToken),
            settings.PcbBuffer.SaveAsync(cancellationToken),
            settings.PcbSupply.SaveAsync(cancellationToken),
            settings.PcbPlacementHandler.SaveAsync(cancellationToken),
            settings.BoltFeeder.SaveAsync(cancellationToken),
            settings.BoltFastening.SaveAsync(cancellationToken),
            settings.InspectionGantry.SaveAsync(cancellationToken),
            ]));

    public async Task<MachineSettings> LoadSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        return new MachineSettings
        {
            Drivers = await Setting.LoadAsync<DriverSettings>(cancellationToken),
            Units = await Setting.LoadAsync<UnitSettings>(
                cancellationToken),
            Options = await Setting.LoadAsync<MachineOptions>(cancellationToken),
            Home = await Setting.LoadAsync<HomeSettings>(cancellationToken),
            MachineHardware = await Setting.LoadAsync<MachineHardwareSettings>(
                cancellationToken),
            ConveyorHardware = await Setting.LoadAsync<ConveyorHardwareSettings>(
                cancellationToken),
            Ajin = await Setting.LoadAsync<AjinSettings>(cancellationToken),
            AlphaMotion = await Setting.LoadAsync<AlphaMotionSettings>(
                cancellationToken),
            InspectionCamera = await Setting.LoadAsync<InspectionCameraSettings>(
                cancellationToken),
            BoltInspection = await Setting.LoadAsync<BoltInspectionSettings>(
                cancellationToken),
            Lighting = await Setting.LoadAsync<LightingSettings>(cancellationToken),
            Hantas = await Setting.LoadAsync<HantasSettings>(cancellationToken),
            PcbBuffer = await Setting.LoadAsync<PcbBufferSettings>(
                cancellationToken),
            PcbBufferHardware = await Setting.LoadAsync<PcbBufferHardwareSettings>(
                cancellationToken),
            PcbSupply = await Setting.LoadAsync<PcbSupplySettings>(
                cancellationToken),
            PcbSupplyHardware = await Setting.LoadAsync<PcbSupplyHardwareSettings>(
                cancellationToken),
            PcbPlacementHandler =
                await Setting.LoadAsync<PcbPlacementHandlerSettings>(
                    cancellationToken),
            PcbPlacementHandlerHardware =
                await Setting.LoadAsync<PcbPlacementHandlerHardwareSettings>(
                    cancellationToken),
            PcbPlacementStationHardware =
                await Setting.LoadAsync<PcbPlacementStationHardwareSettings>(
                    cancellationToken),
            BoltFeeder = await Setting.LoadAsync<BoltFeederSettings>(
                cancellationToken),
            BoltFeederHardware =
                await Setting.LoadAsync<BoltFeederHardwareSettings>(
                    cancellationToken),
            BoltFastening = await Setting.LoadAsync<BoltFasteningSettings>(
                cancellationToken),
            BoltFasteningHardware =
                await Setting.LoadAsync<BoltFasteningHardwareSettings>(
                    cancellationToken),
            BoltFasteningStationHardware =
                await Setting.LoadAsync<BoltFasteningStationHardwareSettings>(
                    cancellationToken),
            InspectionGantry = await Setting.LoadAsync<InspectionGantrySettings>(
                cancellationToken),
            InspectionStationHardware =
                await Setting.LoadAsync<InspectionStationHardwareSettings>(
                    cancellationToken),
            InspectionGantryHardware =
                await Setting.LoadAsync<InspectionGantryHardwareSettings>(
                    cancellationToken),
            NgShuttleHardware = await Setting.LoadAsync<NgShuttleHardwareSettings>(
                cancellationToken),
            NgConveyorHardware =
                await Setting.LoadAsync<NgConveyorHardwareSettings>(
                    cancellationToken),
        };
    }

    private string GetRecipeDirectory(string recipeName)
    {
        var directoryName = recipeName.Trim();
        if (Path.GetFileName(directoryName) != directoryName)
        {
            throw new ArgumentException("Invalid recipe name.", nameof(recipeName));
        }

        return Path.Combine(_recipeDirectory, directoryName);
    }

}
