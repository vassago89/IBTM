using IBTM.Core;
using IBTM.Inspection;

namespace IBTM.Virtual;

public sealed class VirtualBoltRecessSegmenter : IBoltRecessSegmenter
{
    public void Reload()
    {
    }

    public float[] Segment(ImageFrame image)
    {
        var size = IBoltRecessSegmenter.InputSize;
        var left = (image.Width - size) / 2;
        var top = (image.Height - size) / 2;
        var mask = new float[size * size];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var source = ((top + y) * image.Stride)
                             + ((left + x) * ImageFrame.ColorChannelCount);
                if (image.Pixels[source]
                    == VirtualImageFactory.BoltRecessIntensity)
                {
                    mask[(y * size) + x] = 1;
                }
            }
        }

        return mask;
    }
}
