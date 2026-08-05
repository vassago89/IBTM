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

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4_096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
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
        var filePath = GetFilePath(GetType());
        var temporaryPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
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
                    this,
                    GetType(),
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string GetFilePath(Type type) =>
        Path.Combine(DirectoryPath, $"{type.Name}.json");
}
