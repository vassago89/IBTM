using System;
using IBTM.Core;

namespace IBTM.Inspection;

public sealed class BoltPresenceDetector(
    BoltInspectionSettings settings,
    IBoltRecessSegmenter segmenter)
{
    public bool IsPresent(ImageFrame image)
    {
        var mask = segmenter.Segment(image);
        var required = (int)Math.Ceiling(
            mask.Length * settings.MinimumMaskRatio);
        if (required <= 0)
        {
            return true;
        }

        var count = 0;
        foreach (var value in mask)
        {
            if (value >= settings.MaskThreshold && ++count >= required)
            {
                return true;
            }
        }

        return false;
    }
}
