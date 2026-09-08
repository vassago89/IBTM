using IBTM.Core;

namespace IBTM.Inspection;

public interface IBoltRecessSegmenter
{
    const int InputSize = 128;

    void CheckReady();
    // Input is the normalized InputSize x InputSize BGR image, not a camera frame.
    float[] Segment(ImageFrame image);
    void Reload();
}
