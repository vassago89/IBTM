using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace IBTM.Core;

public abstract class Setting
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly string DirectoryPath =
        Path.Combine(AppContext.BaseDirectory, "Settings");

    public static async Task<T> LoadAsync<T>(
        CancellationToken cancellationToken = default)
        where T : Setting, new()
    {
        Directory.CreateDirectory(DirectoryPath);
        var filePath = GetFilePath(typeof(T));
        if (!File.Exists(filePath))
        {
            return new T();
        }

        await using var stream = File.OpenRead(filePath);
        return await JsonSerializer.DeserializeAsync<T>(
                   stream,
                   JsonOptions,
                   cancellationToken)
               ?? throw new InvalidDataException(
                   $"{typeof(T).Name}.json is empty or invalid.");
    }

    public async Task SaveAsync(
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DirectoryPath);
        await using var stream = File.Create(GetFilePath(GetType()));
        await JsonSerializer.SerializeAsync(
            stream,
            this,
            GetType(),
            JsonOptions,
            cancellationToken);
    }

    private static string GetFilePath(Type type) =>
        Path.Combine(DirectoryPath, $"{type.Name}.json");
}
