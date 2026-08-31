using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IBTM.Core;

namespace IBTM.UI;

public sealed record CarrierImageTileView(
    int Number,
    AxisPos Center,
    BitmapSource Image);

public sealed record ImageMarker(
    double X,
    double Y,
    string Label,
    bool Selected = false);

public sealed class ImageTeachingView : FrameworkElement
{
    private double _zoom = 1;
    private Vector _pan;
    private Point? _panStart;

    public static readonly DependencyProperty SourceProperty = Register(
        nameof(Source),
        typeof(BitmapSource));
    public static readonly DependencyProperty TilesProperty = Register(
        nameof(Tiles),
        typeof(IReadOnlyList<CarrierImageTileView>));
    public static readonly DependencyProperty MillimetersPerPixelProperty = Register(
        nameof(MillimetersPerPixel),
        typeof(double),
        0.05);
    public static readonly DependencyProperty MarkersProperty = Register(
        nameof(Markers),
        typeof(IReadOnlyList<ImageMarker>));
    public static readonly DependencyProperty CameraFieldOfViewProperty = Register(
        nameof(CameraFieldOfView),
        typeof(Rect?));
    public static readonly DependencyProperty HoverPositionTextProperty =
        DependencyProperty.Register(
            nameof(HoverPositionText),
            typeof(string),
            typeof(ImageTeachingView));
    public static readonly DependencyProperty CoordinateOriginProperty =
        DependencyProperty.Register(
            nameof(CoordinateOrigin),
            typeof(Point?),
            typeof(ImageTeachingView));
    public static readonly DependencyProperty ClickCommandProperty =
        DependencyProperty.Register(
            nameof(ClickCommand),
            typeof(ICommand),
            typeof(ImageTeachingView));

