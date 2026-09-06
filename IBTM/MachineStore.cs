using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using IBTM.Ajin;
using IBTM.AlphaMotion;
using IBTM.BoltFastening;
using IBTM.BoltFeeder;
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
    private const string RecipeFileName = "Recipe.json";

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
        var filePath = Path.Combine(recipeDirectory, RecipeFileName);
        var temporaryPath = $"{filePath}.tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                recipe,
                JsonOptions,
                cancellationToken);
        }

        File.Move(temporaryPath, filePath, overwrite: true);
    }

    public async Task<Recipe> LoadRecipeAsync(
        string recipeName,
        CancellationToken cancellationToken = default)
    {
        var filePath = Path.Combine(
            GetRecipeDirectory(recipeName),
            RecipeFileName);
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
            .Where(path => File.Exists(Path.Combine(path, RecipeFileName)))
            .Select(path => new DirectoryInfo(path).Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public void ClearRecipeImages(string recipeName)
    {
        var directory = GetRecipeImageDirectory(recipeName);
        Directory.CreateDirectory(directory);
        foreach (var path in Directory.GetFiles(directory, "*.png"))
        {
            File.Delete(path);
        }
    }

    public void SaveRecipeImage(
        string recipeName,
        int number,
        BitmapSource image)
    {
        Directory.CreateDirectory(GetRecipeImageDirectory(recipeName));
        using var stream = File.Create(GetRecipeImagePath(recipeName, number));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
    }

    public BitmapSource LoadRecipeImage(string recipeName, int number)
    {
        using var stream = File.OpenRead(GetRecipeImagePath(recipeName, number));
        var decoder = new PngBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var image = decoder.Frames[0];
        image.Freeze();
        return image;
    }

    public void CopyRecipeImages(
        string sourceRecipe,
        string targetRecipe,
        IEnumerable<int> numbers)
    {
        Directory.CreateDirectory(GetRecipeImageDirectory(targetRecipe));
        foreach (var number in numbers)
        {
            File.Copy(
                GetRecipeImagePath(sourceRecipe, number),
                GetRecipeImagePath(targetRecipe, number),
                overwrite: true);
        }
    }

    private string GetRecipeImageDirectory(string recipeName) =>
        Path.Combine(
            GetRecipeDirectory(recipeName),
            "Carrier");

    private string GetRecipeImagePath(string recipeName, int number) =>
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
            settings.RecipeSelection.SaveAsync(cancellationToken),
            settings.CarrierReference.SaveAsync(cancellationToken),
            settings.Ajin.SaveAsync(cancellationToken),
            settings.AlphaMotion.SaveAsync(cancellationToken),
            settings.InspectionCamera.SaveAsync(cancellationToken),
            settings.BoltInspection.SaveAsync(cancellationToken),
            settings.Lighting.SaveAsync(cancellationToken),
            settings.Hantas.SaveAsync(cancellationToken),
            settings.NgCarrierTransfer.SaveAsync(cancellationToken),
            settings.NgConveyor.SaveAsync(cancellationToken),
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
            RecipeSelection =
                await Setting.LoadAsync<RecipeSelectionSettings>(
                    cancellationToken),
            CarrierReference =
                await Setting.LoadAsync<CarrierReferenceSettings>(
                    cancellationToken),
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
            NgCarrierTransfer =
                await Setting.LoadAsync<NgCarrierTransferSettings>(
                    cancellationToken),
            NgConveyor = await Setting.LoadAsync<NgConveyorSettings>(
                cancellationToken),
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
            NgCarrierTransferHardware =
                await Setting.LoadAsync<NgCarrierTransferHardwareSettings>(
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
        if (directoryName.Length == 0
            || directoryName.EndsWith('.')
            || directoryName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Invalid recipe name.", nameof(recipeName));
        }

        return Path.Combine(_recipeDirectory, directoryName);
    }

}
