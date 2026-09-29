using System;
using System.Windows.Markup;
using IBTM.Core;

namespace IBTM.UI;

[MarkupExtensionReturnType(typeof(string))]
public sealed class TextExtension : MarkupExtension
{
    public TextExtension(string text)
    {
        Text = text;
    }

    public string Text { get; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return UiText.Get(Text);
    }
}
