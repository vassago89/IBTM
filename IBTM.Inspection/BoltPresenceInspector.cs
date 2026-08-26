using System.Linq;
using IBTM.Core;

namespace IBTM.Inspection;

public sealed class BoltPresenceInspector(
    BoltInspectionSettings settings,
    IBoltRecessSegmenter segmenter)
{
    public bool IsPresent(ImageFrame image)
    {
        var mask = segmenter.Segment(image);
        return (double)mask.Count(value => value >= settings.MaskThreshold)
               / mask.Length
               >= settings.MinimumMaskRatio;
    }
}
