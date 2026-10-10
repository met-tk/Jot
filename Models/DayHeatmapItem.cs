using System;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;

namespace Journal.Models
{
    public class DayHeatmapItem
    {
        public DateTime Date { get; set; }
        public int DayNumber => Date.Day;
        public bool IsCurrentMonth { get; set; }
        public bool IsToday => Date.Date == DateTime.Today;
        public int WordCount { get; set; }
        public int TimestampsCount { get; set; }
        public bool HasActivity => WordCount > 0 || TimestampsCount > 0 || !string.IsNullOrWhiteSpace(CalendarExcerpt);
        public string Excerpt { get; set; } = string.Empty;
        public string CalendarExcerpt { get; set; } = string.Empty;

        public Visibility ActivityBadgeVisibility => HasActivity ? Visibility.Visible : Visibility.Collapsed;
        public Visibility HasExcerptVisibility => !string.IsNullOrWhiteSpace(CalendarExcerpt) ? Visibility.Visible : Visibility.Collapsed;
        public Visibility NoActivityVisibility => (!HasActivity) ? Visibility.Visible : Visibility.Collapsed;

        public string WordBadgeText => WordCount > 0 ? $"{WordCount} 字" : (TimestampsCount > 0 ? $"{TimestampsCount} 条" : "记录");
        public string TimestampBadgeText => $"{TimestampsCount} 条记录";
        public string NoActivityTip => "无记录";
        public string DetailHeaderTitle => $"{Date:yyyy-MM-dd} ({(IsToday ? "今日" : Date.ToString("ddd"))})";

        public FontWeight DayFontWeight => (IsToday || HasActivity) ? FontWeights.SemiBold : FontWeights.Normal;

        // 色阶画刷定义（支持明暗双色调，采用全局共享单例画刷就地变色，彻底消除 546 格日历项重复分配与 PropertyChanged 风暴）
        private static bool _isDarkTheme = false;
        public static bool IsDarkTheme
        {
            get => _isDarkTheme;
            set
            {
                if (_isDarkTheme != value)
                {
                    _isDarkTheme = value;
                    UpdateSharedThemeBrushes(Journal.Services.AmbianceThemeEngine.CurrentPalette.AccentColor, _isDarkTheme);
                }
            }
        }

        private static SolidColorBrush? s_todayBgBrush;
        private static SolidColorBrush? s_transparentBgBrush;
        private static SolidColorBrush? s_level1BgBrush;
        private static SolidColorBrush? s_level2BgBrush;
        private static SolidColorBrush? s_level3BgBrush;
        private static SolidColorBrush? s_level4BgBrush;

        private static SolidColorBrush? s_whiteFgBrush;
        private static SolidColorBrush? s_otherMonthFgBrush;
        private static SolidColorBrush? s_activeLightFgBrush;
        private static SolidColorBrush? s_normalFgBrush;

        public static void UpdateSharedThemeBrushes(Color accent, bool isDark)
        {
            _isDarkTheme = isDark;
            try
            {
                var todayColor = Color.FromArgb(235, accent.R, accent.G, accent.B);
                var l1Color = Color.FromArgb(isDark ? (byte)90 : (byte)50, accent.R, accent.G, accent.B);
                var l2Color = Color.FromArgb(isDark ? (byte)140 : (byte)100, accent.R, accent.G, accent.B);
                var l3Color = Color.FromArgb(isDark ? (byte)195 : (byte)165, accent.R, accent.G, accent.B);
                var l4Color = Color.FromArgb(isDark ? (byte)240 : (byte)220, accent.R, accent.G, accent.B);

                var otherMonthFg = isDark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(70, 120, 120, 120);
                var activeLightFg = isDark ? Color.FromArgb(255, 240, 240, 240) : Color.FromArgb(255, 20, 20, 20);
                var normalFg = isDark ? Color.FromArgb(220, 230, 230, 230) : Color.FromArgb(220, 40, 40, 40);

                if (s_todayBgBrush == null) s_todayBgBrush = new SolidColorBrush(todayColor); else s_todayBgBrush.Color = todayColor;
                if (s_transparentBgBrush == null) s_transparentBgBrush = new SolidColorBrush(Colors.Transparent);
                if (s_level1BgBrush == null) s_level1BgBrush = new SolidColorBrush(l1Color); else s_level1BgBrush.Color = l1Color;
                if (s_level2BgBrush == null) s_level2BgBrush = new SolidColorBrush(l2Color); else s_level2BgBrush.Color = l2Color;
                if (s_level3BgBrush == null) s_level3BgBrush = new SolidColorBrush(l3Color); else s_level3BgBrush.Color = l3Color;
                if (s_level4BgBrush == null) s_level4BgBrush = new SolidColorBrush(l4Color); else s_level4BgBrush.Color = l4Color;

                if (s_whiteFgBrush == null) s_whiteFgBrush = new SolidColorBrush(Colors.White);
                if (s_otherMonthFgBrush == null) s_otherMonthFgBrush = new SolidColorBrush(otherMonthFg); else s_otherMonthFgBrush.Color = otherMonthFg;
                if (s_activeLightFgBrush == null) s_activeLightFgBrush = new SolidColorBrush(activeLightFg); else s_activeLightFgBrush.Color = activeLightFg;
                if (s_normalFgBrush == null) s_normalFgBrush = new SolidColorBrush(normalFg); else s_normalFgBrush.Color = normalFg;
            }
            catch
            {
                // 非 UI 线程或无 XAML 宿主测试环境静默忽略
            }
        }

        private static void EnsureBrushesInitialized()
        {
            if (s_todayBgBrush == null)
            {
                UpdateSharedThemeBrushes(Journal.Services.AmbianceThemeEngine.CurrentPalette.AccentColor, _isDarkTheme);
            }
        }

        public Brush DayBackground
        {
            get
            {
                EnsureBrushesInitialized();
                if (IsToday) return s_todayBgBrush!;
                if (!HasActivity) return s_transparentBgBrush!;

                if (WordCount < 100) return s_level1BgBrush!;
                if (WordCount < 300) return s_level2BgBrush!;
                if (WordCount < 800) return s_level3BgBrush!;
                return s_level4BgBrush!;
            }
        }

        public Brush DayForeground
        {
            get
            {
                EnsureBrushesInitialized();
                if (IsToday) return s_whiteFgBrush!;
                if (!IsCurrentMonth) return s_otherMonthFgBrush!;

                if (HasActivity)
                {
                    if (WordCount >= 300) return s_whiteFgBrush!;
                    return s_activeLightFgBrush!;
                }

                return s_normalFgBrush!;
            }
        }
    }
}
