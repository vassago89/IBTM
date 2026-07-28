using System;
using System.Collections.Generic;
using System.Linq;
using IBTM.Device;
using MvCameraControl;

namespace IBTM.Hik;

public sealed class HikCameraStreamService(CameraSettings settings)
    : ICameraStreamService, IDisposable
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

    public int ImageWidth { get; private set; }
    public int ImageHeight { get; private set; }

    public void Initialize()
    {
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

    public ImageFrame Capture()
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

    public void StartLiveView()
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
            Check(
                device.Parameters.SetEnumValueByString("TriggerMode", "Off"),
                "Disable trigger for live view");

            EventHandler<FrameGrabbedEventArgs> frameHandler =
                (_, eventArgs) => FrameReady?.Invoke(
                    ConvertFrame(device, eventArgs.FrameOut));
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
            Check(stream.StopGrabbing(), "Stop Hik live view");
            _grabbing = false;
            _liveView = false;

            ConfigureSingleCapture(_device!);
            Check(
                stream.StartGrabbing(StreamGrabStrategy.OneByOne),
                "Restart Hik software-trigger grabbing");
            _grabbing = true;
        }
    }

    public void Dispose() => Disconnect();

    private static IReadOnlyList<IDeviceInfo> EnumerateDevices()
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
            parameters.SetFloatValue(
                "ExposureTime",
                checked((float)settings.ExposureMicroseconds)),
            "Set ExposureTime");
        Check(
            parameters.SetEnumValueByString("GainAuto", "Off"),
            "Disable GainAuto");
        Check(
            parameters.SetFloatValue("Gain", checked((float)settings.Gain)),
            "Set Gain");
        Check(parameters.GetIntValue("Width", out var width), "Read Width");
        Check(parameters.GetIntValue("Height", out var height), "Read Height");
        ImageWidth = checked((int)width.CurValue);
        ImageHeight = checked((int)height.CurValue);
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
        var pixels = new byte[checked(width * height * 3)];
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

        return new ImageFrame(width, height, width * 3, pixels);
    }

    private void Disconnect()
    {
        lock (_grabGate)
        {
            if (_streamGrabber is not null && _liveFrameHandler is not null)
            {
                _streamGrabber.FrameGrabedEvent -= _liveFrameHandler;
            }

            if (_grabbing)
            {
                Check(_streamGrabber!.StopGrabbing(), "Stop Hik grabbing");
            }

            if (_device?.IsConnected == true)
            {
                Check(_device.Close(), "Close Hik camera");
            }

            _grabbing = false;
            _liveView = false;
            _liveFrameHandler = null;
            _streamGrabber = null;
            _device = null;
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
