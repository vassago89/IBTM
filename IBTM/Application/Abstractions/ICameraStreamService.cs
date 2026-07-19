using System.Windows.Media;

namespace IBTM.Application.Abstractions;

/// <summary>
/// 카메라 라이브 스트리밍 추상화 (Zone 3 티칭용)
/// </summary>
public interface ICameraStreamService
{
    /// <summary>새 프레임 수신 시 발생 (BitmapSource는 Freeze 상태)</summary>
    event Action<ImageSource>? FrameReady;

    /// <summary>라이브 뷰 시작</summary>
    void StartLiveView();

    /// <summary>라이브 뷰 중지</summary>
    void StopLiveView();

    /// <summary>현재 라이브 뷰 실행 중 여부</summary>
    bool IsLive { get; }

    /// <summary>이미지 해상도 (픽셀)</summary>
    int ImageWidth { get; }
    int ImageHeight { get; }
}
