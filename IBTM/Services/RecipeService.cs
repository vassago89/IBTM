using IBTM.Models;
using System.IO;
using System.Text.Json;

namespace IBTM.Services;

/// <summary>
/// 레시피/장비설정 JSON 직렬화 서비스
/// </summary>
public class RecipeService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public string RecipeDirectory { get; }
    public string ConfigFilePath { get; }

    public RecipeService()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        RecipeDirectory = Path.Combine(baseDir, "Recipes");
        ConfigFilePath = Path.Combine(baseDir, "MachineConfig.json");
        Directory.CreateDirectory(RecipeDirectory);
    }

    // ── 레시피 ──────────────────────────────────────────────────────

    public async Task SaveRecipeAsync(Recipe recipe, string? filePath = null)
    {
        filePath ??= Path.Combine(RecipeDirectory, $"{recipe.Name}.json");
        var json = JsonSerializer.Serialize(recipe, JsonOptions);
        await File.WriteAllTextAsync(filePath, json);
    }

    public async Task<Recipe> LoadRecipeAsync(string filePath)
    {
        var json = await File.ReadAllTextAsync(filePath);
        return JsonSerializer.Deserialize<Recipe>(json, JsonOptions) ?? new Recipe();
    }

    public IEnumerable<string> GetRecipeFiles() =>
        Directory.Exists(RecipeDirectory)
            ? Directory.GetFiles(RecipeDirectory, "*.json").OrderBy(f => f)
            : [];

    // ── 장비 설정 ───────────────────────────────────────────────────

    public async Task SaveConfigAsync(MachineConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        await File.WriteAllTextAsync(ConfigFilePath, json);
    }

    public async Task<MachineConfig> LoadConfigAsync()
    {
        if (!File.Exists(ConfigFilePath))
            return new MachineConfig();

        var json = await File.ReadAllTextAsync(ConfigFilePath);
        return JsonSerializer.Deserialize<MachineConfig>(json, JsonOptions) ?? new MachineConfig();
    }
}
