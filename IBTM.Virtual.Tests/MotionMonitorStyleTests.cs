using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class MotionMonitorStyleTests
{
    [Fact]
    public async Task MotionBadgesRenderOnOffAndUnknownFromActualTemplates()
    {
        // Render only the production styles on an STA thread; never start App or any hardware.
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
                var source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MotionWindow.xaml"));
                var dictionary = new XElement(wpf + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", xaml),
                    new XElement(wpf + "SolidColorBrush", new XAttribute(xaml + "Key", "TextMutedBrush"), new XAttribute("Color", "Gray")),
                    new XElement(wpf + "SolidColorBrush", new XAttribute(xaml + "Key", "EquipBgBrush"), new XAttribute("Color", "Black")),
                    new XElement(wpf + "SolidColorBrush", new XAttribute(xaml + "Key", "DividerBrush"), new XAttribute("Color", "Gray")),
                    new XElement(wpf + "SolidColorBrush", new XAttribute(xaml + "Key", "IoInputOnBrush"), new XAttribute("Color", "#39BAFF")),
                    new XElement(wpf + "SolidColorBrush", new XAttribute(xaml + "Key", "AccentRedBrush"), new XAttribute("Color", "Red")),
                    source.Root!.Element(wpf + "Window.Resources")!.Elements().Select(element => new XElement(element)));
                var resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
                foreach (var key in new[] { "MotionBit", "MotionFaultBit" })
                {
                    var badge = new ContentControl { Style = (Style)resources[key] };
                    foreach (bool? value in new bool?[] { true, false, null, true })
                    {
                        badge.Tag = value;
                        badge.ApplyTemplate();
                        badge.Measure(new Size(60, 32));
                        badge.Arrange(new Rect(0, 0, 60, 32));
                        var border = Assert.IsType<Border>(VisualTreeHelper.GetChild(badge, 0));
                        var label = Assert.IsType<TextBlock>(border.Child);
                        Assert.Equal(value is true ? "ON" : value is false ? "OFF" : "—", label.Text);
                        if (value is true)
                            Assert.Equal(key == "MotionFaultBit" ? Colors.Red : Color.FromRgb(0x39, 0xBA, 0xFF),
                                Assert.IsType<SolidColorBrush>(border.Background).Color);
                    }
                }
                finished.SetResult();
            }
            catch (Exception error) { finished.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
