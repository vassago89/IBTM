using IBTM.Models;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace IBTM.Converters;

/// <summary>Frozen 브러시 팔레트 (매번 new 방지)</summary>
internal static class Palette
{
    // 공통 색상
    public static readonly Brush Blue    = Freeze(0x58, 0xA6, 0xFF);
    public static readonly Brush Green   = Freeze(0x3F, 0xB9, 0x50);
    public static readonly Brush Red     = Freeze(0xF8, 0x51, 0x49);
    public static readonly Brush Orange  = Freeze(0xF0, 0x88, 0x3E);
    public static readonly Brush Gray    = Freeze(0x48, 0x4F, 0x58);
    public static readonly Brush DimGray = Freeze(0x38, 0x3C, 0x46);
    public static readonly Brush Light   = Freeze(0xC9, 0xD1, 0xD9);
    public static readonly Brush Muted   = Freeze(0x8B, 0x94, 0x9E);

    // 카드 배경
    public static readonly Brush BgBlue    = Freeze(0x1C, 0x2C, 0x54);
    public static readonly Brush BgGreen   = Freeze(0x1A, 0x2E, 0x20);
    public static readonly Brush BgRed     = Freeze(0x31, 0x1A, 0x1A);
    public static readonly Brush BgOrange  = Freeze(0x30, 0x22, 0x14);
    public static readonly Brush BgDim     = Freeze(0x14, 0x16, 0x1E);
    public static readonly Brush BgDefault = Freeze(0x1C, 0x20, 0x33);

    // NG 슬롯
    public static readonly Brush SlotFilled = Red;
    public static readonly Brush SlotEmpty  = Freeze(0x2D, 0x33, 0x3B);

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

// ── StageStatus → Border/Icon 색상 ─────────────────────────────────────────
[ValueConversion(typeof(StageStatus), typeof(Brush))]
public class StageStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not StageStatus status) return Brushes.Transparent;
        return status switch
        {
            StageStatus.Running => Palette.Blue,
            StageStatus.Done    => Palette.Green,
            StageStatus.Error   => Palette.Red,
            StageStatus.Warning => Palette.Orange,
            StageStatus.Skipped => Palette.DimGray,
            _                   => Palette.Gray
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
            StageStatus.Running => Palette.BgBlue,
            StageStatus.Done    => Palette.BgGreen,
            StageStatus.Error   => Palette.BgRed,
            StageStatus.Warning => Palette.BgOrange,
            StageStatus.Skipped => Palette.BgDim,
            _                   => Palette.BgDefault
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
            LogLevel.Error   => Palette.Red,
            LogLevel.Warning => Palette.Orange,
            LogLevel.Info    => Palette.Light,
            _                => Palette.Gray
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
        if (text.Contains("GOOD")) return Palette.Green;
        if (text.Contains("NG"))   return Palette.Red;
        return Palette.Muted;
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

// ── Enum 문자열 매칭 → Visibility ──────────────────────────────────────────
[ValueConversion(typeof(object), typeof(Visibility))]
public class EnumMatchToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString() ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ── Z 게이지 Top 위치 (바 높이 → Canvas.Top) ─────────────────────────────────
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

// ── double 값의 절반 (십자선 중앙 좌표용) ──────────────────────────────────
public class HalfConverter : IValueConverter
{
    public static HalfConverter Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double d ? d / 2.0 : 0.0;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ── bool → NG/GOOD 채움 색상 (슬롯 시각화) ───────────────────────────────────
[ValueConversion(typeof(bool), typeof(Brush))]
public class NgSlotFillConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Palette.SlotFilled : Palette.SlotEmpty;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
