using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using MvCameraControl;
using Microsoft.Extensions.Logging;

namespace IBTM.Hik;

public sealed class HikCamera : ICamera, IDisposable
{
    private readonly ILogger<HikCamera>? _log;
    private readonly InspectionCameraSettings _settings;
    private static readonly MvGvspPixelType s_conversionPixelType = MvGvspPixelType.PixelType_Gvsp_RGB8_Packed;

    // Device selection changes apply after restart, not during connection recovery.
    private readonly string _deviceId;
    private readonly object _grabGate;
    private IDevice? _device;
    private IStreamGrabber? _streamGrabber;
    private Thread? _liveThread;
    private bool _sdkInitialized;
    private bool _disposed;
    private bool _grabbing;
    private volatile bool _liveView;

    public HikCamera(InspectionCameraSettings settings, ILogger<HikCamera>? log = null)
    {
        _log = log;
        _settings = settings;
        _deviceId = _settings.DeviceId;
        _grabGate = new();
    }

    public event Action<ImageFrame>? FrameReady;
    public event Action<Exception>? LiveViewFailed;

    public bool IsLiveView => _liveView && _liveThread?.IsAlive == true;

    public (int Width, int Height) FrameSize { get; private set; }

    public void Initialize()
    {
        lock (_grabGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_device?.IsConnected == true)
            {
                try
                {
                    StopLiveView();
                    return;
                }
                catch (Exception failure)
                {
                    // A broken grab must not leave the same handle blocking every RESET.
                    try
                    {
                        Disconnect();
                    }
                    catch (Exception cleanupFailure)
                    {
                        throw new AggregateException(failure, cleanupFailure);
                    }
                    throw;
                }
            }

            Disconnect();
            if (!_sdkInitialized)
            {
                Check(SDKSystem.Initialize(), "Initialize MVS SDK");
                _sdkInitialized = true;
            }

            var deviceInfo = EnumerateDevices()
                .SingleOrDefault(
                    device => string.Equals(
                        device.SerialNumber,
                        _deviceId,
                        StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException(
                            $"Hik camera '{_deviceId}' was not found.");

            _device = DeviceFactory.CreateDevice(deviceInfo);
            try
            {
                Check(_device.Open(), "Open Hik camera");
                ConfigureAreaCamera(_device);
                Check(_device.Parameters.GetIntValue("Width", out var width), "Read Hik frame width");
                Check(_device.Parameters.GetIntValue("Height", out var height), "Read Hik frame height");
                FrameSize = (checked((int)width.CurValue), checked((int)height.CurValue));
                _streamGrabber = _device.StreamGrabber;
            }
            catch (Exception failure)
            {
                try
                {
                    Disconnect();
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException(failure, cleanupFailure);
                }
                throw;
            }
        }
    }

    public async Task<ImageFrame> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        // Single-frame SDK acquisition is synchronous. Live frames arrive through events.
        var frame = await Task.Run(() =>
        {
            lock (_grabGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return _liveView
                    ? CaptureLiveFrameAsync(cancellationToken)
                    : Task.FromResult(CaptureSingleFrame());
            }
        }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return frame;
    }

    private ImageFrame CaptureSingleFrame()
    {
        // Teaching may use the camera before machine-wide initialization reaches vision.
        Initialize();
        var device = _device!;
        var stream = _streamGrabber!;
        StartGrabbing();
        ImageFrame? image = null;
        Exception? failure = null;
        try
        {
            Check(
                stream.GetImageBuffer(
                    checked((uint)_settings.FrameTimeoutMilliseconds),
                    out var frameOut),
                "Get single Hik frame");
            image = CopyAndReleaseFrame(device, stream, frameOut);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            StopGrabbing();
        }
        catch (Exception cleanupFailure) when (failure is not null)
        {
            throw new AggregateException(failure, cleanupFailure);
        }

        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
        return image!;
    }

    private async Task<ImageFrame> CaptureLiveFrameAsync(CancellationToken cancellationToken)
    {
        var captured = new TaskCompletionSource<ImageFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(ImageFrame frame)
        {
            captured.TrySetResult(frame);
        }
        void OnFailure(Exception exception)
        {
            captured.TrySetException(exception);
        }

        FrameReady += OnFrame;
        LiveViewFailed += OnFailure;
        try
        {
            return await captured.Task.WaitAsync(
                TimeSpan.FromMilliseconds(_settings.FrameTimeoutMilliseconds),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            FrameReady -= OnFrame;
            LiveViewFailed -= OnFailure;
        }
    }

    public void StartLiveView()
    {
        lock (_grabGate)
        {
            if (_liveView)
            {
                return;
            }

            Initialize();
            var liveThread = new Thread(ReceiveLiveFrames)
            {
                IsBackground = true,
                Name = "Hik live view",
            };
            StartGrabbing();
            _liveThread = liveThread;
            _liveView = true;
            try
            {
                _liveThread.Start();
            }
            catch (Exception failure)
            {
                _liveView = false;
                _liveThread = null;
                try
                {
                    StopGrabbing();
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException(failure, cleanupFailure);
                }
                throw;
            }
        }
    }

    public void StopLiveView()
    {
        // A frame/error subscriber can request STOP on the receive thread itself.
        if (Thread.CurrentThread == _liveThread)
        {
            _liveView = false;
            return;
        }

        lock (_grabGate)
        {
            _liveView = false;
            _liveThread?.Join();
            _liveThread = null;
            StopGrabbing();
        }
    }

    private void ReceiveLiveFrames()
    {
        var device = _device!;
        var stream = _streamGrabber!;
        Exception? failure = null;
        try
        {
            while (_liveView)
            {
                var result = stream.GetImageBuffer(1000, out var frameOut);
                if (result == MvError.MV_E_NODATA)
                    continue;
                Check(result, "Get live Hik frame");
                var image = CopyAndReleaseFrame(device, stream, frameOut, copy: _liveView);

                if (image is not null && _liveView)
                    FrameReady?.Invoke(image);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            StopGrabbing();
        }
        catch (Exception exception)
        {
            failure = failure is null
                ? exception
                : new AggregateException(failure, exception);
        }
        finally
        {
            _liveView = false;
        }

        if (failure is not null)
            LiveViewFailed?.Invoke(failure);
    }

    private void StartGrabbing()
    {
        Check(_streamGrabber!.StartGrabbing(), "Start Hik grabbing");
        _grabbing = true;
    }

    private void StopGrabbing()
    {
        if (!_grabbing)
            return;
        Check(_streamGrabber!.StopGrabbing(), "Stop Hik grabbing");
        _grabbing = false;
    }

    public void Dispose()
    {
        lock (_grabGate)
        {
            _disposed = true;
            Exception? failure = null;
            try
            {
                Disconnect();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            try
            {
                if (_sdkInitialized)
                {
                    Check(SDKSystem.Finalize(), "Finalize MVS SDK");
                    _sdkInitialized = false;
                }
            }
            catch (Exception exception)
            {
                failure = failure is null ? exception : new AggregateException(failure, exception);
            }

            if (failure is not null)
                ExceptionDispatchInfo.Throw(failure);
        }
    }

    private static List<IDeviceInfo> EnumerateDevices()
    {
        var deviceTypes = DeviceTLayerType.MvGigEDevice | DeviceTLayerType.MvUsbDevice;
        Check(
            DeviceEnumerator.EnumDevices(deviceTypes, out List<IDeviceInfo> devices),
            "Enumerate Hik cameras");
        return devices;
    }

    private void ConfigureAreaCamera(IDevice device)
    {
        ConfigureGigE(device);

        var parameters = device.Parameters;
        Check(parameters.SetEnumValueByString("AcquisitionMode", "Continuous"), "Set AcquisitionMode");
        // MVS BasicDemo: select continuous acquisition once, while grabbing is stopped.
        Check(parameters.SetEnumValueByString("TriggerMode", "Off"), "Set continuous acquisition");
    }

    private void ConfigureGigE(IDevice device)
    {
        if (device is not IGigEDevice gigEDevice)
        {
            return;
        }

        var result = gigEDevice.GetOptimalPacketSize(out var packetSize);
        if (result == MvError.MV_OK)
            result = device.Parameters.SetIntValue("GevSCPSPacketSize", packetSize);
        if (result != MvError.MV_OK)
            _log?.LogWarning("Hik GigE packet size setup failed. MVS error code: 0x{Code:X8}", result);
    }

    private static ImageFrame? CopyAndReleaseFrame(
        IDevice device,
        IStreamGrabber stream,
        IFrameOut frameOut,
        bool copy = true)
    {
        Exception? failure = null;
        try
        {
            if (!copy)
                return null;
            if (frameOut.LostPacket != 0)
            {
                throw new InvalidOperationException(
                    $"Hik frame {frameOut.FrameNum} lost {frameOut.LostPacket} packet(s).");
            }

            var image = frameOut.Image;
            var width = checked((int)image.Width);
            var height = checked((int)image.Height);
            var pixels = new byte[checked(width * height * ImageFrame.ColorChannelCount)];
            Check(
                device.PixelTypeConverter.ConvertPixelType(
                    image,
                    pixels,
                    out var convertedSize,
                    s_conversionPixelType),
                "Convert Hik frame to RGB8");

            if (convertedSize != checked((ulong)pixels.Length))
            {
                throw new InvalidOperationException(
                    $"Hik RGB frame size is {convertedSize}; expected {pixels.Length}.");
            }

            // Packed RGB has no row padding. Reversing all bytes flips X/Y and converts RGB to BGR.
            Array.Reverse(pixels);
            return new ImageFrame(width, height, width * ImageFrame.ColorChannelCount, pixels);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            try
            {
                Check(stream.FreeImageBuffer(frameOut), "Free Hik frame");
            }
            catch (Exception cleanupFailure) when (failure is not null)
            {
                throw new AggregateException(failure, cleanupFailure);
            }
        }
    }

    private void Disconnect()
    {
        var device = _device;
        Exception? failure = null;
        try
        {
            StopLiveView();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        _grabbing = false;
        _liveView = false;
        _liveThread = null;
        _streamGrabber = null;
        _device = null;
        FrameSize = default;
        try
        {
            if (device is not null)
                Check(device.Close(), "Close Hik camera");
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }

        try
        {
            device?.Dispose();
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }

        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }

    private static void Check(int result, string operation)
    {
        if (result != 0)
        {
            throw new InvalidOperationException($"{operation} failed. MVS error code: 0x{result:X8}");
        }
    }
}
