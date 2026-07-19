using System.IO;
using System.Text.Json;

namespace IBTM.Infrastructure.Persistence;

/// <summary>Persists recipes and machine configuration under the application directory.</summary>
public sealed class RecipeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public RecipeService()
        : this(AppContext.BaseDirectory)
    {
    }

    public RecipeService(string baseDirectory)
    {
        RecipeDirectory = Path.GetFullPath(Path.Combine(baseDirectory, "Recipes"));
        ConfigFilePath = Path.GetFullPath(Path.Combine(baseDirectory, "MachineConfig.json"));
        Directory.CreateDirectory(RecipeDirectory);
    }

    public string RecipeDirectory { get; }
    public string ConfigFilePath { get; }

    public Task SaveRecipeAsync(
        Recipe recipe,
        CancellationToken cancellationToken = default)
    {
        var fileName = GetRecipeFileName(recipe.Name);
        return WriteJsonAtomicallyAsync(
            Path.Combine(RecipeDirectory, fileName),
            recipe,
            cancellationToken);
    }

    public async Task<Recipe> LoadRecipeAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var safePath = GetPathInsideRecipeDirectory(filePath);
        await using var stream = new FileStream(
            safePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4_096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<Recipe>(
                   stream,
                   JsonOptions,
                   cancellationToken)
               ?? throw new InvalidDataException($"Recipe '{safePath}' is empty or invalid.");
    }

    public IReadOnlyList<string> GetRecipeFiles() =>
        Directory.Exists(RecipeDirectory)
            ? Directory.GetFiles(RecipeDirectory, "*.json")
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

    public Task SaveConfigAsync(
        MachineConfig config,
        CancellationToken cancellationToken = default)
    {
        return WriteJsonAtomicallyAsync(ConfigFilePath, config, cancellationToken);
    }

    public async Task<MachineConfig> LoadConfigAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ConfigFilePath))
        {
            return new MachineConfig();
        }

        await using var stream = new FileStream(
            ConfigFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4_096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<MachineConfig>(
                   stream,
                   JsonOptions,
                   cancellationToken)
               ?? throw new InvalidDataException("MachineConfig.json is empty or invalid.");
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
            throw new ArgumentException("Recipe name contains invalid file-name characters.", nameof(recipeName));
        }

        return $"{trimmedName}.json";
    }

    private string GetPathInsideRecipeDirectory(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        var relativePath = Path.GetRelativePath(RecipeDirectory, fullPath);
        if (relativePath == ".."
            || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("Recipe path must be inside the recipe directory.", nameof(filePath));
        }

        return fullPath;
    }
}
