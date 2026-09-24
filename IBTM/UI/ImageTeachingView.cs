using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IBTM.Core;
using IBTM.Inspection;

namespace IBTM.UI;

public sealed record CarrierImageTileView(
    CarrierImageTile Metadata,
    BitmapSource Image,
    BoltPoint? Bolt = null)
{
    public AxisPosition? Position => Metadata.IsBarcode ? Metadata.Center : Bolt?.InspectionPosition;

    public string Title => $"{Metadata.HeatSink.GetDescription()} · "
        + (Metadata.IsBarcode ? "Data Matrix" : $"Bolt {Metadata.BoltNumber}");

    public override string ToString()
    {
        return $"FOV {Metadata.Number}";
    }
}

public sealed record ImageRuler(Point Start, Point End)
{
    public double PixelLength => (End - Start).Length;
}

// Drawing and measurement coordinates are original-image pixels, independent of display size.
public sealed class ImageTeachingView : FrameworkElement
{
    private static readonly Pen s_regionPen;
    private static readonly Pen s_rulerPen;
    private static readonly Pen s_crosshairOutlinePen;
    private static readonly Pen s_crosshairPen;

    private Point? _dragStart;
    private Point? _dragEnd;

    public static readonly DependencyProperty SourceProperty;

    public static readonly DependencyProperty SourceRegionProperty;

    public static readonly DependencyProperty SourceOverlayProperty;

    public static readonly DependencyProperty ShowCrosshairProperty;

    public static readonly DependencyProperty RegionCommandProperty;

    public static readonly DependencyProperty IsMeasuringProperty;

    public static readonly DependencyProperty RulerProperty;

    public static readonly DependencyProperty MeasureCommandProperty;

