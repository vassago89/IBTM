using System;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Xml.Linq;
using Basler.Pylon;
using static System.Collections.Specialized.BitVector32;

namespace IBTM.Device
{    public class BaslerInfo
    {
        public string Name { get; }
        public Size ImageSize { get; }
        public Camera Camera { get; }

        public BaslerInfo(
            string name,
            Size imageSize,
            Camera camera)
        {
            Name = name;
            ImageSize = imageSize;
            Camera = camera;
        }
    }

    public class BaslerService
    {
        private ObservableCollection<BaslerInfo> _cameras;
        public IEnumerable<BaslerInfo> Cameras => _cameras;

        private Action<string, Size, byte[]> _imageGrabbed;

        public BaslerService()
        {
            try
            {
                _cameras = new ObservableCollection<BaslerInfo>();

                Initialize();
            }
            catch (Exception e)
            {

            }
        }
        
        public void AddHander(
            Action<string, Size, byte[]> action)
        {
            if (_imageGrabbed != action)
                _imageGrabbed += action;
        }

        public void RemoveHandler(
            Action<string, Size, byte[]> action)
        {
            _imageGrabbed -= action;
        }

        public IEnumerable<string> GetCameras()
        {
            return Cameras.Select(c => c.Name);
        }

        public void ReverseX()
        {
            foreach (var camera in _cameras)
            {
                camera.Camera.Parameters[PLCamera.ReverseX].TrySetValue(true);
            }
        }

        public void Initialize()
        {
            foreach (var cameraInfo in CameraFinder.Enumerate())
            {
                var serialNumber = cameraInfo["SerialNumber"];
                
                var camera = new Camera(cameraInfo);
                camera.CameraOpened += Configuration.AcquireSingleFrame;
                camera.Open();
                var imageSize = new Size(
                    (int)camera.Parameters[PLCamera.Width].GetValue(),
                    (int)camera.Parameters[PLCamera.Height].GetValue());

                camera.StreamGrabber.ImageGrabbed += StreamGrabber_ImageGrabbed;
                camera.StreamGrabber.UserData = camera;
                _cameras.Add(new BaslerInfo(serialNumber, imageSize, camera));
            }
        }

        private void StreamGrabber_ImageGrabbed(object? sender, ImageGrabbedEventArgs e)
        {
            try
            {
                if (e.GrabResult == null)
                    return;

                var grabber = (IStreamGrabber)sender;
                var camera = _cameras.First(c => c.Camera == grabber.UserData);

                var converter = new PixelDataConverter()
                {
                    OutputPixelFormat = PixelType.BGRA8packed
                };

                byte[] buffer = new byte[e.GrabResult.Width * e.GrabResult.Height * 4];
                converter.Convert(buffer, e.GrabResult);

                _imageGrabbed?.Invoke(camera.Name, camera.ImageSize, buffer);
            }
            catch (Exception ex)
            {

            }
        }

        public (string name, Size size, byte[] buffer)? Grab(string serial)
        {
            var camera = _cameras.FirstOrDefault(c => c.Name == serial);
            if (camera == null || camera.Camera.StreamGrabber.IsGrabbing)
                return null;

            Configuration.AcquireSingleFrame(camera.Camera, null);
            var grabResult = camera.Camera.StreamGrabber.GrabOne(-1);

            var converter = new PixelDataConverter()
            {
                OutputPixelFormat = PixelType.BGRA8packed
            };

            byte[] buffer = new byte[grabResult.Width * grabResult.Height * 4];
            converter.Convert(buffer, grabResult);
            return (camera.Name, camera.ImageSize, buffer);
        }

        public void StartGrab(string serial)
        {
            var camera = _cameras.FirstOrDefault(c => c.Name == serial);
            if (camera == null || camera.Camera.StreamGrabber.IsGrabbing)
                return;

            Configuration.AcquireContinuous(camera.Camera, null);
            camera.Camera.StreamGrabber.Start(GrabStrategy.OneByOne, GrabLoop.ProvidedByStreamGrabber);
        }

        public void Stop()
        {
            foreach (var camera in _cameras)
            {
                camera.Camera.StreamGrabber.Stop();
            }
        }

        public void Stop(string serial)
        {
            var camera = _cameras.FirstOrDefault(c => c.Name == serial);
            if (camera == null)
                return;

            camera.Camera.StreamGrabber.Stop();
        }

        public void SetExposure(string serial, int exposure)
        {
            var camera = _cameras.FirstOrDefault(c => c.Name == serial);
            if (camera == null)
                return;

            camera.Camera.Parameters[PLCamera.ExposureTimeAbs].TrySetValue(exposure);
        }
    }
}

