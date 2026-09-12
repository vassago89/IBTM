using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IBTM.Inspection;

namespace IBTM.UI;

public sealed record CarrierImageTileView(
    CarrierImageTile Metadata,
    BitmapSource Image)
{
    public override string ToString()
    {
        return $"FOV {Metadata.Number}";
    }
}

// Single camera frame with display-only ROI, prediction overlay and crosshair.
// Coordinates passed to RegionCommand are original-image pixels.
public sealed class ImageTeachingView : FrameworkElement
{
    private static readonly Pen RegionPen = FrozenPen(Color.FromRgb(74, 222, 128), 2.5);
    private static readonly Pen CrosshairOutlinePen = FrozenPen(Colors.Black, 3);
    private static readonly Pen CrosshairPen = FrozenPen(Color.FromRgb(251, 191, 36), 1);

    private Point? _regionStart;
    private Rect? _draftRegion;

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source),
        typeof(BitmapSource),
        typeof(ImageTeachingView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSourceChanged));

    public static readonly DependencyProperty SourceRegionProperty = DependencyProperty.Register(
        nameof(SourceRegion),
        typeof(Rect?),
        typeof(ImageTeachingView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SourceOverlayProperty = DependencyProperty.Register(
        nameof(SourceOverlay),
        typeof(BitmapSource),
        typeof(ImageTeachingView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowCrosshairProperty = DependencyProperty.Register(
        nameof(ShowCrosshair),
        typeof(bool),
        typeof(ImageTeachingView),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RegionCommandProperty = DependencyProperty.Register(
        nameof(RegionCommand),
        typeof(ICommand),
        typeof(ImageTeachingView));

    public ImageTeachingView()
    {
        Focusable = true;
    }

    public BitmapSource? Source
    {
        get
        {
            return (BitmapSource?)GetValue(SourceProperty);
        }
        set
        {
            SetValue(SourceProperty, value);
        }
    }

    public Rect? SourceRegion
    {
        get
        {
            return (Rect?)GetValue(SourceRegionProperty);
        }
        set
        {
            SetValue(SourceRegionProperty, value);
        }
    }

    public BitmapSource? SourceOverlay
    {
        get
        {
            return (BitmapSource?)GetValue(SourceOverlayProperty);
        }
        set
        {
            SetValue(SourceOverlayProperty, value);
        }
    }

    public bool ShowCrosshair
    {
        get
        {
            return (bool)GetValue(ShowCrosshairProperty);
        }
        set
        {
            SetValue(ShowCrosshairProperty, value);
        }
    }

    public ICommand? RegionCommand
    {
        get
        {
            return (ICommand?)GetValue(RegionCommandProperty);
        }
        set
        {
            SetValue(RegionCommandProperty, value);
        }
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (Source is not { } source || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var fitted = ImageBounds();
        var scale = fitted.Width / source.PixelWidth;
        drawing.DrawImage(source, fitted);
        if (SourceRegion is { } region)
        {
            var bounds = new Rect(
                fitted.X + region.X * scale,
                fitted.Y + region.Y * scale,
                region.Width * scale,
                region.Height * scale);
            if (SourceOverlay is not null)
                drawing.DrawImage(SourceOverlay, bounds);
            drawing.DrawRectangle(null, RegionPen, bounds);
        }

        if (_draftRegion is { } draft)
        {
            drawing.DrawRectangle(null, RegionPen, new Rect(
                fitted.X + draft.X * scale,
                fitted.Y + draft.Y * scale,
                draft.Width * scale,
                draft.Height * scale));
        }

        if (!ShowCrosshair)
            return;

        var center = new Point(fitted.X + fitted.Width / 2, fitted.Y + fitted.Height / 2);
        var arm = Math.Min(24, Math.Min(fitted.Width, fitted.Height) / 2);
        var left = new Point(center.X - arm, center.Y);
        var right = new Point(center.X + arm, center.Y);
        var top = new Point(center.X, center.Y - arm);
        var bottom = new Point(center.X, center.Y + arm);
        drawing.DrawLine(CrosshairOutlinePen, left, right);
        drawing.DrawLine(CrosshairOutlinePen, top, bottom);
        drawing.DrawLine(CrosshairPen, left, right);
        drawing.DrawLine(CrosshairPen, top, bottom);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var mouse = e.GetPosition(this);
        if (Source is null
            || ActualWidth <= 0 || ActualHeight <= 0
            || !ImageBounds().Contains(mouse)
            || RegionCommand?.CanExecute(Rect.Empty) != true)
            return;

        Focus();
        _regionStart = ImagePoint(mouse);
        _draftRegion = new Rect(_regionStart.Value, _regionStart.Value);
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_regionStart is not { } start || Source is null)
            return;

        _draftRegion = new Rect(start, ImagePoint(e.GetPosition(this)));
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var region = _draftRegion;
        CancelRegion();
        if (region is { Width: > 0, Height: > 0 }
            && RegionCommand?.CanExecute(region.Value) == true)
            RegionCommand.Execute(region.Value);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Escape || _regionStart is null)
            return;

        CancelRegion();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _regionStart = null;
        _draftRegion = null;
        InvalidateVisual();
    }

    private void CancelRegion()
    {
        _regionStart = null;
        _draftRegion = null;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        InvalidateVisual();
    }

    private Rect ImageBounds()
    {
        var source = Source!;
        var scale = Math.Min(ActualWidth / source.PixelWidth, ActualHeight / source.PixelHeight);
        var width = source.PixelWidth * scale;
        var height = source.PixelHeight * scale;
        return new Rect((ActualWidth - width) / 2, (ActualHeight - height) / 2, width, height);
    }

    private Point ImagePoint(Point screen)
    {
        var source = Source!;
        var fitted = ImageBounds();
        var scale = fitted.Width / source.PixelWidth;
        return new Point(
            Math.Clamp((screen.X - fitted.X) / scale, 0, source.PixelWidth),
            Math.Clamp((screen.Y - fitted.Y) / scale, 0, source.PixelHeight));
    }

    private static void OnSourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        ((ImageTeachingView)sender).CancelRegion();
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness);
        pen.Freeze();
        return pen;
    }
}
