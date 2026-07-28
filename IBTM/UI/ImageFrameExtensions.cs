using System.Windows.Media;
using System.Windows.Media.Imaging;
using IBTM.Device;

namespace IBTM.UI;

public static class ImageFrameExtensions
{
    public static ImageSource ToImageSource(this ImageFrame frame)
    {
        var image = BitmapSource.Create(
            frame.Width,
            frame.Height,
            96,
            96,
            PixelFormats.Bgr24,
            palette: null,
            frame.Pixels,
            frame.Stride);
        image.Freeze();
        return image;
    }
}
