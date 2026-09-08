using System;
using System.IO;
using IBTM.Core;
using TorchSharp;
using static TorchSharp.torch;

namespace IBTM.Inspection.Training;

public sealed class TorchBoltRecessSegmenter : IBoltRecessSegmenter, IDisposable
{
    private readonly Func<byte[]> _loadWeights;
    private TinyUnet? _model;

    public TorchBoltRecessSegmenter(BoltTrainingStore store)
    {
        _loadWeights = () => store.LoadModel().Weights;
    }

    internal TorchBoltRecessSegmenter(byte[] weights)
    {
        _loadWeights = () => weights;
    }

    public void CheckReady()
    {
        if (_model is null)
        {
            Reload();
        }
    }

    public void Reload()
    {
        _model?.Dispose();
        _model = null;
        using var stream = new MemoryStream(_loadWeights(), writable: false);
        var model = new TinyUnet();
        try
        {
            model.load(stream);
            model.eval();
        }
        catch
        {
            model.Dispose();
            throw;
        }

        _model = model;
    }

    public float[] Segment(ImageFrame image)
    {
        CheckReady();

        using var scope = NewDisposeScope();
        using var inference = no_grad();
        var input = tensor(CreateInput(image), dtype: ScalarType.Float32)
            .reshape(
                1,
                ImageFrame.ColorChannelCount,
                IBoltRecessSegmenter.InputSize,
                IBoltRecessSegmenter.InputSize);
        var mask = _model!.call(input).sigmoid().cpu();
        return mask.data<float>().ToArray();
    }

    public void Dispose() => _model?.Dispose();

    internal static float[] CreateInput(ImageFrame image)
    {
        var size = IBoltRecessSegmenter.InputSize;
        var input = new float[ImageFrame.ColorChannelCount * size * size];
        var plane = size * size;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var source = y * image.Stride + x * ImageFrame.ColorChannelCount;
                var target = (y * size) + x;
                input[target] = image.Pixels[source + ImageFrame.RedChannel]
                                / (float)byte.MaxValue;
                input[plane + target] =
                    image.Pixels[source + ImageFrame.GreenChannel]
                    / (float)byte.MaxValue;
                input[(2 * plane) + target] =
                    image.Pixels[source + ImageFrame.BlueChannel]
                    / (float)byte.MaxValue;
            }
        }

        return input;
    }
}
