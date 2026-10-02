using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Inspection;
using IBTM.UI;
using Xunit;
using ZXing;
using DataMatrixReader = IBTM.Inspection.DataMatrixReader;

namespace IBTM.Virtual.Tests;

public sealed class DataMatrixReaderTests
{
    [Fact]
    public void SearchUsesStepIncludesUpperBoundAndPrefersUndilatedResult()
    {
        var image = CreateDataMatrix(dotted: false);
        var region = new PixelRegion(0, 0, image.Width, image.Height);
        var settings = new DataMatrixInspectionRecipe
        {
            ThresholdMinimum = 0, ThresholdMaximum = 80, ThresholdStep = 10, DilationRadius = 1,
        };
        var result = DataMatrixReader.Read(image, region, settings);
        Assert.Equal("PCB-DOTS-123", result.Text);
        Assert.Equal(80, result.Threshold);
        Assert.False(result.Dilated);

        settings.ThresholdMaximum = 70;
        settings.DilationRadius = 0;
        result = DataMatrixReader.Read(image, region, settings);
        Assert.Null(result.Text);
        Assert.Equal(70, result.Threshold);
        Assert.False(result.Dilated);

        settings.ThresholdMaximum = 71;
        result = DataMatrixReader.Read(image, region, settings);
        Assert.Equal("PCB-DOTS-123", result.Text);
        Assert.Equal(71, result.Threshold); // The upper bound is tried even with step 10.
    }

    [Fact]
    public void DilationAtCurrentThresholdPrecedesPlainDecodeAtNextThreshold()
    {
        var image = CreateDataMatrix(dotted: false);
        // Dark centers form separate dots at 71; their faint edges form a solid code at 81.
        for (var y = 12; y < image.Height - 12; y++)
        {
            for (var x = 12; x < image.Width - 12; x++)
            {
                var offset = y * image.Stride + x * 3;
                if (image.Pixels[offset] == 70
                    && ((x - 12) % 6 is 0 or 5 || (y - 12) % 6 is 0 or 5))
                    image.Pixels[offset] = image.Pixels[offset + 1] = image.Pixels[offset + 2] = 80;
            }
        }
        var region = new PixelRegion(0, 0, image.Width, image.Height);
        var settings = new DataMatrixInspectionRecipe
        {
            ThresholdMinimum = 71, ThresholdMaximum = 81, ThresholdStep = 10, DilationRadius = 0,
        };
        var plain = DataMatrixReader.Read(image, region, settings);
        Assert.Equal("PCB-DOTS-123", plain.Text);
        Assert.Equal(81, plain.Threshold);
        Assert.False(plain.Dilated);
        settings.DilationRadius = 1;
        var result = DataMatrixReader.Read(image, region, settings);
        Assert.Equal(plain.Text, result.Text);
        Assert.Equal(71, result.Threshold);
        Assert.True(result.Dilated);
    }

    [Fact]
    public async Task DottedCodeUsesConfiguredDilationAndPreviewShowsActualAttempt()
    {
        var frame = CreateDataMatrix(dotted: true);
        var original = (byte[])frame.Pixels.Clone();
        var region = new PixelRegion(0, 0, frame.Width, frame.Height);
        var recipe = new Recipe();
        var settings = recipe.BoltInspection.DataMatrix2;
        settings.ThresholdMinimum = 70;
        settings.ThresholdMaximum = 71;
        settings.ThresholdStep = 5;
        settings.DilationRadius = 0;
        Assert.Null(DataMatrixReader.Read(frame, region, settings).Text);

        settings.DilationRadius = 1;
        var result = DataMatrixReader.Read(frame, region, settings);
        Assert.Equal("PCB-DOTS-123", result.Text);
        Assert.Equal(71, result.Threshold);
        Assert.True(result.Dilated);
        Assert.Equal(original, frame.Pixels);
        Assert.Equal(70, settings.ThresholdMinimum);

        var preview = new InspectionPreviewViewModel(recipe);
        preview.Clear(HeatSinkSlot.HeatSink2);
        preview.SetSavedImage(InspectionPreviewViewModel.CreateBitmap(frame), region);
        await preview.InspectAsync(CancellationToken.None);
        Assert.True(preview.Success);
        Assert.Equal(result.Text, preview.Result);
        Assert.Contains("71", preview.BinaryDescription);
        Assert.Contains(UiText.Get(" · black dot dilation"), preview.BinaryDescription);
        Assert.Equal(result.BinaryImage.Pixels, InspectionPreviewViewModel.CreateFrame(preview.Overlay!).Pixels);
        Assert.Equal(original, InspectionPreviewViewModel.CreateFrame(preview.Image!).Pixels);
    }

    [Fact]
    public void SearchRejectsInvalidParametersAndHonorsCancellation()
    {
        var settings = new DataMatrixInspectionRecipe();
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.ThresholdMinimum = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.ThresholdMaximum = 256);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.ThresholdStep = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.DilationRadius = 6);
        var image = new ImageFrame(1, 1, 3, [255, 255, 255]);
        settings.ThresholdMinimum = 80;
        settings.ThresholdMaximum = 70;
        Assert.Throws<InvalidOperationException>(() => DataMatrixReader.Read(image, new(0, 0, 1, 1), settings));
        settings.ThresholdMaximum = 80;
        Assert.Throws<OperationCanceledException>(() =>
            DataMatrixReader.Read(image, new(0, 0, 1, 1), settings, new CancellationToken(canceled: true)));
    }

    private static ImageFrame CreateDataMatrix(bool dotted)
    {
        var matrix = new ZXing.Datamatrix.DataMatrixWriter().encode("PCB-DOTS-123", BarcodeFormat.DATA_MATRIX, 0, 0);
        var width = matrix.Width * 6 + 24;
        var height = matrix.Height * 6 + 24;
        var pixels = Enumerable.Repeat((byte)90, width * height * 3).ToArray();
        for (var y = 0; y < matrix.Height; y++)
        {
            for (var x = 0; x < matrix.Width; x++)
            {
                if (!matrix[x, y])
                    continue;
                // Leave a two-pixel gap between neighboring dots; radius 1 joins them.
                for (var dy = dotted ? 1 : 0; dy < (dotted ? 5 : 6); dy++)
                {
                    for (var dx = dotted ? 1 : 0; dx < (dotted ? 5 : 6); dx++)
                    {
                        var offset = ((y * 6 + 12 + dy) * width + x * 6 + 12 + dx) * 3;
                        pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 70;
                    }
                }
            }
        }
        return new(width, height, width * 3, pixels);
    }
}
