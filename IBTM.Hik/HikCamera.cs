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
    private const uint ImageNodeCount = 3;
    private const uint MaximumPacketResendPercent = 100;
    private const uint PacketResendTimeoutMilliseconds = 50;
    private static readonly MvGvspPixelType OutputPixelType =
        MvGvspPixelType.PixelType_Gvsp_BGR8_Packed;

    private readonly object _grabGate = new();
    private IDevice? _device;
    private IStreamGrabber? _streamGrabber;
    private EventHandler<FrameGrabbedEventArgs>? _liveFrameHandler;
    private bool _grabbing;
    private bool _liveView;

    public event Action<ImageFrame>? FrameReady;
    public event Action<Exception>? LiveViewFailed;
    public (int Width, int Height) FrameSize { get; private set; }

    public void Initialize()
    {
        if (_device?.IsConnected == true && _grabbing)
        {
            return;
        }

        Disconnect();
        var deviceInfo = EnumerateDevices().SingleOrDefault(
            device => string.Equals(
                device.SerialNumber,
                settings.DeviceId,
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
            Check(
                _streamGrabber.SetImageNodeNum(ImageNodeCount),
                "Set image node count");
            ConfigureSingleCapture(_device);
            Check(
                _streamGrabber.StartGrabbing(StreamGrabStrategy.OneByOne),
                "Start Hik grabbing");
            _grabbing = true;
        }
        catch
        {
            Disconnect();
            throw;
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
            Check(stream.ClearImageBuffer(), "Clear Hik image buffer");
            Check(
                device.Parameters.SetCommandValue("TriggerSoftware"),
                "Execute software trigger");
            Check(
                stream.GetImageBuffer(
                    checked((uint)settings.FrameTimeoutMilliseconds),
                    out var frameOut),
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
            var stream = _streamGrabber!;
            Check(stream.StopGrabbing(), "Stop software-trigger grabbing");
            _grabbing = false;
            ApplyExposureAndGain(device, exposureMicroseconds, gain);
            Check(
                device.Parameters.SetEnumValueByString("TriggerMode", "Off"),
                "Disable trigger for live view");

            var frameInterval = Stopwatch.Frequency
                                / settings.LiveViewFramesPerSecond;
            var nextFrame = 0L;
            var failed = 0;
            EventHandler<FrameGrabbedEventArgs> frameHandler =
                (_, eventArgs) =>
                {
                    if (Volatile.Read(ref failed) != 0)
                    {
                        return;
                    }

                    try
                    {
                        var now = Stopwatch.GetTimestamp();
                        if (now < nextFrame)
                        {
                            return;
                        }

                        nextFrame = now + frameInterval;
                        FrameReady?.Invoke(
                            ConvertFrame(device, eventArgs.FrameOut));
                    }
                    catch (Exception exception)
                    {
                        if (Interlocked.Exchange(ref failed, 1) == 0)
                        {
                            LiveViewFailed?.Invoke(exception);
                        }
                    }
                };
            stream.FrameGrabedEvent += frameHandler;
            _liveFrameHandler = frameHandler;
            try
            {
                Check(
                    stream.StartGrabbing(StreamGrabStrategy.LatestImageOnly),
                    "Start Hik live view");
                _grabbing = true;
                _liveView = true;
            }
            catch
            {
                stream.FrameGrabedEvent -= frameHandler;
                _liveFrameHandler = null;
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

            var stream = _streamGrabber!;
            if (_liveFrameHandler is not null)
            {
                stream.FrameGrabedEvent -= _liveFrameHandler;
            }

            _liveFrameHandler = null;
            _liveView = false;
            Check(stream.StopGrabbing(), "Stop Hik live view");
            _grabbing = false;

            ConfigureSingleCapture(_device!);
            Check(
                stream.StartGrabbing(StreamGrabStrategy.OneByOne),
                "Restart Hik software-trigger grabbing");
            _grabbing = true;
        }
    }

    public void Dispose() => Disconnect();

    private static List<IDeviceInfo> EnumerateDevices()
    {
        var deviceTypes =
            DeviceTLayerType.MvGigEDevice | DeviceTLayerType.MvUsbDevice;
        Check(
            DeviceEnumerator.EnumDevices(deviceTypes, out List<IDeviceInfo> devices),
            "Enumerate Hik cameras");
        return devices;
    }

    private void ConfigureAreaCamera(IDevice device)
    {
        ConfigureGigE(device);

        var parameters = device.Parameters;
        Check(
            parameters.SetEnumValueByString("AcquisitionMode", "Continuous"),
            "Set AcquisitionMode");
        Check(
            parameters.SetEnumValueByString("ExposureAuto", "Off"),
            "Disable ExposureAuto");
        Check(
            parameters.SetEnumValueByString("GainAuto", "Off"),
            "Disable GainAuto");
    }

    private static void ApplyExposureAndGain(IDevice device, double exposureMicroseconds, double gain)
    {
        var parameters = device.Parameters;
        Check(
            parameters.SetFloatValue(
                "ExposureTime",
                checked((float)exposureMicroseconds)),
            "Set ExposureTime");
        Check(
            parameters.SetFloatValue("Gain", checked((float)gain)),
            "Set Gain");
    }

    private static void ConfigureSingleCapture(IDevice device)
    {
        Check(
            device.Parameters.SetEnumValueByString(
                "TriggerSelector",
                "FrameStart"),
            "Set frame trigger selector");
        Check(
            device.Parameters.SetEnumValueByString("TriggerMode", "On"),
            "Enable software trigger");
        Check(
            device.Parameters.SetEnumValueByString("TriggerSource", "Software"),
            "Set software trigger source");
    }

    private static void ConfigureGigE(IDevice device)
    {
        if (device is not IGigEDevice gigEDevice)
        {
            return;
        }

        Check(
            gigEDevice.GetOptimalPacketSize(out var packetSize),
            "Get optimal GigE packet size");
        Check(
            device.Parameters.SetIntValue("GevSCPSPacketSize", packetSize),
            "Set GevSCPSPacketSize");
        Check(
            gigEDevice.SetResend(
                enable: true,
                maxResendPercent: MaximumPacketResendPercent,
                resendTimeout: PacketResendTimeoutMilliseconds),
            "Enable GigE packet resend");
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
            var stream = _streamGrabber;
            var device = _device;
            if (stream is not null && _liveFrameHandler is not null)
            {
                stream.FrameGrabedEvent -= _liveFrameHandler;
            }

            try
            {
                if (_grabbing)
                {
                    Check(stream!.StopGrabbing(), "Stop Hik grabbing");
                }
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
                    _grabbing = false;
                    _liveView = false;
                    _liveFrameHandler = null;
                    _streamGrabber = null;
                    _device = null;
                    FrameSize = default;
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
