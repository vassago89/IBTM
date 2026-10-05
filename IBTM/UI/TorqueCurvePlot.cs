using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public sealed class TorqueCurvePlot : FrameworkElement
{
    public static readonly DependencyProperty CurveProperty;

    static TorqueCurvePlot()
    {
        CurveProperty = DependencyProperty.Register(nameof(Curve), typeof(AdcTorqueCurve), typeof(TorqueCurvePlot),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    }

    public AdcTorqueCurve? Curve
    {
        get => (AdcTorqueCurve?)GetValue(CurveProperty);
        set => SetValue(CurveProperty, value);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var textBrush = (Brush)FindResource("TextPrimaryBrush");
        var muted = (Brush)FindResource("TextMutedBrush");
        var lineBrush = (Brush)FindResource("AccentBlueBrush");
        drawing.DrawRectangle((Brush)FindResource("ImageBackgroundBrush"), null, new Rect(RenderSize));
        var typeface = new Typeface("Segoe UI");
        void DrawText(string value, double x, double y, Brush brush)
        {
            var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, 12, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawing.DrawText(text, new Point(x, y));
        }
        if (Curve is not { Torques.Length: > 0 } curve)
        {
            DrawText(UiText.Get("No torque curve"), 30, 35, muted);
            return;
        }
        var plot = new Rect(64, 38, Math.Max(1, ActualWidth - 90), Math.Max(1, ActualHeight - 100));
        var maximum = Math.Max(0.01, Math.Max(curve.Torques.Max(), curve.TargetTorque) * 1.1);
        var minimum = Math.Min(0, curve.Torques.Min());
        var duration = Math.Max(curve.SampleMilliseconds, (curve.Torques.Length - 1) * curve.SampleMilliseconds);
        var gridPen = new Pen(muted, 0.3);
        for (var tick = 0; tick <= 4; tick++)
        {
            var fraction = tick / 4.0;
            var y = plot.Bottom - plot.Height * fraction;
            drawing.DrawLine(gridPen, new(plot.Left, y), new(plot.Right, y));
            DrawText((minimum + (maximum - minimum) * fraction).ToString("0.##"), 8, y - 8, muted);
            var x = plot.Left + plot.Width * fraction;
            DrawText((duration * fraction).ToString("0"), x - 8, plot.Bottom + 8, muted);
        }
        DrawText(UiText.Get("Torque (controller unit)"), 12, 10, textBrush);
        DrawText(UiText.Get("Time (ms)"), Math.Max(64, plot.Right - 75), plot.Bottom + 30, textBrush);
        var targetY = plot.Bottom - (curve.TargetTorque - minimum) / (maximum - minimum) * plot.Height;
        drawing.DrawLine(new Pen(muted, 1) { DashStyle = DashStyles.Dash }, new(plot.Left, targetY), new(plot.Right, targetY));
        DrawText(UiText.Get("Target torque") + $" {curve.TargetTorque:0.##}", plot.Left + 8, targetY - 19, muted);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var index = 0; index < curve.Torques.Length; index++)
            {
                var point = new Point(plot.Left + index * curve.SampleMilliseconds / (double)duration * plot.Width,
                    plot.Bottom - (curve.Torques[index] - minimum) / (maximum - minimum) * plot.Height);
                if (index == 0)
                    context.BeginFigure(point, false, false);
                else
                    context.LineTo(point, true, false);
            }
        }
        geometry.Freeze();
        drawing.DrawGeometry(null, new Pen(lineBrush, 2), geometry);
        if (curve.Torques.Length * curve.SampleMilliseconds < curve.FasteningMilliseconds)
            DrawText(UiText.Get("Curve covers only part of the fastening time"), 64, ActualHeight - 17, muted);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Curve is not { Torques.Length: > 0 } curve)
            return;
        var fraction = Math.Clamp((e.GetPosition(this).X - 64) / Math.Max(1, ActualWidth - 90), 0, 1);
        var index = (int)Math.Round(fraction * (curve.Torques.Length - 1));
        ToolTip = $"{index * curve.SampleMilliseconds} ms · {curve.Torques[index]:0.##}";
    }
}
