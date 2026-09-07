using IBTM.Core;

namespace IBTM.Inspection;

public interface IBoltRecessSegmenter
{
    const int InputSize = 128;

    void CheckReady();
    float[] Segment(ImageFrame image);
    void Reload();
}
