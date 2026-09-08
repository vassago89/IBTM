using IBTM.Core;
using IBTM.Inspection;

namespace IBTM.Virtual;

public sealed class VirtualBoltRecessSegmenter : IBoltRecessSegmenter
{
    public void CheckReady()
    {
    }

    public void Reload()
    {
    }

    public float[] Segment(ImageFrame image)
    {
        var size = IBoltRecessSegmenter.InputSize;
        var mask = new float[size * size];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var source = y * image.Stride + x * ImageFrame.ColorChannelCount;
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
