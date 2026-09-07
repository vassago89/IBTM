using System;
using System.IO;
using IBTM.Core;
using TorchSharp;
using static TorchSharp.torch;

namespace IBTM.Inspection.Training;

public sealed class TorchBoltRecessSegmenter : IBoltRecessSegmenter, IDisposable
{
    private readonly Func<string> _modelFile;
    private string? _loadedModelFile;
    private TinyUnet? _model;

    public TorchBoltRecessSegmenter(BoltInspectionSettings settings)
    {
        _modelFile = () => Path.Combine(AppContext.BaseDirectory, settings.ModelFile);
    }

    public TorchBoltRecessSegmenter(string modelFile)
    {
        _modelFile = () => modelFile;
    }

    public void CheckReady()
    {
        if (_loadedModelFile != _modelFile())
        {
            Reload();
        }
    }

    public void Reload()
    {
        var modelFile = _modelFile();
        var model = new TinyUnet();
        try
        {
            model.load(modelFile);
            model.eval();
        }
        catch
        {
            model.Dispose();
            throw;
        }

        _model?.Dispose();
        _model = model;
        _loadedModelFile = modelFile;
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
        var left = (image.Width - size) / 2;
        var top = (image.Height - size) / 2;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var source = ((top + y) * image.Stride)
                             + ((left + x) * ImageFrame.ColorChannelCount);
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