    public BitmapSource? Source
    {
        get => (BitmapSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public IReadOnlyList<CarrierImageTileView>? Tiles
    {
        get => (IReadOnlyList<CarrierImageTileView>?)GetValue(TilesProperty);
        set => SetValue(TilesProperty, value);
    }

    public double MillimetersPerPixel
    {
        get => (double)GetValue(MillimetersPerPixelProperty);
        set => SetValue(MillimetersPerPixelProperty, value);
    }

    public IReadOnlyList<ImageMarker>? Markers
    {
        get => (IReadOnlyList<ImageMarker>?)GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    public Rect? CameraFieldOfView
    {
        get => (Rect?)GetValue(CameraFieldOfViewProperty);
        set => SetValue(CameraFieldOfViewProperty, value);
    }

    public string? HoverPositionText
    {
        get => (string?)GetValue(HoverPositionTextProperty);
        set => SetValue(HoverPositionTextProperty, value);
    }

    public Point? CoordinateOrigin
    {
        get => (Point?)GetValue(CoordinateOriginProperty);
        set => SetValue(CoordinateOriginProperty, value);
    }

    public ICommand? ClickCommand
    {
        get => (ICommand?)GetValue(ClickCommandProperty);
        set => SetValue(ClickCommandProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (Source is not null)
        {
            drawingContext.DrawImage(
                Source,
                Fit(Source.PixelWidth, Source.PixelHeight));
            return;
        }

        if (!HasMap)
        {
            return;
        }

        var layout = MapLayout();
        drawingContext.PushClip(new RectangleGeometry(layout.Screen));
        foreach (var tile in Tiles!.OrderBy(tile => tile.Number))
        {
            var world = TileWorldRect(tile);
            drawingContext.DrawImage(
                tile.Image,
                WorldRect(
                    world.X,
                    world.Y,
                    world.Width,
                    world.Height,
                    layout));
        }

        if (CameraFieldOfView is { } camera)
        {
            DrawCameraFieldOfView(drawingContext, camera, layout);
        }

        foreach (var marker in Markers ?? [])
        {
            DrawMarker(
                drawingContext,
                WorldPoint(marker.X, marker.Y, layout),
                marker);
        }
        drawingContext.Pop();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!HasMap || ClickCommand is null)
        {
            return;
        }

        var layout = MapLayout();
        var click = e.GetPosition(this);
        if (!layout.Screen.Contains(click))
        {
            return;
        }

        var position = ScreenToWorld(click, layout);
        if (!Tiles!.Any(tile => TileWorldRect(tile).Contains(position)))
        {
            return;
        }

        if (ClickCommand.CanExecute(position))
        {
            ClickCommand.Execute(position);
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!HasMap)
        {
            return;
        }

        var mouse = e.GetPosition(this);
        var before = MapLayout();
        if (!before.Screen.Contains(mouse))
        {
            return;
        }

        var world = ScreenToWorld(mouse, before);
        _zoom = Math.Clamp(
            _zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2),
            1,
            20);
        var after = MapLayout();
        var moved = WorldPoint(world.X, world.Y, after);
        _pan += mouse - moved;
        InvalidateVisual();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (!HasMap)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            _zoom = 1;
            _pan = default;
            InvalidateVisual();
            return;
        }

        _panStart = e.GetPosition(this);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var current = e.GetPosition(this);
        if (_panStart is { } previous)
        {
            _pan += current - previous;
            _panStart = current;
            InvalidateVisual();
        }

        var layout = HasMap ? MapLayout() : null;
        HoverPositionText = layout is not null
            && layout.Screen.Contains(current)
            && CoordinateOrigin is { } origin
            ? PositionText(
                ScreenToWorld(current, layout),
                origin)
            : null;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        HoverPositionText = null;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        _panStart = null;
        ReleaseMouseCapture();
    }

    private MapView MapLayout()
    {
        var tiles = Tiles!;
        var left = tiles.Min(tile => TileWorldRect(tile).Left);
        var top = tiles.Min(tile => TileWorldRect(tile).Top);
        var right = tiles.Max(tile => TileWorldRect(tile).Right);
        var bottom = tiles.Max(tile => TileWorldRect(tile).Bottom);
        var world = new Rect(left, top, right - left, bottom - top);
        var fitted = Fit(world.Width, world.Height);
        var width = fitted.Width * _zoom;
        var height = fitted.Height * _zoom;
        var screen = new Rect(
            fitted.X - ((width - fitted.Width) / 2) + _pan.X,
            fitted.Y - ((height - fitted.Height) / 2) + _pan.Y,
            width,
            height);
        return new MapView(world, screen, screen.Width / world.Width);
    }

    private Rect TileWorldRect(CarrierImageTileView tile) => new(
        tile.Center.X
            - (tile.Image.PixelWidth * MillimetersPerPixel / 2),
        tile.Center.Y
            - (tile.Image.PixelHeight * MillimetersPerPixel / 2),
        tile.Image.PixelWidth * MillimetersPerPixel,
        tile.Image.PixelHeight * MillimetersPerPixel);

    private bool HasMap =>
        Source is null
        && Tiles is { Count: > 0 }
        && MillimetersPerPixel > 0;

    private Rect Fit(double width, double height)
    {
        var scale = Math.Min(ActualWidth / width, ActualHeight / height);
        var fittedWidth = width * scale;
        var fittedHeight = height * scale;
        return new Rect(
            (ActualWidth - fittedWidth) / 2,
            (ActualHeight - fittedHeight) / 2,
            fittedWidth,
            fittedHeight);
    }

    private static Rect WorldRect(
        double x,
        double y,
        double width,
        double height,
        MapView layout) => new(
            layout.Screen.Left + ((x - layout.World.Left) * layout.Scale),
            layout.Screen.Top + ((y - layout.World.Top) * layout.Scale),
            width * layout.Scale,
            height * layout.Scale);

    private static Point WorldPoint(
        double x,
        double y,
        MapView layout) => new(
            layout.Screen.Left + ((x - layout.World.Left) * layout.Scale),
            layout.Screen.Top + ((y - layout.World.Top) * layout.Scale));

    private static Point ScreenToWorld(Point point, MapView layout) => new(
        layout.World.Left
            + ((point.X - layout.Screen.Left) / layout.Scale),
        layout.World.Top
            + ((point.Y - layout.Screen.Top) / layout.Scale));

    private static void DrawCameraFieldOfView(
        DrawingContext drawingContext,
        Rect fieldOfView,
        MapView layout)
    {
        var color = Color.FromRgb(251, 191, 36);
        var pen = new Pen(new SolidColorBrush(color), 2)
        {
            DashStyle = DashStyles.Dash,
        };
        var screen = WorldRect(
            fieldOfView.X,
            fieldOfView.Y,
            fieldOfView.Width,
            fieldOfView.Height,
            layout);
        var center = new Point(
            screen.Left + (screen.Width / 2),
            screen.Top + (screen.Height / 2));
        drawingContext.DrawRectangle(
            new SolidColorBrush(Color.FromArgb(18, color.R, color.G, color.B)),
            pen,
            screen);
        drawingContext.DrawLine(
            pen,
            new Point(center.X - 10, center.Y),
            new Point(center.X + 10, center.Y));
        drawingContext.DrawLine(
            pen,
            new Point(center.X, center.Y - 10),
            new Point(center.X, center.Y + 10));
    }

    private static string PositionText(Point position, Point origin) =>
        FormattableString.Invariant(
            $"X {position.X - origin.X:F3}   Y {position.Y - origin.Y:F3}");

    private static void DrawMarker(
        DrawingContext drawingContext,
        Point point,
        ImageMarker marker)
    {
        var color = marker.Selected
            ? Color.FromRgb(74, 222, 128)
            : Color.FromRgb(56, 189, 248);
        var pen = new Pen(new SolidColorBrush(color), marker.Selected ? 2.5 : 1.5);
        drawingContext.DrawEllipse(Brushes.Transparent, pen, point, 7, 7);
        drawingContext.DrawLine(
            pen,
            new Point(point.X - 10, point.Y),
            new Point(point.X + 10, point.Y));
        drawingContext.DrawLine(
            pen,
            new Point(point.X, point.Y - 10),
            new Point(point.X, point.Y + 10));

        var text = new FormattedText(
            marker.Label,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            11,
            new SolidColorBrush(color),
            1.0);
        drawingContext.DrawText(text, new Point(point.X + 11, point.Y - 9));
    }

    private static DependencyProperty Register(
        string name,
        Type type,
        object? defaultValue = null) =>
        DependencyProperty.Register(
            name,
            type,
            typeof(ImageTeachingView),
            new FrameworkPropertyMetadata(
                defaultValue,
                FrameworkPropertyMetadataOptions.AffectsRender));

    private sealed record MapView(Rect World, Rect Screen, double Scale);
}
