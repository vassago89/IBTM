using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace IBTM.Inspection.Training;

public sealed class BoltMaskEditor : FrameworkElement
{
    private static readonly DependencyPropertyKey MaskPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(Mask),
            typeof(byte[]),
            typeof(BoltMaskEditor),
            new PropertyMetadata(Array.Empty<byte>()));

    public static readonly DependencyProperty MaskProperty =
        MaskPropertyKey.DependencyProperty;

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(
            nameof(Source),
            typeof(BitmapSource),
            typeof(BoltMaskEditor),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.AffectsRender,
                SourceChanged));

    public static readonly DependencyProperty BrushSizeProperty =
        DependencyProperty.Register(
            nameof(BrushSize),
            typeof(double),
            typeof(BoltMaskEditor),
            new PropertyMetadata(6d));

    private BitmapSource? _overlay;
    private Point? _lastPoint;
    private byte _paintValue;
    private bool _overlayRefreshQueued;

    public BoltMaskEditor()
    {
        Cursor = Cursors.Cross;
        Focusable = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    public BitmapSource? Source
    {
        get => (BitmapSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public double BrushSize
    {
        get => (double)GetValue(BrushSizeProperty);
        set => SetValue(BrushSizeProperty, value);
    }

    public byte[] Mask => (byte[])GetValue(MaskProperty);

    public void Clear()
    {
        SetValue(
            MaskPropertyKey,
            Source is null
                ? Array.Empty<byte>()
                : new byte[IBoltRecessSegmenter.InputSize
                           * IBoltRecessSegmenter.InputSize]);
        RefreshOverlay();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(
            Brushes.Black,
            null,
            new Rect(RenderSize));

        if (Source is null)
        {
            return;
        }

        var imageRect = ImageRect();
        drawingContext.DrawImage(Source, imageRect);
        if (_overlay is not null)
        {
            drawingContext.DrawImage(_overlay, imageRect);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        BeginPaint(e.GetPosition(this), byte.MaxValue);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        BeginPaint(e.GetPosition(this), 0);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured)
        {
            Paint(e.GetPosition(this));
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        EndPaint();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        EndPaint();
        e.Handled = true;
    }

    private static void SourceChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs _) =>
        ((BoltMaskEditor)dependencyObject).Clear();

    private void BeginPaint(Point point, byte value)
    {
        if (Source is null)
        {
            return;
        }

        _paintValue = value;
        _lastPoint = null;
        CaptureMouse();
        Paint(point);
    }

    private void EndPaint()
    {
        _lastPoint = null;
        ReleaseMouseCapture();
    }

    private void Paint(Point screenPoint)
    {
        var imagePoint = ToImagePoint(screenPoint);
        if (imagePoint is null)
        {
            return;
        }

        var start = _lastPoint ?? imagePoint.Value;
        var distance = imagePoint.Value - start;
        var steps = Math.Max(
            1,
            (int)Math.Ceiling(Math.Max(
                Math.Abs(distance.X),
                Math.Abs(distance.Y))));
        for (var step = 0; step <= steps; step++)
        {
            var amount = (double)step / steps;
            Stamp(new Point(
                start.X + (distance.X * amount),
                start.Y + (distance.Y * amount)));
        }

        _lastPoint = imagePoint;
        QueueOverlayRefresh();
    }

    private void Stamp(Point point)
    {
        var size = IBoltRecessSegmenter.InputSize;
        var radius = BrushSize / 2;
        var left = Math.Max(0, (int)Math.Floor(point.X - radius));
        var top = Math.Max(0, (int)Math.Floor(point.Y - radius));
        var right = Math.Min(size - 1, (int)Math.Ceiling(point.X + radius));
        var bottom = Math.Min(size - 1, (int)Math.Ceiling(point.Y + radius));
        var radiusSquared = radius * radius;

        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var dx = x - point.X;
                var dy = y - point.Y;
                if ((dx * dx) + (dy * dy) <= radiusSquared)
                {
                    Mask[(y * size) + x] = _paintValue;
                }
            }
        }
    }

    private Point? ToImagePoint(Point point)
    {
        var rect = ImageRect();
        if (!rect.Contains(point))
        {
            return null;
        }

        return new Point(
            (point.X - rect.Left) * IBoltRecessSegmenter.InputSize / rect.Width,
            (point.Y - rect.Top) * IBoltRecessSegmenter.InputSize / rect.Height);
    }

    private Rect ImageRect()
    {
        var side = Math.Min(ActualWidth, ActualHeight);
        return new Rect(
            (ActualWidth - side) / 2,
            (ActualHeight - side) / 2,
            side,
            side);
    }

    private void RefreshOverlay()
    {
        var pixels = new byte[Mask.Length * 4];
        for (var index = 0; index < Mask.Length; index++)
        {
            if (Mask[index] == 0)
            {
                continue;
            }

            var target = index * 4;
            pixels[target] = 40;
            pixels[target + 1] = 90;
            pixels[target + 2] = 255;
            pixels[target + 3] = 150;
        }

        _overlay = Mask.Length == 0
            ? null
            : BitmapSource.Create(
                IBoltRecessSegmenter.InputSize,
                IBoltRecessSegmenter.InputSize,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                pixels,
                IBoltRecessSegmenter.InputSize * 4);
        _overlay?.Freeze();
        InvalidateVisual();
    }

    private void QueueOverlayRefresh()
    {
        if (_overlayRefreshQueued)
        {
            return;
        }

        _overlayRefreshQueued = true;
        Dispatcher.InvokeAsync(
            () =>
            {
                _overlayRefreshQueued = false;
                RefreshOverlay();
            },
            DispatcherPriority.Background);
    }
}
