using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using IBTM.Core;

namespace IBTM.UI;

public sealed class ProbeDisplacementPlot : FrameworkElement
{
    public static readonly DependencyProperty SampleProperty;
    public static readonly DependencyProperty ReferenceHeightProperty;
    public static readonly DependencyProperty ShowRelativeHeightProperty;

    static ProbeDisplacementPlot()
    {
        SampleProperty = DependencyProperty.Register(nameof(Sample), typeof(HeightSample), typeof(ProbeDisplacementPlot),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        ReferenceHeightProperty = DependencyProperty.Register(nameof(ReferenceHeight), typeof(double?), typeof(ProbeDisplacementPlot),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        ShowRelativeHeightProperty = DependencyProperty.Register(nameof(ShowRelativeHeight), typeof(bool), typeof(ProbeDisplacementPlot),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    }

    public HeightSample? Sample
    {
        get => (HeightSample?)GetValue(SampleProperty);
        set => SetValue(SampleProperty, value);
    }

    public double? ReferenceHeight
    {
        get => (double?)GetValue(ReferenceHeightProperty);
        set => SetValue(ReferenceHeightProperty, value);
    }

    public bool ShowRelativeHeight
    {
        get => (bool)GetValue(ShowRelativeHeightProperty);
        set => SetValue(ShowRelativeHeightProperty, value);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var muted = (Brush)FindResource("TextMutedBrush");
        var raw = (Brush)FindResource("AccentBlueBrush");
        var relative = (Brush)FindResource("AccentGreenBrush");
        var baseline = (Brush)FindResource("AccentAmberBrush");
        var typeface = new Typeface("Segoe UI");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        void Label(string value, double x, double y, Brush brush, TextAlignment align = TextAlignment.Left)
        {
            var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 12, brush, dpi)
                { TextAlignment = align };
            drawing.DrawText(text, new Point(x, y));
        }

        drawing.DrawRectangle((Brush)FindResource("ImageBackgroundBrush"), null, new Rect(RenderSize));
        if (ActualWidth < 160 || ActualHeight < 95)
            return;
        if (Sample is not { Readings.Length: > 0 } sample)
        {
            Label(UiText.Get("No LVDT readings"), ActualWidth / 2, ActualHeight / 2 - 9, muted, TextAlignment.Center);
            return;
        }
        var readings = sample.Readings;
        var reference = ReferenceHeight ?? (ShowRelativeHeight ? (double?)null : sample.Value);
        var showHeight = ShowRelativeHeight && reference.HasValue;
        var min = readings.Min(p => p.Displacement);
        var max = readings.Max(p => p.Displacement);
        var top = 30d;
        Label(UiText.Format($"Raw mean {sample.Value:0.###} mm ({readings.Length})"), 10, 6, raw);
        if (reference is { } plane)
        {
            if (showHeight)
            {
                Label(UiText.Format($"Protrusion mean {sample.Value - plane:0.###} mm"), 10, top - 5, relative);
                top += 19;
                min = Math.Min(min, min - plane);
                max = Math.Max(max, max - plane);
            }
            var planeLabel = ShowRelativeHeight ? UiText.Format($"Reference plane {plane:0.###} mm")
                : UiText.Format($"Reference point mean {plane:0.###} mm");
            Label(planeLabel, 10, top - 5, baseline);
            top += 19;
            min = Math.Min(min, plane);
            max = Math.Max(max, plane);
        }
        var pad = Math.Max(0.001, (max - min) * 0.10);
        min -= pad;
        max += pad;
        var plot = new Rect(62, top + 5, Math.Max(1, ActualWidth - 84), Math.Max(1, ActualHeight - top - 36));
        var duration = Math.Max(0.001, readings[^1].ElapsedSeconds);
        double Y(double value)
        {
            return plot.Bottom - (value - min) / (max - min) * plot.Height;
        }
        var grid = new Pen(muted, 0.4);
        for (var i = 0; i <= 2; i++)
        {
            var value = min + (max - min) * i / 2;
            var y = Y(value);
            drawing.DrawLine(grid, new(plot.Left, y), new(plot.Right, y));
            Label($"{value:0.###}", plot.Left - 6, y - 8, muted, TextAlignment.Right);
        }
        Label("0 s", plot.Left, plot.Bottom + 5, muted);
        Label($"{duration:0.###} s", plot.Right, plot.Bottom + 5, muted, TextAlignment.Right);
        void DrawReadings(double offset, Brush brush, double radius)
        {
            var pen = new Pen(brush, 1.5);
            Point? previous = null;
            foreach (var reading in readings)
            {
                var p = new Point(plot.Left + plot.Width * reading.ElapsedSeconds / duration, Y(reading.Displacement - offset));
                if (previous is { } last)
                    drawing.DrawLine(pen, last, p);
                drawing.DrawEllipse(brush, null, p, radius, radius);
                previous = p;
            }
        }
        DrawReadings(0, raw, 3.5);
        if (showHeight)
            DrawReadings(reference!.Value, relative, 2);
        if (reference is { } height)
        {
            var dashed = new Pen(baseline, 1.2) { DashStyle = DashStyles.Dash };
            drawing.DrawLine(dashed, new(plot.Left, Y(height)), new(plot.Right, Y(height)));
        }
    }
}
