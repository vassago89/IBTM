using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using IBTM.Device;
using IBTM.Hik;
using MvCameraControl;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class HikCameraTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void CaptureReturnsBufferAndStopsBeforeNextCapture(bool noData, bool conversionFails)
    {
        var sdk = new CameraSdk { NoData = noData, ConversionFails = conversionFails };
        using var camera = sdk.CreateCamera();

        if (noData || conversionFails)
            Assert.Throws<InvalidOperationException>(() => camera.Capture(500, 0));
        else
            Assert.Equal(1, camera.Capture(500, 0).Width);

        Assert.Equal(noData ? ["Start", "Read", "Stop"] : ["Start", "Read", "Free", "Stop"],
            sdk.Calls.ToArray());
        sdk.NoData = false;
        sdk.ConversionFails = false;
        sdk.Calls.Clear();
        camera.Capture(500, 0);
        Assert.Equal(["Start", "Read", "Free", "Stop"], sdk.Calls.ToArray());
    }

    [Fact]
    public void LiveCanRestartAndThenCaptureWithoutRestartingOnStop()
    {
        var sdk = new CameraSdk();
        using var camera = sdk.CreateCamera();
        using var frameReceived = new ManualResetEventSlim();
        var failures = new ConcurrentQueue<Exception>();
        camera.FrameReady += _ => frameReceived.Set();
        camera.LiveViewFailed += failures.Enqueue;

        for (var cycle = 0; cycle < 2; cycle++)
        {
            frameReceived.Reset();
            camera.StartLiveView(500, 0);
            try
            {
                camera.StartLiveView(500, 0);
                Assert.True(frameReceived.Wait(TimeSpan.FromSeconds(2)));
            }
            finally
            {
                camera.StopLiveView();
            }
            camera.StopLiveView();
            Assert.Empty(failures);
            var calls = sdk.Calls.ToArray();
            Assert.Equal("Start", calls[0]);
            Assert.Equal("Stop", calls[^1]);
            for (var index = 1; index < calls.Length - 1; index += 2)
            {
                Assert.Equal("Read", calls[index]);
                Assert.Equal("Free", calls[index + 1]);
            }
            sdk.Calls.Clear();
        }

        camera.Capture(500, 0);
        Assert.Equal(["Start", "Read", "Free", "Stop"], sdk.Calls.ToArray());
    }

    private sealed class CameraSdk
    {
        public readonly ConcurrentQueue<string> Calls = new();
        public bool NoData;
        public bool ConversionFails;
        private bool _grabbing;
        private bool _bufferHeld;

        public HikCamera CreateCamera()
        {
            var image = Stub<IImage>((method, _) => method.Name switch
            {
                "get_Width" or "get_Height" => 1u,
                _ => throw new NotSupportedException(method.Name)
            });
            var frame = Stub<IFrameOut>((method, _) => method.Name switch
            {
                "get_LostPacket" => 0u,
                "get_Image" => image,
                _ => throw new NotSupportedException(method.Name)
            });
            var converter = Stub<IPixelTypeConverter>((method, args) =>
            {
                Assert.Equal("ConvertPixelType", method.Name);
                args[2] = 3ul;
                return ConversionFails ? MvError.MV_E_PARAMETER : MvError.MV_OK;
            });
            var parameters = Stub<IParameters>((method, args) =>
            {
                Assert.Contains(method.Name, new[] { "SetEnumValueByString", "SetFloatValue" });
                Assert.Contains((string)args[0]!, new[] { "ExposureAuto", "ExposureTime", "GainAuto", "Gain" });
                return MvError.MV_OK;
            });
            var stream = Stub<IStreamGrabber>((method, args) =>
            {
                switch (method.Name)
                {
                    case "StartGrabbing":
                        Assert.Empty(args);
                        Assert.False(_grabbing);
                        _grabbing = true;
                        Calls.Enqueue("Start");
                        break;
                    case "GetImageBuffer":
                        Assert.True(_grabbing);
                        Assert.False(_bufferHeld);
                        Calls.Enqueue("Read");
                        Thread.Sleep(1);
                        if (NoData) return MvError.MV_E_NODATA;
                        _bufferHeld = true;
                        args[1] = frame;
                        break;
                    case "FreeImageBuffer":
                        Assert.True(_bufferHeld);
                        Assert.Same(frame, args[0]);
                        _bufferHeld = false;
                        Calls.Enqueue("Free");
                        break;
                    case "StopGrabbing":
                        Assert.True(_grabbing);
                        Assert.False(_bufferHeld);
                        _grabbing = false;
                        Calls.Enqueue("Stop");
                        break;
                    default:
                        throw new NotSupportedException(method.Name);
                }
                return MvError.MV_OK;
            });
            var device = Stub<IDevice>((method, _) => method.Name switch
            {
                "get_Parameters" => parameters,
                "get_PixelTypeConverter" => converter,
                "get_IsConnected" => true,
                "Close" => MvError.MV_OK,
                "Dispose" => null,
                _ => throw new NotSupportedException(method.Name)
            });
            var camera = new HikCamera(new InspectionCameraSettings());
            // Inject SDK interfaces without opening physical hardware or initializing the native SDK.
            typeof(HikCamera).GetField("_device", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(camera, device);
            typeof(HikCamera).GetField("_streamGrabber", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(camera, stream);
            return camera;
        }
    }

    private static T Stub<T>(Func<MethodInfo, object?[], object?> invoke) where T : class
    {
        var stub = DispatchProxy.Create<T, SdkProxy>();
        ((SdkProxy)(object)stub).Handler = invoke;
        return stub;
    }

    public class SdkProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(targetMethod!, args!);
    }
}
