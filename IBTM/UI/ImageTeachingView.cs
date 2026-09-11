using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IBTM.Core;

namespace IBTM.UI;

public sealed record CarrierImageTileView(
    int Number,
    AxisPosition Center,
    BitmapSource Image,
    PixelRegion? Region = null,
    int? BoltNumber = null,
    HeatSinkSlot HeatSink = HeatSinkSlot.HeatSink1,
    bool IsBarcode = false)
{
    public override string ToString()
    {
        return $"FOV {Number}";
    }
}

public sealed record ImageMarker(double X, double Y, string Label, bool Selected = false);

public sealed record ImageRegion(Rect Bounds, bool Selected = false, string Label = "");

public sealed class ImageTeachingView : FrameworkElement
{
    private const double MinimumZoom = 1;
    private const double MaximumZoom = 20;
    private const double ZoomStep = 1.2;

    private static readonly SolidColorBrush CameraFill = FrozenBrush(Color.FromArgb(18, 251, 191, 36));
    private static readonly SolidColorBrush CameraStroke = FrozenBrush(Color.FromRgb(251, 191, 36));
    private static readonly SolidColorBrush MarkerStroke = FrozenBrush(Color.FromRgb(56, 189, 248));
    private static readonly SolidColorBrush SelectedMarkerStroke = FrozenBrush(
        Color.FromRgb(74, 222, 128));
    private static readonly Pen CameraPen = FrozenPen(CameraStroke, 2, DashStyles.Dash);
    private static readonly Pen MarkerPen = FrozenPen(MarkerStroke, 1.5);
    private static readonly Pen SelectedMarkerPen = FrozenPen(SelectedMarkerStroke, 2.5);
    private static readonly Pen CrosshairOutlinePen = FrozenPen(Brushes.Black, 3);
    private static readonly Pen CrosshairPen = FrozenPen(CameraStroke, 1);
    private static readonly Typeface MarkerTypeface = new("Segoe UI");

    private readonly DrawingVisual _mapVisual = new();
    private readonly DrawingVisual _markerVisual = new();
    private readonly DrawingVisual _cameraVisual = new();
    private readonly VisualCollection _visuals;

    private double _zoom = MinimumZoom;
    private Vector _pan;
    private Point? _panStart;
    private Point? _regionStart;
    private Rect? _draftRegion;
    private (CarrierImageTileView Tile, Rect World)[] _tileLayout = [];
    private Rect? _world;
    private MapView? _layout;
    private RectangleGeometry? _screenClip;

    public static readonly DependencyProperty SourceProperty = Register(
        nameof(Source),
        typeof(BitmapSource),
        OnMapChanged);
    public static readonly DependencyProperty TilesProperty = Register(
        nameof(Tiles),
        typeof(IReadOnlyList<CarrierImageTileView>),
        OnMapChanged);
    public static readonly DependencyProperty MillimetersPerPixelProperty = Register(
        nameof(MillimetersPerPixel),
        typeof(double),
        OnMapChanged,
        Recipe.DefaultCarrierImageMillimetersPerPixel);
    public static readonly DependencyProperty MarkersProperty = Register(
        nameof(Markers),
        typeof(IReadOnlyList<ImageMarker>),
        OnMarkersChanged);
    public static readonly DependencyProperty CameraFieldOfViewProperty = Register(
        nameof(CameraFieldOfView),
        typeof(Rect?),
        OnCameraChanged);
    public static readonly DependencyProperty HoverPositionTextProperty = DependencyProperty.Register(
        nameof(HoverPositionText),
        typeof(string),
        typeof(ImageTeachingView));
    public static readonly DependencyProperty CoordinateOriginProperty = DependencyProperty.Register(
        nameof(CoordinateOrigin),
        typeof(Point?),
        typeof(ImageTeachingView));
    public static readonly DependencyProperty ClickCommandProperty = DependencyProperty.Register(
        nameof(ClickCommand),
        typeof(ICommand),
        typeof(ImageTeachingView));
    public static readonly DependencyProperty RegionCommandProperty = DependencyProperty.Register(
        nameof(RegionCommand),
        typeof(ICommand),
        typeof(ImageTeachingView));
    public static readonly DependencyProperty RegionsProperty = Register(
        nameof(Regions),
        typeof(IReadOnlyList<ImageRegion>),
        OnMarkersChanged);
    public static readonly DependencyProperty SourceRegionProperty = Register(
        nameof(SourceRegion),
        typeof(Rect?),
        OnMapChanged);
    public static readonly DependencyProperty SourceOverlayProperty = Register(
        nameof(SourceOverlay),
        typeof(BitmapSource),
        OnMapChanged);
    public static readonly DependencyProperty ShowCrosshairProperty = Register(
        nameof(ShowCrosshair),
        typeof(bool),
        OnCameraChanged,
        true);

