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
    public static readonly DependencyProperty TorqueCompensationPercentProperty;

    static TorqueCurvePlot()
    {
        CurveProperty = DependencyProperty.Register(nameof(Curve), typeof(AdcTorqueCurve), typeof(TorqueCurvePlot),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        TargetTorqueProperty = DependencyProperty.Register(nameof(TargetTorque), typeof(double?), typeof(TorqueCurvePlot),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        TorqueCompensationPercentProperty = DependencyProperty.Register(nameof(TorqueCompensationPercent), typeof(ushort?), typeof(TorqueCurvePlot),
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

    public ushort? TorqueCompensationPercent
    {
        get => (ushort?)GetValue(TorqueCompensationPercentProperty);
        set => SetValue(TorqueCompensationPercentProperty, value);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var textBrush = (Brush)FindResource("TextPrimaryBrush");
        var muted = (Brush)FindResource("TextMutedBrush");
        var lineBrush = (Brush)FindResource("AccentBlueBrush");
        var angleBrush = (Brush)FindResource("AccentAmberBrush");
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
        var angles = curve.Angles is { Length: > 0 } ? curve.Angles : null;
        // Scale only the display samples; the result retains the received curve unchanged.
        var torques = TorqueCompensationPercent is { } compensationPercent
            ? curve.Torques.Select(torque => torque * compensationPercent / 100.0).ToArray() : null;
        if (torques is null && angles is null)
        {
            DrawText(UiText.Get("Torque compensation not recorded"), 30, 35, muted);
            return;
        }
        var plot = new Rect(64, 38, Math.Max(1, ActualWidth - (angles is null ? 90 : 128)), Math.Max(1, ActualHeight - 100));
        var maximum = Math.Max(0.01, Math.Max(torques?.Max() ?? 0, TargetTorque ?? 0) * 1.1);
        var minimum = Math.Min(0, torques?.Min() ?? 0);
        var angleMaximum = Math.Max(1, (angles?.Max() ?? 0) * 1.1);
        var angleMinimum = Math.Min(0, angles?.Min() ?? 0);
        var duration = Math.Max(curve.SampleMilliseconds,
            Math.Max(curve.FasteningMilliseconds, curve.StartMilliseconds + (curve.Torques.Length - 1) * curve.SampleMilliseconds));
        if (curve.StartMilliseconds > 0)
        {
            var missing = new Rect(plot.Left, plot.Top, curve.StartMilliseconds / (double)duration * plot.Width, plot.Height);
            drawing.PushOpacity(0.08);
            drawing.DrawRectangle(muted, null, missing);
            drawing.Pop();
        }
        var gridPen = new Pen(muted, 0.3);
        for (var tick = 0; tick <= 4; tick++)
        {
            var fraction = tick / 4.0;
            var y = plot.Bottom - plot.Height * fraction;
            drawing.DrawLine(gridPen, new(plot.Left, y), new(plot.Right, y));
            if (torques is not null)
                DrawText((minimum + (maximum - minimum) * fraction).ToString("0.##"), 8, y - 8, lineBrush);
            if (angles is not null)
                DrawText((angleMinimum + (angleMaximum - angleMinimum) * fraction).ToString("#,##0.##"), plot.Right + 8, y - 8, angleBrush);
            var x = plot.Left + plot.Width * fraction;
            DrawText((duration * fraction).ToString("#,##0"), x, plot.Bottom + 8, muted, TextAlignment.Center);
        }
        if (torques is not null)
            drawing.DrawLine(new Pen(lineBrush, 2), new(12, 18), new(34, 18));
        if (angles is not null)
            drawing.DrawLine(new Pen(angleBrush, 2) { DashStyle = DashStyles.Dash },
                new(ActualWidth - 112, 18), new(ActualWidth - 90, 18));
        DrawText(UiText.Get(torques is null ? "Torque compensation not recorded" : "Torque (controller unit)"),
            torques is null ? 12 : 42, 10, torques is null ? muted : lineBrush);
        DrawText(UiText.Get(angles is null ? "No angle curve" : "Angle (°)"),
            ActualWidth - 12, 10, angles is null ? muted : angleBrush, TextAlignment.Right);
        DrawText(UiText.Format($"{curve.SampleMilliseconds} ms · {curve.Torques.Length} samples"),
            12, plot.Bottom + 30, muted);
        DrawText(UiText.Get("Time (ms)"), Math.Max(64, plot.Right - 75), plot.Bottom + 30, textBrush);
        if (torques is not null && TargetTorque is { } targetTorque)
        {
            var targetY = plot.Bottom - (targetTorque - minimum) / (maximum - minimum) * plot.Height;
            drawing.DrawLine(new Pen(muted, 1) { DashStyle = DashStyles.Dash }, new(plot.Left, targetY), new(plot.Right, targetY));
            DrawText(UiText.Get("Target torque") + $" {targetTorque:0.##}", plot.Left + 8, targetY - 19, muted);
        }
        var sampleWidth = curve.SampleMilliseconds / (double)duration * plot.Width;
        Point SamplePoint(double[] values, int index, double low, double high)
        {
            return new(plot.Left + curve.StartMilliseconds / (double)duration * plot.Width + index * sampleWidth,
                plot.Bottom - (values[index] - low) / (high - low) * plot.Height);
        }
        double Slope(double before, double after)
        {
            // Monotone cubic interpolation passes through every sample without inventing peaks.
            return before * after <= 0 ? 0 : 2 * before * after / (before + after);
        }
        void DrawCurve(double[] values, double low, double high, Brush brush, DashStyle dashStyle)
        {
            var yScale = plot.Height / (high - low);
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                var previous = SamplePoint(values, 0, low, high);
                context.BeginFigure(previous, false, false);
                for (var index = 1; index < values.Length; index++)
                {
                    var point = SamplePoint(values, index, low, high);
                    var change = values[index] - values[index - 1];
                    var startSlope = Slope(index > 1 ? values[index - 1] - values[index - 2] : change, change);
                    var endSlope = Slope(change, index + 1 < values.Length ? values[index + 1] - values[index] : change);
                    context.BezierTo(
                        new(previous.X + sampleWidth / 3, previous.Y - startSlope * yScale / 3),
                        new(point.X - sampleWidth / 3, point.Y + endSlope * yScale / 3),
                        point, true, false);
                    previous = point;
                }
            }
            geometry.Freeze();
            drawing.DrawGeometry(null, new Pen(brush, 2)
            {
                DashStyle = dashStyle,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            }, geometry);
            if (values.Length == 1)
                drawing.DrawEllipse(brush, null, SamplePoint(values, 0, low, high), 3, 3);
        }
        if (torques is not null)
            DrawCurve(torques, minimum, maximum, lineBrush, DashStyles.Solid);
        if (angles is not null)
            DrawCurve(angles, angleMinimum, angleMaximum, angleBrush, DashStyles.Dash);
        if (curve.StartMilliseconds > 0)
            DrawText(UiText.Format($"No samples before {curve.StartMilliseconds} ms"), 12, ActualHeight - 17, muted);

        if (!IsMouseOver)
            return;
        var pointer = Mouse.GetPosition(this);
        if (!plot.Contains(pointer))
            return;
        var samplePosition = ((pointer.X - plot.Left) / plot.Width * duration - curve.StartMilliseconds) / curve.SampleMilliseconds;
        if (samplePosition < 0 || samplePosition > curve.Torques.Length - 1)
            return;
        var selectedIndex = (int)Math.Round(samplePosition);
        var selectedPoint = torques is not null ? SamplePoint(torques, selectedIndex, minimum, maximum)
            : SamplePoint(angles!, selectedIndex, angleMinimum, angleMaximum);
        drawing.DrawLine(new Pen(muted, 0.8) { DashStyle = DashStyles.Dash },
            new(selectedPoint.X, plot.Top), new(selectedPoint.X, plot.Bottom));
        var valuesText = $"{curve.StartMilliseconds + selectedIndex * curve.SampleMilliseconds} ms";
        if (torques is not null)
        {
            drawing.DrawEllipse(lineBrush, new Pen(textBrush, 1.5), selectedPoint, 4, 4);
            valuesText += $"\n{UiText.Get("Torque")} {torques[selectedIndex]:0.##}";
        }
        if (angles is not null)
        {
            drawing.DrawEllipse(angleBrush, new Pen(textBrush, 1.5),
                SamplePoint(angles, selectedIndex, angleMinimum, angleMaximum), 4, 4);
            valuesText += $"\n{UiText.Get("Angle (°)")} {angles[selectedIndex]:0.##}";
        }
        // Show the nearest received sample immediately, not an interpolated measurement.
        var label = new FormattedText(
            valuesText,
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 13, textBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var labelWidth = label.Width + 20;
        var labelHeight = label.Height + 12;
        var labelX = pointer.X + 12;
        if (labelX + labelWidth > plot.Right)
            labelX = pointer.X - labelWidth - 12;
        var labelY = pointer.Y - labelHeight - 12;
        if (labelY < plot.Top)
            labelY = pointer.Y + 12;
        var labelRect = new Rect(
            Math.Clamp(labelX, plot.Left, Math.Max(plot.Left, plot.Right - labelWidth)),
            Math.Clamp(labelY, plot.Top, Math.Max(plot.Top, plot.Bottom - labelHeight)),
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
