using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IBTM.Core;

namespace IBTM.Inspection.Training;

public static class BitmapFiles
{
    public static ImageFrame LoadFrame(string path) => ToFrame(Load(path));

    public static BitmapSource Load(string path)
    {
        using var stream = File.OpenRead(path);
        var image = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0];
        image.Freeze();
        return image;
    }

    public static ImageFrame ToFrame(BitmapSource source)
    {
        var image = new FormatConvertedBitmap(
            source,
            PixelFormats.Bgr24,
            null,
            0);
        var stride = image.PixelWidth * ImageFrame.ColorChannelCount;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        return new ImageFrame(
            image.PixelWidth,
            image.PixelHeight,
            stride,
            pixels);
    }
}
