using System;
using IBTM.Core;

namespace IBTM.Inspection;

public sealed class BoltPresenceDetector(
    Func<BoltInspectionRecipe> getRecipe,
    IBoltRecessSegmenter segmenter,
    Func<float> getMaskThreshold)
{
    internal void CheckReady() => segmenter.CheckReady();

    public BoltPrediction Predict(ImageFrame image)
    {
        var recipe = getRecipe();
        var input = BoltImageInput.Create(image, recipe.RegionSizePixels);
        return new(input, segmenter.Segment(input));
    }

    internal bool IsPresent(ImageFrame image) => Predict(image).IsPresent(getMaskThreshold(), getRecipe().MinimumMaskRatio);
}