    static ImageTeachingView()
    {
        s_regionPen = CreateFrozenPen(Color.FromRgb(74, 222, 128), 2.5);
        s_rulerPen = CreateFrozenPen(Color.FromRgb(56, 189, 248), 2);
        s_crosshairOutlinePen = CreateFrozenPen(Colors.Black, 3);
        s_crosshairPen = CreateFrozenPen(Color.FromRgb(251, 191, 36), 1);
        SourceProperty = DependencyProperty.Register(
            nameof(Source),
            typeof(BitmapSource),
            typeof(ImageTeachingView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDrawingContextChanged));
        SourceRegionProperty = DependencyProperty.Register(
            nameof(SourceRegion),
            typeof(Rect?),
            typeof(ImageTeachingView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        SourceOverlayProperty = DependencyProperty.Register(
            nameof(SourceOverlay),
            typeof(BitmapSource),
            typeof(ImageTeachingView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        ShowCrosshairProperty = DependencyProperty.Register(
            nameof(ShowCrosshair),
            typeof(bool),
            typeof(ImageTeachingView),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
        RegionCommandProperty = DependencyProperty.Register(
            nameof(RegionCommand),
            typeof(ICommand),
            typeof(ImageTeachingView));
        IsMeasuringProperty = DependencyProperty.Register(
            nameof(IsMeasuring), typeof(bool), typeof(ImageTeachingView),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnDrawingContextChanged));
        RulerProperty = DependencyProperty.Register(
            nameof(Ruler), typeof(ImageRuler), typeof(ImageTeachingView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        MeasureCommandProperty = DependencyProperty.Register(
            nameof(MeasureCommand), typeof(ICommand), typeof(ImageTeachingView));
    }

    public ImageTeachingView()
    {
        Focusable = true;
    }

    public BitmapSource? Source
    {
        get => (BitmapSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public Rect? SourceRegion
    {
        get => (Rect?)GetValue(SourceRegionProperty);
        set => SetValue(SourceRegionProperty, value);
    }

    public BitmapSource? SourceOverlay
    {
        get => (BitmapSource?)GetValue(SourceOverlayProperty);
        set => SetValue(SourceOverlayProperty, value);
    }

    public bool ShowCrosshair
    {
        get => (bool)GetValue(ShowCrosshairProperty);
        set => SetValue(ShowCrosshairProperty, value);
    }

    public ICommand? RegionCommand
    {
        get => (ICommand?)GetValue(RegionCommandProperty);
        set => SetValue(RegionCommandProperty, value);
    }

    public bool IsMeasuring
    {
        get => (bool)GetValue(IsMeasuringProperty);
        set => SetValue(IsMeasuringProperty, value);
    }

    public ImageRuler? Ruler
    {
        get => (ImageRuler?)GetValue(RulerProperty);
        set => SetValue(RulerProperty, value);
    }

    public ICommand? MeasureCommand
    {
        get => (ICommand?)GetValue(MeasureCommandProperty);
        set => SetValue(MeasureCommandProperty, value);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (Source is not { } source || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var fitted = ImageBounds;
        var scale = fitted.Width / source.PixelWidth;
        drawing.DrawImage(source, fitted);
        var displayedRegion = !IsMeasuring && _dragEnd is { } point ? GetRegion(point) : SourceRegion;
        if (displayedRegion is { } region)
        {
            var bounds = new Rect(
                fitted.X + region.X * scale,
                fitted.Y + region.Y * scale,
                region.Width * scale,
                region.Height * scale);
            if (SourceOverlay is not null && _dragStart is null)
                drawing.DrawImage(SourceOverlay, bounds);
            drawing.DrawRectangle(null, s_regionPen, bounds);
        }

        if (IsMeasuring)
        {
            var ruler = _dragStart is { } start && _dragEnd is { } end
                ? new ImageRuler(start, end)
                : Ruler;
            if (ruler is not null)
            {
                var first = new Point(fitted.X + ruler.Start.X * scale, fitted.Y + ruler.Start.Y * scale);
                var last = new Point(fitted.X + ruler.End.X * scale, fitted.Y + ruler.End.Y * scale);
                drawing.DrawLine(s_crosshairOutlinePen, first, last);
                drawing.DrawLine(s_rulerPen, first, last);
                drawing.DrawEllipse(Brushes.Black, s_rulerPen, first, 4, 4);
                drawing.DrawEllipse(Brushes.Black, s_rulerPen, last, 4, 4);
            }
        }

        if (!ShowCrosshair)
            return;

        var center = new Point(fitted.X + fitted.Width / 2, fitted.Y + fitted.Height / 2);
        var arm = Math.Min(24, Math.Min(fitted.Width, fitted.Height) / 2);
        var left = new Point(center.X - arm, center.Y);
        var right = new Point(center.X + arm, center.Y);
        var top = new Point(center.X, center.Y - arm);
        var bottom = new Point(center.X, center.Y + arm);
        drawing.DrawLine(s_crosshairOutlinePen, left, right);
        drawing.DrawLine(s_crosshairOutlinePen, top, bottom);
        drawing.DrawLine(s_crosshairPen, left, right);
        drawing.DrawLine(s_crosshairPen, top, bottom);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var mouse = e.GetPosition(this);
        if (Source is null
            || ActualWidth <= 0 || ActualHeight <= 0
            || !ImageBounds.Contains(mouse))
            return;
        var point = GetImagePoint(mouse);
        if (IsMeasuring
            ? MeasureCommand?.CanExecute(new ImageRuler(point, point)) != true
            : RegionCommand?.CanExecute(Rect.Empty) != true)
            return;

        Focus();
        _dragStart = point;
        _dragEnd = point;
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragStart is null || Source is null)
            return;

        _dragEnd = GetImagePoint(e.GetPosition(this));
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragStart is not { } start || Source is null)
            return;
        var end = GetImagePoint(e.GetPosition(this));
        CancelDrag();
        if (IsMeasuring)
        {
            var ruler = new ImageRuler(start, end);
            if (ruler.PixelLength >= 1 && MeasureCommand?.CanExecute(ruler) == true)
                MeasureCommand.Execute(ruler);
        }
        else
        {
            var region = GetRegion(end);
            if (region is { Width: > 0, Height: > 0 } && RegionCommand?.CanExecute(region) == true)
                RegionCommand.Execute(region);
        }
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Escape || _dragStart is null)
            return;

        CancelDrag();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragStart = null;
        _dragEnd = null;
        InvalidateVisual();
    }

    private void CancelDrag()
    {
        _dragStart = null;
        _dragEnd = null;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        InvalidateVisual();
    }

    private Rect ImageBounds
    {
        get
        {
            var source = Source!;
            var scale = Math.Min(ActualWidth / source.PixelWidth, ActualHeight / source.PixelHeight);
            var width = source.PixelWidth * scale;
            var height = source.PixelHeight * scale;
            return new Rect((ActualWidth - width) / 2, (ActualHeight - height) / 2, width, height);
        }
    }

    private Point GetImagePoint(Point screen)
    {
        var source = Source!;
        var fitted = ImageBounds;
        var scale = fitted.Width / source.PixelWidth;
        return new Point(
            Math.Clamp((screen.X - fitted.X) / scale, 0, source.PixelWidth),
            Math.Clamp((screen.Y - fitted.Y) / scale, 0, source.PixelHeight));
    }

    private Rect GetRegion(Point point)
    {
        var source = Source!;
        var halfSize = Math.Max(
            Math.Abs(point.X - source.PixelWidth / 2.0),
            Math.Abs(point.Y - source.PixelHeight / 2.0));
        var region = PixelRegion.CenteredSquare(
            source.PixelWidth, source.PixelHeight, (int)Math.Ceiling(halfSize) * 2);
        return new Rect(region.X, region.Y, region.Width, region.Height);
    }

    private static void OnDrawingContextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        ((ImageTeachingView)sender).CancelDrag();
    }

    private static Pen CreateFrozenPen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness);
        pen.Freeze();
        return pen;
    }
}
