using System;
using System.Collections.Concurrent;
using Microsoft.UI.Xaml.Media;

namespace Journal.Models
{
    /// <summary>
    /// 全局应用偏好与沉浸设置实体模型
    /// </summary>
    public class AppSettings
    {
        private static readonly ConcurrentDictionary<string, FontFamily> s_fontFamilyCache = new(StringComparer.OrdinalIgnoreCase);

        // 1. 声音与物理音效配置
        public bool IsSoundEnabled { get; set; } = true;
        public string SoundTheme { get; set; } = "Typewriter";
        public string SaveSoundTheme { get; set; } = "TypewriterBell"; // "TypewriterBell", "None" 等手动保存音效预设
        public double SoundVolume { get; set; } = 0.8; // 0.0 ~ 1.0

        // 2. 拟物手感与触觉微动效
        public bool IsHapticVibrationEnabled { get; set; } = true;

        // 3. 排版与字体
        public string FontFamily { get; set; } = "Segoe UI Variable Text";
        public string UiFontFamily { get; set; } = "Segoe UI Variable Text";
        public double FontSize { get; set; } = 15.0; // 12.0 ~ 24.0
        public double LineSpacing { get; set; } = 1.5; // 1.2 ~ 2.2
        public bool IsAutoSaveEnabled { get; set; } = true;

        // 4. 窗口材质与沉浸度
        public string BackdropType { get; set; } = "Solid"; // "Solid", "MicaAlt", "Mica", "Acrylic"
        public string ThemeAmbiance { get; set; } = "ClassicSepia"; // "ClassicSepia", "Default", "WarmPaper", "MidnightDark", "DawnMist", "TwilightLavender"
        public double BackgroundTintOpacity { get; set; } = 0.0; // 0.0 ~ 0.9 (纯色遮罩)

        // 5. 岁月沉淀拟物渲染系统
        public bool IsAgingEnabled { get; set; } = true;
        public string? AgingPaperTexturePath { get; set; } = null;
        public string? AgingInkTexturePath { get; set; } = null;
        public double AgingPaperIntensity { get; set; } = 0.85; // 0.0 ~ 1.0 (纸张底色老化强度)
        public double AgingInkIntensity { get; set; } = 0.85; // 0.0 ~ 1.0 (字迹风化褪色强度)
        public string AgingPaperBlendMode { get; set; } = "Auto"; // "Auto"=跟随主题, "Multiply"=正片叠底, "Screen"=反相滤色, "Overlay"=叠加, "SoftLight"=柔光

        // 6. 窗口位置、尺寸与视图缩放记忆
        public int WindowX { get; set; } = -1;
        public int WindowY { get; set; } = -1;
        public int WindowWidth { get; set; } = 1180;
        public int WindowHeight { get; set; } = 780;
        public bool IsWindowMaximized { get; set; } = false;
        public double CalendarZoomFactor { get; set; } = 1.0;

        /// <summary>
        /// 生成专为 WinUI 3 XAML / DirectWrite 渲染引擎优化并带缓存的 FontFamily 实例，
        /// 消除可变光学轴字体复合链导致中文回退字形测量宽度偏窄与重复实例化开销。
        /// </summary>
        public static FontFamily BuildXamlFontFamily(string? chosenFont)
        {
            var clean = (chosenFont ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(clean)
                || clean.Equals("Segoe UI Variable Text", StringComparison.OrdinalIgnoreCase)
                || clean.Equals("Segoe UI Variable", StringComparison.OrdinalIgnoreCase)
                || clean.Equals("Segoe UI", StringComparison.OrdinalIgnoreCase)
                || clean.Equals("系统默认", StringComparison.OrdinalIgnoreCase))
            {
                return Microsoft.UI.Xaml.Media.FontFamily.XamlAutoFontFamily;
            }

            if (s_fontFamilyCache.TryGetValue(clean, out var cached))
            {
                return cached;
            }

            string xamlStack = clean switch
            {
                "霞鹜文楷" or "LXGW WenKai" => "LXGW WenKai, 霞鹜文楷, Microsoft YaHei UI",
                "思源宋体" or "Source Han Serif" => "Source Han Serif SC, 思源宋体, SimSun, Microsoft YaHei UI",
                "Cascadia Code" => "Cascadia Code, Microsoft YaHei UI",
                "Microsoft YaHei UI" or "微软雅黑" => "Microsoft YaHei UI",
                _ => $"{clean}, Microsoft YaHei UI"
            };

            var created = new FontFamily(xamlStack);
            s_fontFamilyCache.TryAdd(clean, created);
            return created;
        }

        /// <summary>
        /// 创建设置副本
        /// </summary>
        public AppSettings Clone()
        {
            return new AppSettings
            {
                IsSoundEnabled = this.IsSoundEnabled,
                SoundTheme = this.SoundTheme,
                SaveSoundTheme = this.SaveSoundTheme,
                SoundVolume = this.SoundVolume,
                IsHapticVibrationEnabled = this.IsHapticVibrationEnabled,
                FontFamily = this.FontFamily,
                UiFontFamily = this.UiFontFamily,
                FontSize = this.FontSize,
                LineSpacing = this.LineSpacing,
                IsAutoSaveEnabled = this.IsAutoSaveEnabled,
                BackdropType = this.BackdropType,
                ThemeAmbiance = this.ThemeAmbiance,
                BackgroundTintOpacity = this.BackgroundTintOpacity,
                IsAgingEnabled = this.IsAgingEnabled,
                AgingPaperTexturePath = this.AgingPaperTexturePath,
                AgingInkTexturePath = this.AgingInkTexturePath,
                AgingPaperIntensity = this.AgingPaperIntensity,
                AgingInkIntensity = this.AgingInkIntensity,
                AgingPaperBlendMode = this.AgingPaperBlendMode,
                WindowX = this.WindowX,
                WindowY = this.WindowY,
                WindowWidth = this.WindowWidth,
                WindowHeight = this.WindowHeight,
                IsWindowMaximized = this.IsWindowMaximized,
                CalendarZoomFactor = this.CalendarZoomFactor
            };
        }
    }
}
