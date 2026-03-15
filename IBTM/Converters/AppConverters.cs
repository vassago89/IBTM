using IBTM.Models;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace IBTM.Converters;

// ── StageStatus → Border/Icon 색상 ─────────────────────────────────────────
[ValueConversion(typeof(StageStatus), typeof(Brush))]
public class StageStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not StageStatus status) return Brushes.Transparent;
        return status switch
        {
            StageStatus.Running => new SolidColorBrush(Color.FromRgb(0x58, 0xA6, 0xFF)),  // #58A6FF 파랑
            StageStatus.Done    => new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)),  // #3FB950 초록
            StageStatus.Error   => new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49)),  // #F85149 빨강
            StageStatus.Warning => new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x3E)),  // #F0883E 주황
            _                   => new SolidColorBrush(Color.FromRgb(0x48, 0x4F, 0x58))   // #484F58 회색
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ── StageStatus → 카드 배경색 ──────────────────────────────────────────────
[ValueConversion(typeof(StageStatus), typeof(Brush))]
public class StageStatusToBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not StageStatus status) return Brushes.Transparent;
        return status switch
        {
            StageStatus.Running => new SolidColorBrush(Color.FromRgb(0x1C, 0x2C, 0x54)),  // 파란 틴트
            StageStatus.Done    => new SolidColorBrush(Color.FromRgb(0x1A, 0x2E, 0x20)),  // 초록 틴트
            StageStatus.Error   => new SolidColorBrush(Color.FromRgb(0x31, 0x1A, 0x1A)),  // 빨간 틴트
            StageStatus.Warning => new SolidColorBrush(Color.FromRgb(0x30, 0x22, 0x14)),  // 주황 틴트
            _                   => new SolidColorBrush(Color.FromRgb(0x1C, 0x20, 0x33))   // 기본 카드색
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ── LogLevel → 색상 ────────────────────────────────────────────────────────
[ValueConversion(typeof(LogLevel), typeof(Brush))]
public class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not LogLevel level) return Brushes.White;
        return level switch
        {
            LogLevel.Error   => new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49)),
            LogLevel.Warning => new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x3E)),
            LogLevel.Info    => new SolidColorBrush(Color.FromRgb(0xC9, 0xD1, 0xD9)),
            _                => new SolidColorBrush(Color.FromRgb(0x48, 0x4F, 0x58))
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ── bool 반전 → Visibility ─────────────────────────────────────────────────
[ValueConversion(typeof(bool), typeof(Visibility))]
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ── bool → Visibility ─────────────────────────────────────────────────────
[ValueConversion(typeof(bool), typeof(Visibility))]
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ── StageStatus → 텍스트 굵기 ─────────────────────────────────────────────
[ValueConversion(typeof(StageStatus), typeof(FontWeight))]
public class StageStatusToFontWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is StageStatus.Running ? FontWeights.Bold : FontWeights.Normal;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ── InspectionResult → 색상 ───────────────────────────────────────────────
[ValueConversion(typeof(string), typeof(Brush))]
public class InspectionResultToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value?.ToString() ?? string.Empty;
        if (text.Contains("GOOD")) return new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
        if (text.Contains("NG"))   return new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));
        return new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ── bool → 불투명도 (활성: 1.0 / 비활성: 0.28) ─────────────────────────────
[ValueConversion(typeof(bool), typeof(double))]
public class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? 1.0 : 0.28;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ── Z 게이지 Top 위치 (바 높이 → Canvas.Top) ─────────────────────────────────
// 게이지가 아래에서 위로 채워지도록: Top = BaseTop + MaxHeight - fillHeight
[ValueConversion(typeof(double), typeof(double))]
public class ZGaugeTopConverter : IValueConverter
{
    public double GaugeTop { get; set; } = 30;
    public double GaugeHeight { get; set; } = 60;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var fillHeight = value is double h ? h : 0.0;
        return GaugeTop + GaugeHeight - fillHeight;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ── bool → NG/GOOD 채움 색상 (슬롯 시각화) ───────────────────────────────────
[ValueConversion(typeof(bool), typeof(Brush))]
public class NgSlotFillConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true
            ? new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49))   // 채움: 빨강
            : new SolidColorBrush(Color.FromRgb(0x2D, 0x33, 0x3B));  // 빈 칸: 어두운 회색
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
