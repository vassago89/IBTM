using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Resources;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using IBTM.Core;
using Xunit;

namespace IBTM.Virtual.Tests;

[Collection("WPF UI")]
public sealed class LocalizationTests
{
    [Fact]
    public async Task LanguageIsSavedWithoutChangingNamesIdentityOrNumericCulture()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var settings = await MachineSettings.LoadAsync(store);
        Assert.Equal(UiLanguage.English, settings.Options.Language);
        settings.Options.Language = UiLanguage.Korean;
        await store.SaveSettingsAsync(settings.Sections);
        var loaded = await MachineSettings.LoadAsync(store);
        Assert.Equal(UiLanguage.Korean, loaded.Options.Language);

        var originalCulture = CultureInfo.CurrentCulture;
        var originalLanguage = UiText.Culture.Name == "ko" ? UiLanguage.Korean : UiLanguage.English;
        var bolt = new BoltPoint { Name = "Settings", FasteningX = 1.25, MinimumTurns = 2.5 };
        var json = JsonSerializer.Serialize(bolt);
        try
        {
            UiText.Apply(loaded.Options.Language);
            Assert.Equal("설정", UiText.Get("Settings"));
            Assert.Equal("볼트 3", BoltPoint.GetDisplayName(null, 3));
            Assert.Equal("Settings", BoltPoint.GetDisplayName(bolt.Name, 3));
            Assert.Equal(json, JsonSerializer.Serialize(bolt));
            Assert.Same(originalCulture, CultureInfo.CurrentCulture);
            Assert.Equal("custom message", UiText.Get("custom message"));

            UiText.Apply(UiLanguage.English);
            Assert.Equal("SETTINGS", UiText.Get("SETTINGS"));
            Assert.Equal("Bolt 3", BoltPoint.GetDisplayName(null, 3));
            Assert.Equal(json, JsonSerializer.Serialize(bolt));
        }
        finally
        {
            UiText.Apply(originalLanguage);
        }
    }

    [Fact]
    public void TranslationsRetainFormatArguments()
    {
        var resources = new ResourceManager("IBTM.Core.Resources.UiText", typeof(UiText).Assembly);
        using var english = resources.GetResourceSet(CultureInfo.GetCultureInfo("en"), true, true)!;
        using var korean = resources.GetResourceSet(CultureInfo.GetCultureInfo("ko"), true, false)!;
        Assert.Equal(english.Cast<DictionaryEntry>().Count(), korean.Cast<DictionaryEntry>().Count());
        foreach (DictionaryEntry entry in english)
        {
            var translated = korean.GetString((string)entry.Key);
            Assert.False(string.IsNullOrWhiteSpace(translated), (string)entry.Key);
            Assert.Equal(
                CompositeFormat.Parse((string)entry.Value!).MinimumArgumentCount,
                CompositeFormat.Parse(translated!).MinimumArgumentCount);
        }
    }

    [Fact]
    public void XamlTextAndFormattedBindingsUseSelectedLanguage()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var originalLanguage = UiText.Culture.Name == "ko" ? UiLanguage.Korean : UiLanguage.English;
            try
            {
                UiText.Apply(UiLanguage.Korean);
                var panel = (StackPanel)XamlReader.Parse("""
                    <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:ui="clr-namespace:IBTM.UI;assembly=IBTM">
                        <TextBlock Text="{ui:Text 'Settings'}"/>
                        <TextBlock Text="{Binding Count, StringFormat={ui:Text '{}{0} items'}}"/>
                    </StackPanel>
                    """);
                panel.DataContext = new { Count = 3 };
                panel.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                Assert.Equal("설정", ((TextBlock)panel.Children[0]).Text);
                Assert.Equal("3개", ((TextBlock)panel.Children[1]).Text);

            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                UiText.Apply(originalLanguage);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Localization binding check timed out.");
        Assert.Null(failure);
    }
}
