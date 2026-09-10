using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.Storage;

namespace IBTM;
// One-time, read-only import. Original JSON and PNG files remain available for recovery.
internal static class LegacyMachineImport
{
    internal static void Run(MachineStore store, string directory)
    {
        if (store.HasData)
            return;
        var settingsPath = Path.Combine(directory, "Settings");
        var recipesPath = Path.Combine(directory, "Recipes");
        var files = Directory.Exists(settingsPath) ? Directory.GetFiles(settingsPath, "*.json") : [];
        var recipeFiles = Directory.Exists(recipesPath)
            ? Directory.GetDirectories(recipesPath)
                .Select(path => Path.Combine(path, "Recipe.json"))
                .Where(File.Exists)
                .ToArray()
            : [];
        if (files.Length == 0 && recipeFiles.Length == 0)
            return;

        var values = files.ToDictionary(path => Path.GetFileNameWithoutExtension(path), File.ReadAllText);
        var settings = MachineSettings.From(new SavedSettings(values));
        if (values.TryGetValue(nameof(DriverSettings), out var driverJson)
            && JsonNode.Parse(driverJson)?[nameof(DriverSettings.Light)] is null)
            settings.Drivers.Light = settings.Drivers.Control == ControlDriver.Physical
                ? LightDriver.Movs
                : LightDriver.Virtual;
        var recipes = new List<StoredRecipe>();
        foreach (var file in recipeFiles)
        {
            var node = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            AddInspectionConditions(node, values);
            var recipe = node.Deserialize<Recipe>()!;
            recipe.Name = Path.GetFileName(Path.GetDirectoryName(file))!;
            var images = recipe.CarrierImages.Select(
                tile =>
                    new RecipeImage(
                        tile.Number,
                        File.ReadAllBytes(
                            Path.Combine(
                                Path.GetDirectoryName(file)!,
                                "Carrier",
                                $"{tile.Number:D4}.png"))));
            recipes.Add(new(recipe.Name, JsonSerializer.Serialize(recipe), images));
        }

        if (recipes.Count == 0 && settings.RecipeSelection.LastRecipeName is null)
        {
            var node = JsonSerializer.SerializeToNode(new Recipe())!.AsObject();
            AddInspectionConditions(node, values, replaceDefaults: true);
            var recipe = node.Deserialize<Recipe>()!;
            recipes.Add(new(recipe.Name, JsonSerializer.Serialize(recipe), []));
            settings.RecipeSelection.LastRecipeName = recipe.Name;
        }

        if (settings.RecipeSelection.LastRecipeName is { } selected
            && !recipes.Any(
                recipe => string.Equals(recipe.Name, selected, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Selected legacy recipe '{selected}' is missing.");
        store.ImportLegacy(settings.Sections, recipes);
    }

    private static void AddInspectionConditions(
        JsonObject recipe,
        IReadOnlyDictionary<string, string> settings,
        bool replaceDefaults = false)
    {
        var conditions = recipe[nameof(Recipe.BoltInspection)] as JsonObject;
        if (conditions is null)
            recipe[nameof(Recipe.BoltInspection)] = conditions = new();
        Copy(nameof(InspectionCameraSettings), nameof(BoltInspectionRecipe.ExposureMicroseconds));
        Copy(nameof(InspectionCameraSettings), nameof(BoltInspectionRecipe.Gain));
        Copy(nameof(LightingSettings), nameof(BoltInspectionRecipe.LightLevel), "InspectionLevel");
        Copy(
            nameof(InspectionGantrySettings),
            nameof(BoltInspectionRecipe.CarrierScanOverlapMillimeters));
        Copy("BoltInspectionSettings", nameof(BoltInspectionRecipe.RegionSizePixels));
        Copy("BoltInspectionSettings", nameof(BoltInspectionRecipe.MinimumMaskRatio));

        void Copy(string section, string target, string? source = null)
        {
            if ((!replaceDefaults && conditions.ContainsKey(target))
                || !settings.TryGetValue(section, out var json))
                return;
            if (JsonNode.Parse(json)?[source ?? target] is { } value)
                conditions[target] = value.DeepClone();
        }
    }
}
