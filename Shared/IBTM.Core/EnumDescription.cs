using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;

namespace IBTM.Core;

public static class EnumDescription
{
    private static readonly ConcurrentDictionary<Enum, string> s_descriptions;

    static EnumDescription()
    {
        s_descriptions = new();
    }

    public static string GetDescription(this Enum value)
    {
        return s_descriptions.GetOrAdd(
            value,
            static item =>
                item.GetType().GetField(item.ToString())?.GetCustomAttribute<DescriptionAttribute>()?.Description
                    ?? item.ToString());
    }
}
