using System;
using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace IBTM.Core;

public enum UiLanguage
{
    [Description("English")]
    English,
    [Description("한국어")]
    Korean,
}

public static class UiText
{
    private static readonly ResourceManager s_resources;

    static UiText()
    {
        s_resources = new("IBTM.Core.Resources.UiText", typeof(UiText).Assembly);
        Culture = CultureInfo.GetCultureInfo("en");
    }

    public static CultureInfo Culture { get; private set; }

    public static void Apply(UiLanguage language)
    {
        // UI resources only: numeric input, device protocols and log formatting keep their culture.
        Culture = CultureInfo.GetCultureInfo(language == UiLanguage.Korean ? "ko" : "en");
    }

    public static string Get(string text)
    {
        return s_resources.GetString(text, Culture) ?? text;
    }

    public static string Get(Enum value)
    {
        return Get(value.GetDescription());
    }

    public static string Format(string text, params object?[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, Get(text), arguments);
    }
}
