using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace IBTM.Services;

/// <summary>
/// 시뮬레이션용 가상 카메라 스트리밍 (개발/테스트용)
/// 어두운 배경 + 십자선 + 노이즈 패턴 생성
/// </summary>
public class VirtualCameraStreamService : ICameraStreamService
{
    private DispatcherTimer? _timer;
    private readonly Random _rng = new();

    public event Action<ImageSource>? FrameReady;

    public bool IsLive { get; private set; }
    public int ImageWidth => 320;
    public int ImageHeight => 240;

    public void StartLiveView()
    {
        if (IsLive) return;
        IsLive = true;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(66) }; // ~15fps
        _timer.Tick += (_, _) => GenerateFrame();
        _timer.Start();
    }

    public void StopLiveView()
    {
        _timer?.Stop();
        _timer = null;
        IsLive = false;
    }

    private void GenerateFrame()
    {
        const int w = 320, h = 240;
        var pixels = new byte[w * h * 3];

        // 어두운 배경 + 노이즈
        for (int i = 0; i < pixels.Length; i += 3)
        {
            byte v = (byte)(18 + _rng.Next(12));
            pixels[i] = v; pixels[i + 1] = v; pixels[i + 2] = v;
        }

        // 중앙 밝은 영역 (PCB 시뮬레이션)
        for (int y = 50; y < 190; y++)
            for (int x = 60; x < 260; x++)
            {
                int idx = (y * w + x) * 3;
                byte v = (byte)(45 + _rng.Next(15));
                pixels[idx] = v; pixels[idx + 1] = v; pixels[idx + 2] = v;
            }

        // 십자선 (중앙, 시안색)
        int cx = w / 2, cy = h / 2;
        for (int x = 0; x < w; x++)
        {
            int idx = (cy * w + x) * 3;
            pixels[idx] = 80; pixels[idx + 1] = 160; pixels[idx + 2] = 0; // BGR: cyan
        }
        for (int y = 0; y < h; y++)
        {
            int idx = (y * w + cx) * 3;
            pixels[idx] = 80; pixels[idx + 1] = 160; pixels[idx + 2] = 0;
        }

        // 볼트 시뮬레이션 (밝은 점 4개)
        int[][] bolts = [[120, 90], [200, 90], [120, 150], [200, 150]];
        foreach (var b in bolts)
            for (int dy = -5; dy <= 5; dy++)
                for (int dx = -5; dx <= 5; dx++)
                    if (dx * dx + dy * dy <= 25)
                    {
                        int px = b[0] + dx, py = b[1] + dy;
                        if (px >= 0 && px < w && py >= 0 && py < h)
                        {
                            int idx = (py * w + px) * 3;
                            pixels[idx] = (byte)(120 + _rng.Next(30));
                            pixels[idx + 1] = (byte)(120 + _rng.Next(30));
                            pixels[idx + 2] = (byte)(120 + _rng.Next(30));
                        }
                    }

        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr24, null);
        bmp.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), pixels, w * 3, 0);
        bmp.Freeze();

        FrameReady?.Invoke(bmp);
    }
}
