using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using IBTM.Core;

namespace IBTM.UI;

public sealed class TorqueCurvePlot : FrameworkElement
{
    public static readonly DependencyProperty CurveProperty;
    public static readonly DependencyProperty TargetTorqueProperty;

    static TorqueCurvePlot()
    {
        CurveProperty = DependencyProperty.Register(nameof(Curve), typeof(AdcTorqueCurve), typeof(TorqueCurvePlot),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        TargetTorqueProperty = DependencyProperty.Register(nameof(TargetTorque), typeof(double?), typeof(TorqueCurvePlot),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    }

    public AdcTorqueCurve? Curve
    {
        get => (AdcTorqueCurve?)GetValue(CurveProperty);
        set => SetValue(CurveProperty, value);
    }

    public double? TargetTorque
    {
        get => (double?)GetValue(TargetTorqueProperty);
        set => SetValue(TargetTorqueProperty, value);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var textBrush = (Brush)FindResource("TextPrimaryBrush");
        var muted = (Brush)FindResource("TextMutedBrush");
        var lineBrush = (Brush)FindResource("AccentBlueBrush");
        drawing.DrawRectangle((Brush)FindResource("ImageBackgroundBrush"), null, new Rect(RenderSize));
        var typeface = new Typeface("Segoe UI");
        void DrawText(string value, double x, double y, Brush brush, TextAlignment alignment = TextAlignment.Left)
        {
            var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, 12, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip) { TextAlignment = alignment };
            drawing.DrawText(text, new Point(x, y));
        }
        if (Curve is not { Torques.Length: > 0 } curve)
        {
            DrawText(UiText.Get("No torque curve"), 30, 35, muted);
            return;
        }
        var plot = new Rect(64, 38, Math.Max(1, ActualWidth - 90), Math.Max(1, ActualHeight - 100));
        var maximum = Math.Max(0.01, Math.Max(curve.Torques.Max(), TargetTorque ?? 0) * 1.1);
        var minimum = Math.Min(0, curve.Torques.Min());
        var duration = Math.Max(curve.SampleMilliseconds,
            Math.Max(curve.FasteningMilliseconds, curve.StartMilliseconds + (curve.Torques.Length - 1) * curve.SampleMilliseconds));
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
        DrawText(UiText.Format($"{curve.SampleMilliseconds} ms · {curve.Torques.Length} samples"),
            plot.Right, 10, muted, TextAlignment.Right);
        DrawText(UiText.Get("Time (ms)"), Math.Max(64, plot.Right - 75), plot.Bottom + 30, textBrush);
        if (TargetTorque is { } targetTorque)
        {
            var targetY = plot.Bottom - (targetTorque - minimum) / (maximum - minimum) * plot.Height;
            drawing.DrawLine(new Pen(muted, 1) { DashStyle = DashStyles.Dash }, new(plot.Left, targetY), new(plot.Right, targetY));
            DrawText(UiText.Get("Target torque") + $" {targetTorque:0.##}", plot.Left + 8, targetY - 19, muted);
        }
        var yScale = plot.Height / (maximum - minimum);
        var sampleWidth = curve.SampleMilliseconds / (double)duration * plot.Width;
        Point SamplePoint(int index)
        {
            return new(plot.Left + curve.StartMilliseconds / (double)duration * plot.Width + index * sampleWidth,
                plot.Bottom - (curve.Torques[index] - minimum) * yScale);
        }
        double Slope(double before, double after)
        {
            // Monotone cubic interpolation passes through every sample without inventing peaks.
            return before * after <= 0 ? 0 : 2 * before * after / (before + after);
        }
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var previous = SamplePoint(0);
            context.BeginFigure(previous, false, false);
            for (var index = 1; index < curve.Torques.Length; index++)
            {
                var point = SamplePoint(index);
                var change = curve.Torques[index] - curve.Torques[index - 1];
                var startSlope = Slope(index > 1 ? curve.Torques[index - 1] - curve.Torques[index - 2] : change, change);
                var endSlope = Slope(change, index + 1 < curve.Torques.Length ? curve.Torques[index + 1] - curve.Torques[index] : change);
                context.BezierTo(
                    new(previous.X + sampleWidth / 3, previous.Y - startSlope * yScale / 3),
                    new(point.X - sampleWidth / 3, point.Y + endSlope * yScale / 3),
                    point, true, false);
                previous = point;
            }
        }
        geometry.Freeze();
        drawing.DrawGeometry(null, new Pen(lineBrush, 2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        }, geometry);
        if (curve.Torques.Length == 1)
            drawing.DrawEllipse(lineBrush, null, SamplePoint(0), 3, 3);
        if (curve.StartMilliseconds > 0)
            DrawText(UiText.Get("Curve covers only part of the fastening time"), 64, ActualHeight - 17, muted);

        if (!IsMouseOver)
            return;
        var pointer = Mouse.GetPosition(this);
        if (!plot.Contains(pointer))
            return;
        var samplePosition = ((pointer.X - plot.Left) / plot.Width * duration - curve.StartMilliseconds) / curve.SampleMilliseconds;
        if (samplePosition < 0 || samplePosition > curve.Torques.Length - 1)
            return;
        var selectedIndex = (int)Math.Round(samplePosition);
        var selectedPoint = SamplePoint(selectedIndex);
        drawing.DrawLine(new Pen(muted, 0.8) { DashStyle = DashStyles.Dash },
            new(selectedPoint.X, plot.Top), new(selectedPoint.X, plot.Bottom));
        drawing.DrawEllipse(lineBrush, new Pen(textBrush, 1.5), selectedPoint, 4, 4);
        // Show the nearest received sample immediately, not an interpolated measurement.
        var label = new FormattedText(
            $"{curve.StartMilliseconds + selectedIndex * curve.SampleMilliseconds} ms · {UiText.Get("Torque")} {curve.Torques[selectedIndex]:0.##}",
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 13, textBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var labelWidth = label.Width + 20;
        var labelHeight = label.Height + 12;
        var labelRect = new Rect(
            Math.Clamp(selectedPoint.X + 12, plot.Left, Math.Max(plot.Left, plot.Right - labelWidth)),
            Math.Clamp(selectedPoint.Y - labelHeight - 12, plot.Top, Math.Max(plot.Top, plot.Bottom - labelHeight)),
            labelWidth, labelHeight);
        drawing.DrawRoundedRectangle((Brush)FindResource("ControlBgBrush"), new Pen(lineBrush, 1), labelRect, 4, 4);
        drawing.DrawText(label, new(labelRect.Left + 10, labelRect.Top + 6));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        InvalidateVisual();
    }
}
