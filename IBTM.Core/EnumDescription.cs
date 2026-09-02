using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;

namespace IBTM.Core;

public static class EnumDescription
{
    private static readonly ConcurrentDictionary<Enum, string> Descriptions =
        new();

    public static string GetDescription(this Enum value) =>
        Descriptions.GetOrAdd(
            value,
            static item => item.GetType()
                .GetField(item.ToString())!
                .GetCustomAttribute<DescriptionAttribute>()!
                .Description);
}
