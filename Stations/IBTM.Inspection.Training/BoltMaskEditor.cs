using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IBTM.Inspection.Training;

public sealed class BoltMaskEditor : Control
{
    private const double VertexRadius = 4;
    private const double CloseDistance = 8;
    private const double ZoomStep = 1.2;
    private const double MaximumZoom = 64;
    private static readonly DependencyPropertyKey MaskPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Mask),
        typeof(byte[]),
        typeof(BoltMaskEditor),
        new PropertyMetadata(Array.Empty<byte>()));
    private static readonly DependencyPropertyKey CompletedPolygonPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(CompletedPolygon),
        typeof(Point[]),
        typeof(BoltMaskEditor),
        new PropertyMetadata(Array.Empty<Point>()));

    public static readonly DependencyProperty MaskProperty = MaskPropertyKey.DependencyProperty;
    public static readonly DependencyProperty CompletedPolygonProperty = CompletedPolygonPropertyKey.DependencyProperty;
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source),
        typeof(BitmapSource),
        typeof(BoltMaskEditor),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsRender,
            (
                owner,
                _) => ((BoltMaskEditor)owner).ResetImage()));
    public static readonly DependencyProperty PredictionProperty = DependencyProperty.Register(
        nameof(Prediction),
        typeof(BitmapSource),
        typeof(BoltMaskEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowPredictionProperty = DependencyProperty.Register(
        nameof(ShowPrediction),
        typeof(bool),
        typeof(BoltMaskEditor),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PolygonProperty = DependencyProperty.Register(
        nameof(Polygon),
        typeof(Point[]),
        typeof(BoltMaskEditor),
        new PropertyMetadata(
            Array.Empty<Point>(),
            (
                owner,
                args) => ((BoltMaskEditor)owner).LoadPolygon((Point[])args.NewValue)));
    public static readonly DependencyProperty RegionSizeProperty = DependencyProperty.Register(
        nameof(RegionSize),
        typeof(int),
        typeof(BoltMaskEditor),
        new FrameworkPropertyMetadata(
            IBoltRecessSegmenter.InputSize,
            FrameworkPropertyMetadataOptions.AffectsRender
                | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (owner, args) => ((BoltMaskEditor)owner).ResizeRegion((int)args.OldValue, (int)args.NewValue)));

    private readonly List<Point> _points = [];
    private StreamGeometry? _polygon;
    private Point? _preview;
    private bool _closed;
    private double _zoom = 1;
    private Vector _pan;
    private Point _lastMouse;
    private DragAction _drag;

    private enum DragAction
    {
        None,
        Pan,
        ResizeRegion
    }

    public BoltMaskEditor()
    {
        Cursor = Cursors.Cross;
        Focusable = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
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

    public byte[] Mask
    {
        get
        {
            return (byte[])GetValue(MaskProperty);
        }
    }

    public BitmapSource? Prediction
    {
        get
        {
            return (BitmapSource?)GetValue(PredictionProperty);
        }

        set
        {
            SetValue(PredictionProperty, value);
        }
    }

    public bool ShowPrediction
    {
        get
        {
            return (bool)GetValue(ShowPredictionProperty);
        }

        set
        {
            SetValue(ShowPredictionProperty, value);
        }
    }

    public int RegionSize
    {
        get
        {
            return (int)GetValue(RegionSizeProperty);
        }

        set
        {
            SetValue(RegionSizeProperty, value);
        }
    }

    public Point[] Polygon
    {
        get
        {
            return (Point[])GetValue(PolygonProperty);
        }

        set
        {
            SetValue(PolygonProperty, value);
        }
    }

    private void LoadPolygon(Point[] points)
    {
        _points.Clear();
        _points.AddRange(points);
        _closed = points.Length >= 3;
        _preview = null;
        UpdatePolygon();
    }

    private void ResetImage()
    {
        Clear();
        FitImage();
    }

    public void FitImage()
    {
        _zoom = 1;
        _pan = default;
        InvalidateVisual();
    }

    public void FitRegion()
    {
        if (Source is null)
            return;
        _pan = default;
        _zoom = Math.Clamp(
            Math.Min(ActualWidth, ActualHeight) * 0.85 / (RegionSize * FitScale()),
            1,
            MaximumZoom);
        InvalidateVisual();
    }

    private void ResizeRegion(int oldSize, int newSize)
    {
        if (Source is null || _points.Count == 0)
            return;
        var previous = BoltTrainingImages.Region(Source, oldSize);
        var current = BoltTrainingImages.Region(Source, newSize);
        var inputSize = IBoltRecessSegmenter.InputSize;
        for (var index = 0; index < _points.Count; index++)
            _points[index] = new Point(
                (previous.X + _points[index].X * oldSize / inputSize - current.X) * inputSize / newSize,
                (previous.Y + _points[index].Y * oldSize / inputSize - current.Y) * inputSize / newSize);
        _preview = null;
        UpdatePolygon();
    }

    public void Clear()
    {
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        _points.Clear();
        _closed = false;
        _preview = null;
        UpdatePolygon();
    }

    public void UndoPoint()
    {
        if (_points.Count == 0)
            return;
        _points.RemoveAt(_points.Count - 1);
        _closed = false;
        UpdatePolygon();
    }

    public void ClosePolygon()
    {
        if (_closed || _points.Count < 3)
            return;
        _closed = true;
        _preview = null;
        UpdatePolygon();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(Brushes.Black, null, new Rect(RenderSize));
        if (Source is null)
            return;

        var image = ImageRect();
        var region = RegionRect();
        drawingContext.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        drawingContext.DrawImage(Source, image);
        drawingContext.PushOpacity(0.45);
        drawingContext.DrawGeometry(
            Brushes.Black,
            null,
            new CombinedGeometry(
                GeometryCombineMode.Exclude,
                new RectangleGeometry(image),
                new RectangleGeometry(region)));
        drawingContext.Pop();
        drawingContext.PushClip(new RectangleGeometry(region));
        if (ShowPrediction && Prediction is not null)
            drawingContext.DrawImage(Prediction, region);
        DrawPolygon(drawingContext, region);
        drawingContext.Pop();
        drawingContext.DrawRectangle(null, new Pen(Brushes.Black, 4), region);
        drawingContext.DrawRectangle(null, new Pen(Foreground, 2), region);
        foreach (var corner in Corners(region))
            drawingContext.DrawRectangle(
                Foreground,
                new Pen(Brushes.Black, 1),
                new Rect(
                    corner.X - VertexRadius,
                    corner.Y - VertexRadius,
                    VertexRadius * 2,
                    VertexRadius * 2));
        drawingContext.Pop();
    }

    private void DrawPolygon(DrawingContext drawingContext, Rect rect)
    {
        if (_points.Count == 0)
            return;

        var scale = rect.Width / IBoltRecessSegmenter.InputSize;
        drawingContext.PushTransform(new MatrixTransform(scale, 0, 0, scale, rect.Left, rect.Top));
        var pen = new Pen(Foreground, 1.5 / scale);
        if (_closed)
        {
            drawingContext.PushOpacity(0.3);
            drawingContext.DrawGeometry(Foreground, null, _polygon);
            drawingContext.Pop();
        }

        drawingContext.DrawGeometry(null, pen, _polygon);
        if (!_closed && _preview is { } preview)
            drawingContext.DrawLine(pen, _points[^1], preview);
        foreach (var point in _points)
            drawingContext.DrawEllipse(
                Foreground,
                null,
                point,
                VertexRadius / scale,
                VertexRadius / scale);
        drawingContext.DrawEllipse(null, pen, _points[0], CloseDistance / scale, CloseDistance / scale);
        drawingContext.Pop();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Source is not null && IsRegionHandle(e.GetPosition(this)))
        {
            Focus();
            _drag = DragAction.ResizeRegion;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if (Source is null || ToImagePoint(e.GetPosition(this)) is not { } point)
            return;
        Focus();
        if (e.ClickCount == 2)
            ClosePolygon();
        else
            AddPoint(point);
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        Focus();
        UndoPoint();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (_drag == DragAction.Pan)
        {
            _pan += point - _lastMouse;
            _lastMouse = point;
            InvalidateVisual();
        }
        else if (_drag == DragAction.ResizeRegion)
            ResizeRegionAt(point);
        else
            Cursor = Source is not null && IsRegionHandle(point)
                ? Cursors.SizeNWSE
                : _closed ? Cursors.Arrow : Cursors.Cross;
        if (_drag != DragAction.None)
            return;
        if (_closed || _points.Count == 0)
            return;
        _preview = ToImagePoint(point);
        InvalidateVisual();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (Source is null || e.ChangedButton != MouseButton.Middle)
            return;
        Focus();
        _drag = DragAction.Pan;
        _lastMouse = e.GetPosition(this);
        Cursor = Cursors.Hand;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag == DragAction.None)
            return;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _drag = DragAction.None;
        Cursor = _closed ? Cursors.Arrow : Cursors.Cross;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Source is null)
            return;
        ZoomAt(e.GetPosition(this), e.Delta > 0 ? ZoomStep : 1 / ZoomStep);
        e.Handled = true;
    }

    private void ZoomAt(Point point, double factor)
    {
        var zoom = Math.Clamp(_zoom * factor, 1, MaximumZoom);
        var relative = point - new Point(ActualWidth / 2, ActualHeight / 2);
        _pan = relative - (relative - _pan) * (zoom / _zoom);
        _zoom = zoom;
        _preview = null;
        InvalidateVisual();
    }

    private void ResizeRegionAt(Point point)
    {
        var image = ImageRect();
        var scale = image.Width / Source!.PixelWidth;
        var side = 2 * Math.Max(
            Math.Abs(point.X - image.X - image.Width / 2),
            Math.Abs(point.Y - image.Y - image.Height / 2)) / scale;
        SetCurrentValue(
            RegionSizeProperty,
            Math.Clamp((int)Math.Round(side), 1, Math.Min(Source.PixelWidth, Source.PixelHeight)));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _preview = null;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Escape:
                Clear();
                break;
            case Key.Enter:
                ClosePolygon();
                break;
            case Key.Back:
                UndoPoint();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void AddPoint(Point point)
    {
        if (_closed)
            return;
        var scale = RegionRect().Width / IBoltRecessSegmenter.InputSize;
        if (_points.Count >= 3 && (point - _points[0]).Length * scale <= CloseDistance)
        {
            ClosePolygon();
            return;
        }

        _points.Add(point);
        _preview = null;
        UpdatePolygon();
    }

    private void UpdatePolygon()
    {
        _polygon = _points.Count > 0 ? BoltPolygon.Geometry(_points, _closed) : null;
        var mask = _closed ? BoltPolygon.Mask(_points) : Array.Empty<byte>();
        SetValue(MaskPropertyKey, mask.Any(value => value > 0) ? mask : Array.Empty<byte>());
        SetValue(CompletedPolygonPropertyKey, Mask.Length > 0 ? _points.ToArray() : Array.Empty<Point>());
        Cursor = _closed ? Cursors.Arrow : Cursors.Cross;
        InvalidateVisual();
    }

    public Point[] CompletedPolygon
    {
        get
        {
            return (Point[])GetValue(CompletedPolygonProperty);
        }
    }

    private Point? ToImagePoint(Point point)
    {
        var rect = RegionRect();
        if (!rect.Contains(point))
            return null;
        var scale = IBoltRecessSegmenter.InputSize / rect.Width;
        return new Point((point.X - rect.Left) * scale, (point.Y - rect.Top) * scale);
    }

    private Rect ImageRect()
    {
        var scale = FitScale() * _zoom;
        var width = Source!.PixelWidth * scale;
        var height = Source.PixelHeight * scale;
        return new Rect(
            (ActualWidth - width) / 2 + _pan.X,
            (ActualHeight - height) / 2 + _pan.Y,
            width,
            height);
    }

    private double FitScale()
    {
        return Math.Min(ActualWidth / Source!.PixelWidth, ActualHeight / Source.PixelHeight);
    }

    private Rect RegionRect()
    {
        var image = ImageRect();
        var region = BoltTrainingImages.Region(Source!, RegionSize);
        var scale = image.Width / Source!.PixelWidth;
        return new Rect(
            image.X + region.X * scale,
            image.Y + region.Y * scale,
            region.Width * scale,
            region.Height * scale);
    }

    private bool IsRegionHandle(Point point)
    {
        return Corners(RegionRect()).Any(corner => (point - corner).Length <= CloseDistance);
    }

    private static Point[] Corners(Rect rect)
    {
        return [rect.TopLeft, rect.TopRight, rect.BottomLeft, rect.BottomRight];
    }
}
