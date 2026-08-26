using System;
using System.IO;
using IBTM.Core;
using TorchSharp;
using static TorchSharp.torch;

namespace IBTM.Inspection.Training;

public sealed class TorchBoltRecessSegmenter : IBoltRecessSegmenter, IDisposable
{
    private readonly string _modelFile;
    private readonly TinyUnet _model = new();

    public TorchBoltRecessSegmenter(BoltInspectionSettings settings) : this(
        Path.Combine(AppContext.BaseDirectory, settings.ModelFile))
    {
    }

    public TorchBoltRecessSegmenter(string modelFile)
    {
        _modelFile = modelFile;
        Reload();
    }

    public void Reload()
    {
        _model.load(_modelFile);
        _model.eval();
    }

    public float[] Segment(ImageFrame image)
    {
        using var scope = NewDisposeScope();
        using var inference = no_grad();
        using var input = tensor(CreateInput(image), dtype: ScalarType.Float32)
            .reshape(
                1,
                3,
                IBoltRecessSegmenter.InputSize,
                IBoltRecessSegmenter.InputSize);
        using var mask = _model.call(input).sigmoid().cpu();
        return mask.data<float>().ToArray();
    }

    public void Dispose() => _model.Dispose();

    private static float[] CreateInput(ImageFrame image)
    {
        var size = IBoltRecessSegmenter.InputSize;
        var input = new float[3 * size * size];
        var plane = size * size;
        var left = (image.Width - size) / 2;
        var top = (image.Height - size) / 2;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var source = ((top + y) * image.Stride)
                             + ((left + x) * 3);
                var target = (y * size) + x;
                input[target] = image.Pixels[source + 2] / 255f;
                input[plane + target] = image.Pixels[source + 1] / 255f;
                input[(2 * plane) + target] = image.Pixels[source] / 255f;
            }
        }

        return input;
    }
}
