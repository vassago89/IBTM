using OpenCvSharp;
using System.IO;

namespace IBTM.Infrastructure.Vision;

/// <summary>
/// OpenCvSharp 기반 Fiducial 마크 검출 서비스
/// BaslerService에서 카메라 이미지를 받아 Template Matching으로 위치 오프셋 계산
/// </summary>
public sealed class FiducialService : IFiducialService, IDisposable
{
    private readonly MachineConfig _config;
    private Mat? _template;

    public FiducialService(MachineConfig config)
    {
        _config = config;
    }

    public void LoadTemplate(string templatePath)
    {
        _template?.Dispose();
        _template = Cv2.ImRead(templatePath, ImreadModes.Grayscale);
        if (_template.Empty())
        {
            _template.Dispose();
            _template = null;
            throw new InvalidDataException($"Unable to load fiducial template '{templatePath}'.");
        }
    }

    public void SetTemplate(Mat template)
    {
        if (template.Empty())
        {
            throw new ArgumentException("Fiducial template must not be empty.", nameof(template));
        }

        _template?.Dispose();
        _template = template.Clone();
    }

    public async Task<FiducialResult> DetectFromCameraAsync(CancellationToken ct = default)
    {
        if (!_config.SimulationMode)
        {
            throw new NotSupportedException("A physical fiducial camera adapter has not been configured.");
        }

        await Task.Delay(400, ct);
        return SimulateDetection();
    }

    public Task<FiducialResult> DetectAsync(Mat sourceImage, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            if (_template == null)
                return SimulateDetection();

            return MatchTemplate(sourceImage);
        }, ct);
    }

    private FiducialResult MatchTemplate(Mat src)
    {
        if (_template == null || src.Empty()) return new FiducialResult(false, 0, 0, 0);
        if (_config.PixelsPerMm <= 0 || !double.IsFinite(_config.PixelsPerMm))
        {
            throw new InvalidOperationException("Pixels-per-mm must be finite and greater than zero.");
        }

        using var gray = src.Channels() > 1
            ? src.CvtColor(ColorConversionCodes.BGR2GRAY)
            : src.Clone();

        using var result = new Mat();
        Cv2.MatchTemplate(gray, _template, result, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);

        if (maxVal < _config.FiducialMatchThreshold)
            return new FiducialResult(false, 0, 0, maxVal);

        // 이미지 중심 대비 검출 위치 → mm 오프셋 계산
        double centerX = gray.Width / 2.0;
        double centerY = gray.Height / 2.0;
        double foundX = maxLoc.X + _template.Width / 2.0;
        double foundY = maxLoc.Y + _template.Height / 2.0;

        double offsetX = (foundX - centerX) / _config.PixelsPerMm;
        double offsetY = (foundY - centerY) / _config.PixelsPerMm;

        return new FiducialResult(true, offsetX, offsetY, maxVal);
    }

    private static FiducialResult SimulateDetection()
    {
        // 시뮬레이션: 소량의 랜덤 오프셋
        var rnd = Random.Shared;
        double offsetX = (rnd.NextDouble() - 0.5) * 0.4;
        double offsetY = (rnd.NextDouble() - 0.5) * 0.4;
        return new FiducialResult(true, offsetX, offsetY, 0.93 + rnd.NextDouble() * 0.06);
    }

    public void Dispose() => _template?.Dispose();
}
