using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using IBTM.Core;
using IBTM.Device;
using MvCameraControl;

namespace IBTM.Hik;

public sealed class HikCamera(InspectionCameraSettings settings)
    : ICamera, IDisposable
{
    private static readonly MvGvspPixelType OutputPixelType =
        MvGvspPixelType.PixelType_Gvsp_BGR8_Packed;

    private readonly object _grabGate = new();
    private IDevice? _device;
    private IStreamGrabber? _streamGrabber;
    private Thread? _liveThread;
    private bool _sdkInitialized;
    private bool _grabbing;
    private volatile bool _liveView;

    public event Action<ImageFrame>? FrameReady;
    public event Action<Exception>? LiveViewFailed;
    public (int Width, int Height) FrameSize { get; private set; }

    public void Initialize()
    {
        lock (_grabGate)
        {
            if (_device?.IsConnected == true) return;

            Disconnect();
            if (!_sdkInitialized)
            {
                Check(SDKSystem.Initialize(), "Initialize MVS SDK");
                _sdkInitialized = true;
            }

            var deviceInfo = EnumerateDevices().SingleOrDefault(
                device => string.Equals(device.SerialNumber, settings.DeviceId,
                    StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Hik camera '{settings.DeviceId}' was not found.");

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
            catch
            {
                Disconnect();
                throw;
            }
        }
    }

    public ImageFrame Capture(double exposureMicroseconds, double gain)
    {
        lock (_grabGate)
        {
            if (_liveView)
            {
                throw new InvalidOperationException(
                    "Stop Hik live view before single-frame capture.");
            }

            var device = _device
                ?? throw new InvalidOperationException("Hik camera is not initialized.");
            var stream = _streamGrabber!;
            ApplyExposureAndGain(device, exposureMicroseconds, gain);
            StartGrabbing();
            try
            {
                Check(stream.GetImageBuffer(checked((uint)settings.FrameTimeoutMilliseconds), out var frameOut),
                    "Get single Hik frame");
                try
                {
                    return ConvertFrame(device, frameOut);
                }
                finally
                {
                    Check(stream.FreeImageBuffer(frameOut), "Free single Hik frame");
                }
            }
            finally
            {
                StopGrabbing();
            }
        }
    }

    public void StartLiveView(double exposureMicroseconds, double gain)
    {
        lock (_grabGate)
        {
            if (_liveView)
            {
                return;
            }

            var device = _device
                ?? throw new InvalidOperationException("Hik camera is not initialized.");
            ApplyExposureAndGain(device, exposureMicroseconds, gain);
            var liveThread = new Thread(ReceiveLiveFrames) { IsBackground = true, Name = "Hik live view" };
            StartGrabbing();
            _liveThread = liveThread;
            _liveView = true;
            try
            {
                _liveThread.Start();
            }
            catch
            {
                _liveView = false;
                _liveThread = null;
                StopGrabbing();
                throw;
            }
        }
    }

    public void StopLiveView()
    {
        lock (_grabGate)
        {
            if (!_liveView)
            {
                return;
            }

            _liveView = false;
            _liveThread!.Join();
            _liveThread = null;
            StopGrabbing();
        }
    }

    private void ReceiveLiveFrames()
    {
        var device = _device!;
        var stream = _streamGrabber!;
        try
        {
            var frameInterval = Stopwatch.Frequency / settings.LiveViewFramesPerSecond;
            var nextFrame = 0L;
            while (_liveView)
            {
                var result = stream.GetImageBuffer(1000, out var frameOut);
                if (result == MvError.MV_E_NODATA) continue;
                Check(result, "Get live Hik frame");
                ImageFrame? image = null;
                try
                {
                    var now = Stopwatch.GetTimestamp();
                    if (_liveView && now >= nextFrame)
                    {
                        image = ConvertFrame(device, frameOut);
                        nextFrame = now + frameInterval;
                    }
                }
                finally
                {
                    Check(stream.FreeImageBuffer(frameOut), "Free live Hik frame");
                }

                if (image is not null && _liveView) FrameReady?.Invoke(image);
            }
        }
        catch (Exception exception)
        {
            LiveViewFailed?.Invoke(exception);
        }
    }

    private void StartGrabbing()
    {
        Check(_streamGrabber!.StartGrabbing(), "Start Hik grabbing");
        _grabbing = true;
    }

    private void StopGrabbing()
    {
        if (!_grabbing) return;
        Check(_streamGrabber!.StopGrabbing(), "Stop Hik grabbing");
        _grabbing = false;
    }

    public void Dispose()
    {
        lock (_grabGate)
        {
            try
            {
                Disconnect();
            }
            finally
            {
                if (_sdkInitialized)
                {
                    Check(SDKSystem.Finalize(), "Finalize MVS SDK");
                    _sdkInitialized = false;
                }
            }
        }
    }

    private static List<IDeviceInfo> EnumerateDevices()
    {
        var deviceTypes =
            DeviceTLayerType.MvGigEDevice | DeviceTLayerType.MvUsbDevice;
        Check(
            DeviceEnumerator.EnumDevices(deviceTypes, out List<IDeviceInfo> devices),
            "Enumerate Hik cameras");
        return devices;
    }

    private static void ConfigureAreaCamera(IDevice device)
    {
        ConfigureGigE(device);

        var parameters = device.Parameters;
        Check(
            parameters.SetEnumValueByString("AcquisitionMode", "Continuous"),
            "Set AcquisitionMode");
        // MVS BasicDemo: select continuous acquisition once, while grabbing is stopped.
        Check(
            parameters.SetEnumValueByString("TriggerMode", "Off"),
            "Set continuous acquisition");
    }

    private static void ApplyExposureAndGain(IDevice device, double exposureMicroseconds, double gain)
    {
        var parameters = device.Parameters;
        Check(parameters.SetEnumValueByString("ExposureAuto", "Off"), "Disable ExposureAuto");
        Check(
            parameters.SetFloatValue(
                "ExposureTime",
                checked((float)exposureMicroseconds)),
            "Set ExposureTime");
        Check(parameters.SetEnumValueByString("GainAuto", "Off"), "Disable GainAuto");
        Check(
            parameters.SetFloatValue("Gain", checked((float)gain)),
            "Set Gain");
    }

    private static void ConfigureGigE(IDevice device)
    {
        if (device is not IGigEDevice gigEDevice)
        {
            return;
        }

        var result = gigEDevice.GetOptimalPacketSize(out var packetSize);
        if (result == MvError.MV_OK)
            result = device.Parameters.SetIntValue("GevSCPSPacketSize", packetSize);
        if (result != MvError.MV_OK)
            Trace.TraceWarning("Hik GigE packet size setup failed. MVS error code: 0x{0:X8}", result);
    }

    private static ImageFrame ConvertFrame(
        IDevice device,
        IFrameOut frameOut)
    {
        if (frameOut.LostPacket != 0)
        {
            throw new InvalidOperationException(
                $"Hik frame {frameOut.FrameNum} lost "
                + $"{frameOut.LostPacket} packet(s).");
        }

        var image = frameOut.Image;
        var width = checked((int)image.Width);
        var height = checked((int)image.Height);
        var pixels = new byte[checked(
            width * height * ImageFrame.ColorChannelCount)];
        Check(
            device.PixelTypeConverter.ConvertPixelType(
                image,
                pixels,
                out var convertedSize,
                OutputPixelType),
            "Convert Hik frame to BGR8");

        if (convertedSize != checked((ulong)pixels.Length))
        {
            throw new InvalidOperationException(
                $"Hik BGR frame size is {convertedSize}; expected {pixels.Length}.");
        }

        return new ImageFrame(
            width,
            height,
            width * ImageFrame.ColorChannelCount,
            pixels);
    }

    private void Disconnect()
    {
        lock (_grabGate)
        {
            var device = _device;
            try
            {
                StopLiveView();
                StopGrabbing();
            }
            finally
            {
                try
                {
                    if (device?.IsConnected == true)
                    {
                        Check(device.Close(), "Close Hik camera");
                    }
                }
                finally
                {
                    try
                    {
                        device?.Dispose();
                    }
                    finally
                    {
                        _grabbing = false;
                        _liveView = false;
                        _liveThread = null;
                        _streamGrabber = null;
                        _device = null;
                        FrameSize = default;
                    }
                }
            }
        }
    }

    private static void Check(int result, string operation)
    {
        if (result != 0)
        {
            throw new InvalidOperationException(
                $"{operation} failed. MVS error code: 0x{result:X8}");
        }
    }
}
