using System;
using System.ComponentModel;
using System.Reflection;

namespace IBTM.Core;

public static class EnumDescription
{
    public static string GetDescription(this Enum value) =>
        value.GetType()
            .GetField(value.ToString())!
            .GetCustomAttribute<DescriptionAttribute>()!
            .Description;
}
