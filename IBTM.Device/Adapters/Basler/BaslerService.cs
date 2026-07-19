using Basler.Pylon;
using System.Drawing;

namespace IBTM.Device.Adapters.Basler;

public sealed record BaslerCameraInfo(
    string SerialNumber,
    Size ImageSize,
    Camera Camera,
    IStreamGrabber StreamGrabber);

public sealed class CameraFrameEventArgs(
    string serialNumber,
    Size imageSize,
    byte[] pixels) : EventArgs
{
    public string SerialNumber { get; } = serialNumber;
    public Size ImageSize { get; } = imageSize;
    public byte[] Pixels { get; } = pixels;
}

/// <summary>Owns discovered Basler cameras and converts grabbed frames to BGRA32.</summary>
public sealed class BaslerService : IDisposable
{
    private readonly List<BaslerCameraInfo> _cameras = [];
    private bool _disposed;

    public IReadOnlyList<BaslerCameraInfo> Cameras => _cameras;

    public event EventHandler<CameraFrameEventArgs>? FrameReceived;
    public event EventHandler<Exception>? CaptureFailed;

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_cameras.Count > 0)
        {
            return;
        }

        foreach (var cameraInfo in CameraFinder.Enumerate())
        {
            var camera = new Camera(cameraInfo);
            try
            {
                camera.CameraOpened += OnCameraOpened;
                camera.Open();
                var streamGrabber = camera.StreamGrabber
                    ?? throw new InvalidOperationException("The Basler camera has no stream grabber.");
                streamGrabber.ImageGrabbed += OnImageGrabbed;
                streamGrabber.UserData = camera;

                var serialNumber = cameraInfo["SerialNumber"]
                    ?? throw new InvalidOperationException("The Basler camera has no serial number.");
                var imageSize = new Size(
                    checked((int)camera.Parameters[PLCamera.Width].GetValue()),
                    checked((int)camera.Parameters[PLCamera.Height].GetValue()));
                _cameras.Add(new BaslerCameraInfo(serialNumber, imageSize, camera, streamGrabber));
            }
            catch
            {
                camera.Dispose();
                throw;
            }
        }
    }

    public IEnumerable<string> GetSerialNumbers() =>
        _cameras.Select(camera => camera.SerialNumber);

    public void SetReverseX(bool enabled)
    {
        foreach (var camera in _cameras)
        {
            camera.Camera.Parameters[PLCamera.ReverseX].TrySetValue(enabled);
        }
    }

    public CameraFrameEventArgs? GrabSingle(string serialNumber)
    {
        var cameraInfo = FindCamera(serialNumber);
        if (cameraInfo is null || cameraInfo.StreamGrabber.IsGrabbing)
        {
            return null;
        }

        Configuration.AcquireSingleFrame(cameraInfo.Camera, EventArgs.Empty);
        using var grabResult = cameraInfo.StreamGrabber.GrabOne(-1)
            ?? throw new InvalidOperationException("The Basler camera returned no grab result.");
        var pixels = ConvertPixels(grabResult);
        return new CameraFrameEventArgs(cameraInfo.SerialNumber, cameraInfo.ImageSize, pixels);
    }

    public void StartContinuousGrab(string serialNumber)
    {
        var cameraInfo = FindCamera(serialNumber);
        if (cameraInfo is null || cameraInfo.StreamGrabber.IsGrabbing)
        {
            return;
        }

        Configuration.AcquireContinuous(cameraInfo.Camera, EventArgs.Empty);
        cameraInfo.StreamGrabber.Start(
            GrabStrategy.OneByOne,
            GrabLoop.ProvidedByStreamGrabber);
    }

    public void StopAll()
    {
        foreach (var camera in _cameras)
        {
            Stop(camera);
        }
    }

    public void Stop(string serialNumber)
    {
        var cameraInfo = FindCamera(serialNumber);
        if (cameraInfo is not null)
        {
            Stop(cameraInfo);
        }
    }

    public void SetExposure(string serialNumber, double exposureMicroseconds)
    {
        if (!double.IsFinite(exposureMicroseconds) || exposureMicroseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(exposureMicroseconds),
                "Exposure must be finite and greater than zero.");
        }

        var cameraInfo = FindCamera(serialNumber)
            ?? throw new ArgumentException(
                $"Camera '{serialNumber}' was not found.",
                nameof(serialNumber));
        cameraInfo.Camera.Parameters[PLCamera.ExposureTimeAbs].TrySetValue(exposureMicroseconds);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var cameraInfo in _cameras)
        {
            var camera = cameraInfo.Camera;
            Stop(cameraInfo);
            cameraInfo.StreamGrabber.ImageGrabbed -= OnImageGrabbed;
            camera.CameraOpened -= OnCameraOpened;
            camera.Dispose();
        }

        _cameras.Clear();
    }

    private static void OnCameraOpened(object? sender, EventArgs eventArgs)
    {
        if (sender is not null)
        {
            Configuration.AcquireSingleFrame(sender, eventArgs);
        }
    }

    private void OnImageGrabbed(object? sender, ImageGrabbedEventArgs eventArgs)
    {
        try
        {
            if (sender is not IStreamGrabber { UserData: Camera camera }
                || eventArgs.GrabResult is null)
            {
                return;
            }

            var cameraInfo = _cameras.FirstOrDefault(candidate => candidate.Camera == camera);
            if (cameraInfo is null)
            {
                return;
            }

            using var grabResult = eventArgs.GrabResult;
            var pixels = ConvertPixels(grabResult);
            FrameReceived?.Invoke(
                this,
                new CameraFrameEventArgs(
                    cameraInfo.SerialNumber,
                    cameraInfo.ImageSize,
                    pixels));
        }
        catch (Exception exception)
        {
            CaptureFailed?.Invoke(this, exception);
        }
    }

    private BaslerCameraInfo? FindCamera(string serialNumber) =>
        _cameras.FirstOrDefault(camera => camera.SerialNumber == serialNumber);

    private static void Stop(BaslerCameraInfo cameraInfo)
    {
        if (cameraInfo.StreamGrabber.IsGrabbing)
        {
            cameraInfo.StreamGrabber.Stop();
        }
    }

    private static byte[] ConvertPixels(IGrabResult grabResult)
    {
        var converter = new PixelDataConverter
        {
            OutputPixelFormat = PixelType.BGRA8packed,
        };
        var pixels = new byte[checked(grabResult.Width * grabResult.Height * 4)];
        converter.Convert(pixels, grabResult);
        return pixels;
    }
}
