using System.Linq;
using IBTM.Core;

namespace IBTM.Inspection;

public sealed class BoltPrediction(ImageFrame input, float[] probabilities)
{
    private readonly float[] _sorted = probabilities.Order().ToArray();
    public ImageFrame Input { get; } = input;
    public float[] Probabilities { get; } = probabilities;

    public double MaskRatio(float threshold)
    {
        var low = 0;
        var high = _sorted.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_sorted[middle] >= threshold) high = middle;
            else low = middle + 1;
        }
        return (double)(_sorted.Length - low) / _sorted.Length;
    }

    public bool IsPresent(float maskThreshold, double minimumMaskRatio) =>
        MaskRatio(maskThreshold) >= minimumMaskRatio;
}
