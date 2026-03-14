using OpenCvSharp;

namespace IBTM.Services;

/// <summary>
/// OpenCvSharp 기반 Fiducial 마크 검출 서비스
/// BaslerService에서 카메라 이미지를 받아 Template Matching으로 위치 오프셋 계산
/// </summary>
public class FiducialService : IFiducialService, IDisposable
{
    private Mat? _template;

    // 카메라 캘리브레이션: 픽셀당 mm (실측 후 조정 필요)
    private const double PixelsPerMm = 50.0;
    private const double MatchThreshold = 0.75;

    public void LoadTemplate(string templatePath)
    {
        _template?.Dispose();
        _template = Cv2.ImRead(templatePath, ImreadModes.Grayscale);
    }

    public void SetTemplate(Mat template)
    {
        _template?.Dispose();
        _template = template.Clone();
    }

    public async Task<FiducialResult> DetectFromCameraAsync(CancellationToken ct = default)
    {
        // TODO: BaslerService에서 실제 카메라 프레임 수신으로 교체
        await Task.Delay(400, ct);
        return SimulateDetection();
    }

    public async Task<FiducialResult> DetectAsync(Mat sourceImage, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            if (_template == null)
                return SimulateDetection();

            return MatchTemplate(sourceImage);
        }, ct);
    }

    private FiducialResult MatchTemplate(Mat src)
    {
        if (_template == null) return new FiducialResult(false, 0, 0, 0);

        try
        {
            using var gray = src.Channels() > 1
                ? src.CvtColor(ColorConversionCodes.BGR2GRAY)
                : src.Clone();

            using var result = new Mat();
            Cv2.MatchTemplate(gray, _template, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);

            if (maxVal < MatchThreshold)
                return new FiducialResult(false, 0, 0, maxVal);

            // 이미지 중심 대비 검출 위치 → mm 오프셋 계산
            double centerX = gray.Width / 2.0;
            double centerY = gray.Height / 2.0;
            double foundX = maxLoc.X + _template.Width / 2.0;
            double foundY = maxLoc.Y + _template.Height / 2.0;

            double offsetX = (foundX - centerX) / PixelsPerMm;
            double offsetY = (foundY - centerY) / PixelsPerMm;

            return new FiducialResult(true, offsetX, offsetY, maxVal);
        }
        catch
        {
            return new FiducialResult(false, 0, 0, 0);
        }
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
