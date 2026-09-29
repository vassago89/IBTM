using System.Text.Json;

namespace IBTM.Storage;

public static class JsonCopy
{
    // Deep-copy stored data. JsonIgnore members and runtime state are not copied.
    public static T Clone<T>(this T source) where T : class
    {
        return JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(source))!;
    }
}