    public ImageTeachingView()
    {
        Focusable = true;
        _visuals = new VisualCollection(this)
        {
            _mapVisual,
            _markerVisual,
            _cameraVisual,
        };
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

    public IReadOnlyList<CarrierImageTileView>? Tiles
    {
        get
        {
            return (IReadOnlyList<CarrierImageTileView>?)GetValue(TilesProperty);
        }

        set
        {
            SetValue(TilesProperty, value);
        }
    }

    public double MillimetersPerPixel
    {
        get
        {
            return (double)GetValue(MillimetersPerPixelProperty);
        }

        set
        {
            SetValue(MillimetersPerPixelProperty, value);
        }
    }

    public IReadOnlyList<ImageMarker>? Markers
    {
        get
        {
            return (IReadOnlyList<ImageMarker>?)GetValue(MarkersProperty);
        }

        set
        {
            SetValue(MarkersProperty, value);
        }
    }

    public Rect? CameraFieldOfView
    {
        get
        {
            return (Rect?)GetValue(CameraFieldOfViewProperty);
        }

        set
        {
            SetValue(CameraFieldOfViewProperty, value);
        }
    }

    public string? HoverPositionText
    {
        get
        {
            return (string?)GetValue(HoverPositionTextProperty);
        }

        set
        {
            SetValue(HoverPositionTextProperty, value);
        }
    }

    public Point? CoordinateOrigin
    {
        get
        {
            return (Point?)GetValue(CoordinateOriginProperty);
        }

        set
        {
            SetValue(CoordinateOriginProperty, value);
        }
    }

    public ICommand? ClickCommand
    {
        get
        {
            return (ICommand?)GetValue(ClickCommandProperty);
        }

        set
        {
            SetValue(ClickCommandProperty, value);
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

    public IReadOnlyList<ImageRegion>? Regions
    {
        get
        {
            return (IReadOnlyList<ImageRegion>?)GetValue(RegionsProperty);
        }

        set
        {
            SetValue(RegionsProperty, value);
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

    protected override int VisualChildrenCount
    {
        get
        {
            return _visuals.Count;
        }
    }

    protected override Visual GetVisualChild(int index)
    {
        return _visuals[index];
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        RefreshVisuals();
    }

    private void DrawMap()
    {
        using var drawingContext = _mapVisual.RenderOpen();
        if (Source is not null)
        {
            var fitted = Fit(Source.PixelWidth, Source.PixelHeight);
            drawingContext.DrawImage(Source, fitted);
            if (SourceRegion is { } region)
            {
                var scale = fitted.Width / Source.PixelWidth;
                var bounds = new Rect(
                    fitted.X + region.X * scale,
                    fitted.Y + region.Y * scale,
                    region.Width * scale,
                    region.Height * scale);
                if (SourceOverlay is not null)
                    drawingContext.DrawImage(SourceOverlay, bounds);
                drawingContext.DrawRectangle(null, SelectedMarkerPen, bounds);
            }

            return;
        }

        if (_layout is not { } layout)
        {
            return;
        }

        drawingContext.PushClip(_screenClip!);
        foreach (var (tile, world) in _tileLayout)
        {
            drawingContext.DrawImage(
                tile.Image,
                WorldRect(world.X, world.Y, world.Width, world.Height, layout));
        }

        drawingContext.Pop();
    }

    private void DrawMarkers()
    {
        using var drawingContext = _markerVisual.RenderOpen();
        if (Source is not null)
        {
            if (_draftRegion is { } sourceDraft)
            {
                var fitted = Fit(Source.PixelWidth, Source.PixelHeight);
                var scale = fitted.Width / Source.PixelWidth;
                drawingContext.DrawRectangle(
                    null,
                    SelectedMarkerPen,
                    new Rect(fitted.X + sourceDraft.X * scale, fitted.Y + sourceDraft.Y * scale,
                        sourceDraft.Width * scale, sourceDraft.Height * scale));
            }
            return;
        }
        if (_layout is not { } layout)
        {
            return;
        }

        drawingContext.PushClip(_screenClip!);
        foreach (var marker in Markers ?? [])
        {
            DrawMarker(drawingContext, WorldPoint(marker.X, marker.Y, layout), marker);
        }

        foreach (var region in Regions ?? [])
        {
            DrawRegion(drawingContext, region.Bounds, layout, region.Selected);
            if (region.Label.Length == 0)
                continue;
            var corner = WorldPoint(region.Bounds.X, region.Bounds.Y, layout);
            var label = new FormattedText(
                region.Label,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                MarkerTypeface,
                13,
                region.Selected ? SelectedMarkerStroke : MarkerStroke,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawingContext.DrawText(label, new Point(corner.X + 5, corner.Y + 4));
        }

        if (_draftRegion is { } draft)
            DrawRegion(drawingContext, draft, layout, true);
        drawingContext.Pop();
    }

    private static void DrawRegion(DrawingContext drawing, Rect region, MapView layout, bool selected)
    {
        drawing.DrawRectangle(
            null,
            selected ? SelectedMarkerPen : MarkerPen,
            WorldRect(region.X, region.Y, region.Width, region.Height, layout));
    }

    private void DrawCamera()
    {
        using var drawingContext = _cameraVisual.RenderOpen();
        if (Source is { } source)
        {
            if (!ShowCrosshair)
                return;
            var fitted = Fit(source.PixelWidth, source.PixelHeight);
            var center = new Point(fitted.X + fitted.Width / 2, fitted.Y + fitted.Height / 2);
            var arm = Math.Min(24, Math.Min(fitted.Width, fitted.Height) / 2);
            var left = new Point(center.X - arm, center.Y);
            var right = new Point(center.X + arm, center.Y);
            var top = new Point(center.X, center.Y - arm);
            var bottom = new Point(center.X, center.Y + arm);
            drawingContext.DrawLine(CrosshairOutlinePen, left, right);
            drawingContext.DrawLine(CrosshairOutlinePen, top, bottom);
            drawingContext.DrawLine(CrosshairPen, left, right);
            drawingContext.DrawLine(CrosshairPen, top, bottom);
            return;
        }
        if (_layout is not { } layout || CameraFieldOfView is not { } camera)
        {
            return;
        }

        drawingContext.PushClip(_screenClip!);
        DrawCameraFieldOfView(drawingContext, camera, layout);
        drawingContext.Pop();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Source is not null)
        {
            var fitted = Fit(Source.PixelWidth, Source.PixelHeight);
            if (fitted.Contains(e.GetPosition(this)) && RegionCommand?.CanExecute(Rect.Empty) == true)
            {
                Focus();
                _regionStart = SourcePoint(e.GetPosition(this));
                _draftRegion = new Rect(_regionStart.Value, _regionStart.Value);
                CaptureMouse();
                e.Handled = true;
            }
            return;
        }
        if (_layout is not { } layout)
        {
            return;
        }

        var click = e.GetPosition(this);
        if (!layout.Screen.Contains(click))
        {
            return;
        }

        var position = ScreenToWorld(click, layout);
        var onTile = false;
        foreach (var tile in _tileLayout)
        {
            if (!tile.World.Contains(position))
            {
                continue;
            }

            onTile = true;
            break;
        }

        if (!onTile)
        {
            return;
        }

        if (RegionCommand?.CanExecute(Rect.Empty) == true)
        {
            Focus();
            _regionStart = position;
            _draftRegion = new Rect(position, position);
            CaptureMouse();
            e.Handled = true;
        }
        else if (ClickCommand?.CanExecute(position) == true)
        {
            ClickCommand.Execute(position);
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_regionStart is not null || _layout is not { } before)
        {
            return;
        }

        var mouse = e.GetPosition(this);
        if (!before.Screen.Contains(mouse))
        {
            return;
        }

        var world = ScreenToWorld(mouse, before);
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? ZoomStep : 1 / ZoomStep), MinimumZoom, MaximumZoom);
        UpdateMapLayout();
        var after = _layout!;
        var moved = WorldPoint(world.X, world.Y, after);
        _pan += mouse - moved;
        RefreshVisuals();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (_layout is null)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            _zoom = MinimumZoom;
            _pan = default;
            RefreshVisuals();
            return;
        }

        _panStart = e.GetPosition(this);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var current = e.GetPosition(this);
        if (_regionStart is { } sourceStart && Source is not null)
        {
            _draftRegion = new Rect(sourceStart, SourcePoint(current));
            DrawMarkers();
        }
        else if (_regionStart is { } start && _layout is { } regionLayout)
        {
            _draftRegion = new Rect(start, ScreenToWorld(current, regionLayout));
            DrawMarkers();
        }

        if (_panStart is { } previous)
        {
            _pan += current - previous;
            _panStart = current;
            RefreshVisuals();
        }

        var layout = _layout;
        HoverPositionText = layout is not null
            && layout.Screen.Contains(current)
            && CoordinateOrigin is { } origin
            ? PositionText(ScreenToWorld(current, layout), origin)
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
        DrawMarkers();
    }

    private void CancelRegion()
    {
        _regionStart = null;
        _draftRegion = null;
        if (_panStart is null)
            ReleaseMouseCapture();
        DrawMarkers();
    }

    private Point SourcePoint(Point screen)
    {
        var source = Source!;
        var fitted = Fit(source.PixelWidth, source.PixelHeight);
        var scale = fitted.Width / source.PixelWidth;
        return new Point(
            Math.Clamp((screen.X - fitted.X) / scale, 0, source.PixelWidth),
            Math.Clamp((screen.Y - fitted.Y) / scale, 0, source.PixelHeight));
    }

    private void RebuildMap()
    {
        CancelRegion();
        if (!HasMap)
        {
            _tileLayout = [];
            _world = null;
            RefreshVisuals();
            return;
        }

        var ordered = new CarrierImageTileView[Tiles!.Count];
        for (var index = 0; index < ordered.Length; index++)
        {
            ordered[index] = Tiles[index];
        }

        Array.Sort(ordered, static (left, right) => left.Number.CompareTo(right.Number));

        _tileLayout = new (CarrierImageTileView, Rect)[ordered.Length];
        for (var index = 0; index < ordered.Length; index++)
        {
            var tile = ordered[index];
            _tileLayout[index] = (tile, TileWorldRect(tile));
        }

        var left = _tileLayout[0].World.Left;
        var top = _tileLayout[0].World.Top;
        var right = _tileLayout[0].World.Right;
        var bottom = _tileLayout[0].World.Bottom;
        for (var index = 1; index < _tileLayout.Length; index++)
        {
            var tile = _tileLayout[index].World;
            left = Math.Min(left, tile.Left);
            top = Math.Min(top, tile.Top);
            right = Math.Max(right, tile.Right);
            bottom = Math.Max(bottom, tile.Bottom);
        }

        _world = new Rect(left, top, right - left, bottom - top);
        RefreshVisuals();
    }

    private void RefreshVisuals()
    {
        UpdateMapLayout();
        DrawMap();
        DrawMarkers();
        DrawCamera();
    }

    private void UpdateMapLayout()
    {
        if (_world is not { } world)
        {
            _layout = null;
            _screenClip = null;
            return;
        }

        var fitted = Fit(world.Width, world.Height);
        var width = fitted.Width * _zoom;
        var height = fitted.Height * _zoom;
        var screen = new Rect(
            fitted.X - ((width - fitted.Width) / 2) + _pan.X,
            fitted.Y - ((height - fitted.Height) / 2) + _pan.Y,
            width,
            height);
        _layout = new MapView(world, screen, screen.Width / world.Width);
        _screenClip = new RectangleGeometry(screen);
        _screenClip.Freeze();
    }

    private Rect TileWorldRect(CarrierImageTileView tile)
    {
        return new(
            tile.Center.X - (tile.Image.PixelWidth * MillimetersPerPixel / 2),
            tile.Center.Y - (tile.Image.PixelHeight * MillimetersPerPixel / 2),
            tile.Image.PixelWidth * MillimetersPerPixel,
            tile.Image.PixelHeight * MillimetersPerPixel);
    }

    private bool HasMap
    {
        get
        {
            return Source is null && Tiles is { Count: > 0 } && MillimetersPerPixel > 0;
        }
    }

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

    private static Rect WorldRect(double x, double y, double width, double height, MapView layout)
    {
        return new(
            layout.Screen.Left + ((x - layout.World.Left) * layout.Scale),
            layout.Screen.Top + ((y - layout.World.Top) * layout.Scale),
            width * layout.Scale,
            height * layout.Scale);
    }

    private static Point WorldPoint(double x, double y, MapView layout)
    {
        return new(
            layout.Screen.Left + ((x - layout.World.Left) * layout.Scale),
            layout.Screen.Top + ((y - layout.World.Top) * layout.Scale));
    }

    private static Point ScreenToWorld(Point point, MapView layout)
    {
        return new(
            layout.World.Left + ((point.X - layout.Screen.Left) / layout.Scale),
            layout.World.Top + ((point.Y - layout.Screen.Top) / layout.Scale));
    }

    private static void DrawCameraFieldOfView(
        DrawingContext drawingContext,
        Rect fieldOfView,
        MapView layout)
    {
        var screen = WorldRect(
            fieldOfView.X,
            fieldOfView.Y,
            fieldOfView.Width,
            fieldOfView.Height,
            layout);
        var center = new Point(screen.Left + (screen.Width / 2), screen.Top + (screen.Height / 2));
        drawingContext.DrawRectangle(CameraFill, CameraPen, screen);
        drawingContext.DrawLine(
            CameraPen,
            new Point(center.X - 10, center.Y),
            new Point(center.X + 10, center.Y));
        drawingContext.DrawLine(
            CameraPen,
            new Point(center.X, center.Y - 10),
            new Point(center.X, center.Y + 10));
    }

    private static string PositionText(Point position, Point origin)
    {
        return FormattableString.Invariant(
            $"X {position.X - origin.X:F3}   Y {position.Y - origin.Y:F3}");
    }

    private static void DrawMarker(DrawingContext drawingContext, Point point, ImageMarker marker)
    {
        var brush = marker.Selected ? SelectedMarkerStroke : MarkerStroke;
        var pen = marker.Selected ? SelectedMarkerPen : MarkerPen;
        drawingContext.DrawEllipse(Brushes.Transparent, pen, point, 7, 7);
        drawingContext.DrawLine(pen, new Point(point.X - 10, point.Y), new Point(point.X + 10, point.Y));
        drawingContext.DrawLine(pen, new Point(point.X, point.Y - 10), new Point(point.X, point.Y + 10));

        var text = new FormattedText(
            marker.Label,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            MarkerTypeface,
            11,
            brush,
            1.0);
        drawingContext.DrawText(text, new Point(point.X + 11, point.Y - 9));
    }

    private static DependencyProperty Register(
        string name,
        Type type,
        PropertyChangedCallback changed,
        object? defaultValue = null)
    {
        return DependencyProperty.Register(
            name,
            type,
            typeof(ImageTeachingView),
            new FrameworkPropertyMetadata(defaultValue, changed));
    }

    private static void OnMapChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        ((ImageTeachingView)sender).RebuildMap();
    }

    private static void OnMarkersChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        ((ImageTeachingView)sender).DrawMarkers();
    }

    private static void OnCameraChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        ((ImageTeachingView)sender).DrawCamera();
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness, DashStyle? dashStyle = null)
    {
        var pen = new Pen(brush, thickness)
        {
            DashStyle = dashStyle,
        };
        pen.Freeze();
        return pen;
    }

    private sealed record MapView(Rect World, Rect Screen, double Scale);
}
