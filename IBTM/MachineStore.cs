using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Ajin;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbBuffer;
using IBTM.PcbSupply;
using IBTM.Stations.BoltFastening;
using IBTM.Stations.Inspection;
using IBTM.Stations.PcbPlacement;

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

    public Task SaveRecipeAsync(
        Recipe recipe,
        CancellationToken cancellationToken = default) =>
        WriteJsonAtomicallyAsync(
            Path.Combine(_recipeDirectory, GetRecipeFileName(recipe.Name)),
            recipe,
            cancellationToken);

    public async Task<Recipe> LoadRecipeAsync(
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var filePath = Path.Combine(_recipeDirectory, fileName);
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4_096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var recipe = await JsonSerializer.DeserializeAsync<Recipe>(
            stream,
            JsonOptions,
            cancellationToken);
        return recipe
            ?? throw new InvalidDataException(
                $"Recipe '{fileName}' is empty or invalid.");
    }

    public IReadOnlyList<string> GetRecipeFiles() =>
        Directory.GetFiles(_recipeDirectory, "*.json")
            .Select(path => new FileInfo(path).Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public Task SaveSettingsAsync(
        MachineSettings settings,
        CancellationToken cancellationToken = default) =>
        Task.WhenAll(
            settings.SaveAsync(cancellationToken),
            settings.Hardware.SaveAsync(cancellationToken),
            settings.Ajin.SaveAsync(cancellationToken),
            settings.AlignmentCamera.SaveAsync(cancellationToken),
            settings.InspectionCamera.SaveAsync(cancellationToken),
            settings.Lighting.SaveAsync(cancellationToken),
            settings.PcbBuffer.SaveAsync(cancellationToken),
            settings.PcbSupply.SaveAsync(cancellationToken),
            settings.PcbPlacement.SaveAsync(cancellationToken),
            settings.BoltFastening.SaveAsync(cancellationToken),
            settings.Inspection.SaveAsync(cancellationToken));

    public async Task<MachineSettings> LoadSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await Setting.LoadAsync<MachineSettings>(cancellationToken);
        settings.Hardware = await Setting.LoadAsync<HardwareMap>(cancellationToken);
        settings.Ajin = await Setting.LoadAsync<AjinSettings>(cancellationToken);
        settings.AlignmentCamera =
            await Setting.LoadAsync<AlignmentCameraSettings>(cancellationToken);
        settings.InspectionCamera =
            await Setting.LoadAsync<InspectionCameraSettings>(cancellationToken);
        settings.Lighting = await Setting.LoadAsync<LightingSettings>(cancellationToken);
        settings.PcbBuffer = await Setting.LoadAsync<PcbBufferSettings>(cancellationToken);
        settings.PcbSupply = await Setting.LoadAsync<PcbSupplySettings>(cancellationToken);
        settings.PcbPlacement =
            await Setting.LoadAsync<PcbPlacementSettings>(cancellationToken);
        settings.BoltFastening =
            await Setting.LoadAsync<BoltFasteningSettings>(cancellationToken);
        settings.Inspection =
            await Setting.LoadAsync<InspectionSettings>(cancellationToken);
        return settings;
    }

    private static async Task WriteJsonAtomicallyAsync<T>(
        string destinationPath,
        T value,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4_096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    value,
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string GetRecipeFileName(string recipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeName);
        var trimmedName = recipeName.Trim();
        if (trimmedName is "." or ".."
            || trimmedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || trimmedName.Contains(Path.DirectorySeparatorChar)
            || trimmedName.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException(
                "Recipe name contains invalid file-name characters.",
                nameof(recipeName));
        }

        return $"{trimmedName}.json";
    }

}
