using System;
using System.Linq;
using System.Text.Json;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.Storage;
using IBTM.UI;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class FiducialTests
{
    [Fact]
    public void ContoursFindShiftedMarksAndRejectWrongSizeAndMissingMarks()
    {
        var reference = CircleImage((100, 100, 14));
        var mark = new FiducialMark { SearchSizePixels = 100, ReferenceDiameterMm = 2.8, WhiteMark = true };
        var registered = FiducialDetector.Detect(reference, mark, 0.1, registering: true);
        mark.Contour = registered.Contour;
        mark.ImageWidth = reference.Width;
        mark.ImageHeight = reference.Height;
        var found = FiducialDetector.Detect(CircleImage((112, 93, 14)), mark, 0.1);
        Assert.Equal(112, found.Center.X, 6);
        Assert.Equal(93, found.Center.Y, 6);
        Assert.InRange(found.ShapeError, 0, mark.MaximumShapeError);
        var outside = CircleImage((170, 100, 10));
        Assert.Throws<FiducialDetectionException>(() => FiducialDetector.Detect(outside, mark, 0.1));
        Assert.Throws<FiducialDetectionException>(() => FiducialDetector.Detect(outside, mark, 0.1, registering: true));
        var darkHole = CircleImage((112, 93, 14));
        for (var i = 0; i < darkHole.Pixels.Length; i++)
            darkHole.Pixels[i] = (byte)(255 - darkHole.Pixels[i]);
        mark.WhiteMark = false;
        Assert.Equal(found.Center, FiducialDetector.Detect(darkHole, mark, 0.1).Center);
        mark.WhiteMark = true;
        Assert.Throws<FiducialDetectionException>(() => FiducialDetector.Detect(CircleImage((100, 100, 23)), mark, 0.1));
        Assert.Throws<FiducialDetectionException>(() => FiducialDetector.Detect(CircleImage(), mark, 0.1));
        Assert.Throws<FiducialDetectionException>(() => FiducialDetector.Detect(CircleImage((70, 100, 14), (130, 100, 14)), mark, 0.1, registering: true));
        Assert.Throws<InvalidOperationException>(() => FiducialDetector.Detect(new(20, 20, 60, new byte[1200]), mark, 0.1));
    }

    [Fact]
    public void FilledMaskRanksCandidatesWithTheSameOutlineAndReportsTheBestScoreBelowThreshold()
    {
        var mark = new FiducialMark { ReferenceDiameterMm = 2.8, SearchSizePixels = 100, WhiteMark = true };
        var image = CircleImage((100, 100, 14));
        var reference = FiducialDetector.Detect(image, mark, 0.1, registering: true);
        Assert.Null(reference.MatchScore);
        mark.Contour = reference.Contour;
        mark.ImageWidth = image.Width;
        mark.ImageHeight = image.Height;

        var pair = CircleImage((72, 100, 14), (124, 100, 14));
        for (var y = 92; y <= 108; y++)
            Array.Clear(pair.Pixels, y * pair.Stride + 116 * 3, 17 * 3);
        mark.MinimumMatchScore = 100;
        var found = FiducialDetector.Detect(pair, mark, 0.1);
        Assert.Equal(72, found.Center.X, 6);
        Assert.Equal(100d, found.MatchScore);

        for (var y = 86; y <= 114; y++)
            Array.Clear(pair.Pixels, y * pair.Stride + 58 * 3, 29 * 3);
        mark.MinimumMatchScore = 0;
        var hollow = FiducialDetector.Detect(pair, mark, 0.1);
        Assert.Equal(124, hollow.Center.X, 6);
        Assert.InRange(hollow.MatchScore!.Value, 0, 98);
        mark.MinimumMatchScore = 99;
        var failure = Assert.Throws<FiducialDetectionException>(() => FiducialDetector.Detect(pair, mark, 0.1));
        Assert.Equal(UiText.Format($"Best match score {hollow.MatchScore:0.##} (minimum {mark.MinimumMatchScore})"), failure.Message);
    }

    [Fact]
    public void CalibratedDiameterFiltersRegistrationAndOverridesLegacyContourSize()
    {
        var image = CircleImage((100, 100, 14));
        var mark = new FiducialMark { ReferenceDiameterMm = 2.8, WhiteMark = true };
        var found = FiducialDetector.Detect(image, mark, 0.1, registering: true);
        Assert.InRange(found.RadiusPixels * 0.1, 1.3, 1.4);
        var sizeError = Assert.Throws<FiducialDetectionException>(() => FiducialDetector.Detect(image, mark, 0.05, registering: true));
        Assert.Contains("2.24", sizeError.Message);
        Assert.Contains("3.36", sizeError.Message);
        Assert.Throws<InvalidOperationException>(() => FiducialDetector.Detect(image, mark, null, registering: true));
        mark.Contour = found.Contour;
        mark.ImageWidth = image.Width;
        mark.ImageHeight = image.Height;
        mark.ReferenceDiameterMm = null;
        Assert.Equal(2 * found.RadiusPixels * 0.1, FiducialDetector.GetReferenceDiameterMm(mark, 0.1)!.Value, 8);
        Assert.Equal(found.Center, FiducialDetector.Detect(image, mark, 0.1).Center);

        mark.ReferenceDiameterMm = 4;
        Assert.Throws<FiducialDetectionException>(() => FiducialDetector.Detect(image, mark, 0.1));
        var larger = CircleImage((100, 100, 21));
        var largerReference = FiducialDetector.Detect(larger, mark, 0.1, registering: true);
        var maskError = Assert.Throws<FiducialDetectionException>(() => FiducialDetector.Detect(larger, mark, 0.1));
        mark.MinimumMatchScore = 0;
        var oldMask = FiducialDetector.Detect(larger, mark, 0.1);
        Assert.Equal(UiText.Format($"Best match score {oldMask.MatchScore:0.##} (minimum {50})"), maskError.Message);
        mark.MinimumMatchScore = 50;
        mark.Contour = largerReference.Contour;
        Assert.Equal(found.Center, FiducialDetector.Detect(larger, mark, 0.1).Center);
    }

    [Fact]
    public void FailureIdentifiesClippedContourAndReportsTheSearchWidth()
    {
        var mark = new FiducialMark { SearchSizePixels = 26, ReferenceDiameterMm = 2.8, WhiteMark = true };
        var clipped = Assert.Throws<FiducialDetectionException>(() => FiducialDetector.Detect(CircleImage((100, 100, 14)), mark, 0.1, registering: true));
        Assert.Equal(UiText.Format($"{1} contours touch the edge of the {26} px search area"), clipped.Message);
        var empty = Assert.Throws<FiducialDetectionException>(() => FiducialDetector.Detect(CircleImage(), mark, 0.1, registering: true));
        Assert.Equal(UiText.Format($"No foreground contour at threshold {127}"), empty.Message);
    }

    [Fact]
    public void LegacyRadiusLoadsAsDiameterAndSavesOnlyDiameter()
    {
        var mark = JsonSerializer.Deserialize<FiducialMark>("""{"ReferenceRadiusMm":1.25}""")!;
        Assert.Equal(2.5, mark.ReferenceDiameterMm);
        Assert.Equal(50, mark.MinimumMatchScore);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(mark));
        Assert.Equal(2.5, json.RootElement.GetProperty(nameof(FiducialMark.ReferenceDiameterMm)).GetDouble());
        Assert.False(json.RootElement.TryGetProperty("ReferenceRadiusMm", out _));
    }

    [Fact]
    public void MatchingRegisteredContourCanPassBelowTheFormerCircularityCutoff()
    {
        var mark = new FiducialMark { ReferenceDiameterMm = 3, WhiteMark = true };
        var image = CircleImage();
        for (var y = 95; y <= 105; y++)
            Array.Fill(image.Pixels, (byte)220, y * image.Stride + 68 * 3, 65 * 3);

        var registered = FiducialDetector.Detect(image, mark, 0.1, registering: true);
        Assert.True(registered.Circularity < 0.8);
        mark.Contour = registered.Contour;
        mark.ImageWidth = image.Width;
        mark.ImageHeight = image.Height;
        var found = FiducialDetector.Detect(image, mark, 0.1);
        Assert.Equal(registered.Center, found.Center);
        Assert.InRange(found.ShapeError, 0, mark.MaximumShapeError);
        Assert.Throws<FiducialDetectionException>(() => FiducialDetector.Detect(CircleImage((100, 100, 14)), mark, 0.1));
    }

    [Fact]
    public void TwoMarksCorrectTranslationAndRotationWithoutScalingOrMutatingTeaching()
    {
        var alignment = new PcbAlignment(DateTimeOffset.Now,
            new(new(0, 0), new(10, 20), 0), new(new(10, 0), new(10, 30), 0));
        var taught = new AxisPosition { X = 3, Y = 4, Z = 5 };
        var corrected = alignment.Transform(taught);
        Assert.Equal(90, alignment.AngleDegrees, 8);
        Assert.Equal(6, corrected.X, 8);
        Assert.Equal(23, corrected.Y, 8);
        Assert.Equal(5, corrected.Z);
        Assert.Equal((3d, 4d, 5d), (taught.X, taught.Y, taught.Z));
        var longer = alignment with { Second = new(new(10, 0), new(10, 32), 0) };
        Assert.Equal(10, double.Hypot(longer.Transform(new() { X = 10 }).X - longer.Transform(new()).X,
            longer.Transform(new() { X = 10 }).Y - longer.Transform(new()).Y), 8);
    }

    [Fact]
    public void CameraResolutionPersistsAndConvertsImageRightToPositiveXAndDownToNegativeY()
    {
        var settings = new InspectionGantrySettings();
        Assert.Throws<InvalidOperationException>(() => settings.ToMachinePosition(new(), new(120, 108), 200, 200));
        settings.CameraResolution = 0.0125;
        foreach (var invalid in new[] { 0, -0.01, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => settings.CameraResolution = invalid);
        Assert.Equal(0.0125, settings.CameraResolution);
        var store = VirtualTestSupport.OpenMachineStore();
        store.SaveSettings([settings]);
        var saved = new MachineStore(store.DatabaseFile).LoadSettings().Get<InspectionGantrySettings>();
        Assert.Equal(0.0125, saved.CameraResolution);
        var camera = new AxisPosition { X = 3, Y = 4 };
        Assert.Equal(new(3, 4), saved.ToMachinePosition(camera, new(100, 100), 200, 200));
        var position = saved.ToMachinePosition(camera, new(120, 108), 200, 200);
        Assert.Equal(3.25, position.X, 8);
        Assert.Equal(3.9, position.Y, 8);
    }

    [Fact]
    public void RecipeRetainsMarksWithoutChangingBoltsAndReteachingInvalidatesContour()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var recipes = new RecipeManager(store, new());
        var bolt = new BoltPoint { X = 3, Y = 4, FasteningX = 5, FasteningY = 6 };
        recipes.Current.Pcb.BoltPoints = [bolt];
        var mark = recipes.Current.BoltInspection.Fiducials2.First;
        mark.SearchSizePixels = 80;
        mark.ReferenceDiameterMm = 2.8;
        mark.WhiteMark = true;
        mark.MarkPosition = new() { X = 30, Y = 40 };
        mark.MinimumMatchScore = 83;
        mark.Contour = FiducialDetector.Detect(CircleImage((100, 100, 14)), mark, 0.1, registering: true).Contour;
        mark.ImageWidth = mark.ImageHeight = 200;
        store.SaveRecipe(recipes.Current);
        var loaded = new MachineStore(store.DatabaseFile).LoadRecipe(recipes.Current.Name);
        Assert.Equal(mark.ReferenceDiameterMm, loaded.BoltInspection.Fiducials2.First.ReferenceDiameterMm);
        Assert.Equal(mark.SearchSizePixels, loaded.BoltInspection.Fiducials2.First.SearchSizePixels);
        Assert.Equal(83, loaded.BoltInspection.Fiducials2.First.MinimumMatchScore);
        Assert.Equal(50, loaded.BoltInspection.Fiducials2.Second.MinimumMatchScore);
        Assert.Equal(mark.Contour, loaded.BoltInspection.Fiducials2.First.Contour);
        Assert.Empty(loaded.BoltInspection.Fiducials1.First.Contour);
        var saved = Assert.Single(loaded.Pcb.BoltPoints);
        Assert.Equal((3d, 4d, 5d, 6d), (saved.X, saved.Y, saved.FasteningX, saved.FasteningY));
        var point = new TeachingPoint(new(TeachingTarget.Fiducial1, MotionGroup.InspectionGantry, TeachMode.XYOnly),
            new(), recipes, HeatSinkSlot.HeatSink2);
        point.Teach(31, 41, 0);
        Assert.Empty(mark.Contour);
        Assert.Equal((31d, 41d), (mark.MarkPosition.X, mark.MarkPosition.Y));
    }

    internal static ImageFrame CircleImage(params (int X, int Y, int Radius)[] circles)
    {
        var pixels = new byte[200 * 200 * 3];
        for (var y = 0; y < 200; y++)
        for (var x = 0; x < 200; x++)
        {
            if (!circles.Any(circle => (x - circle.X) * (x - circle.X) + (y - circle.Y) * (y - circle.Y) <= circle.Radius * circle.Radius))
                continue;
            for (var channel = 0; channel < 3; channel++)
                pixels[(y * 200 + x) * 3 + channel] = 220;
        }
        return new(200, 200, 600, pixels);
    }
}
