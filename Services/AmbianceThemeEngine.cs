using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Journal.Services
{
    /// <summary>
    /// 氛围调色板数据模型：统合强调色、专属卡片背景与边框、悬浮岛质感、日历打点与导航高光
    /// </summary>
    public class AmbiancePalette
    {
        public string Id { get; set; } = "Default";
        public string DisplayName { get; set; } = "默认";
        public bool? ForcedDarkTheme { get; set; } // null: 跟随系统, true: 锁定深色, false: 锁定浅色

        public Color AccentColor { get; set; }
        public Color AccentHoverColor { get; set; }
        public Color AccentPressedColor { get; set; }

        public Color BadgeBackgroundColor { get; set; }
        public Color BadgeBorderColor { get; set; }

        public Color CalendarActiveBackground { get; set; }
        public Color CalendarActiveRing { get; set; }

        public Color OverlayColorLight { get; set; }
        public Color OverlayColorLightDeep { get; set; }
        public Color OverlayColorDark { get; set; }
        public Color OverlayColorDarkDeep { get; set; }

        public Color NavIndicatorColor { get; set; }
        public Color NavActiveBackground { get; set; }

        public Color CardBackgroundColor { get; set; }
        public Color CardSecondaryBackgroundColor { get; set; }
        public Color CardBorderColor { get; set; }
        public Color ShellIslandBackground { get; set; }
        public Color ShellIslandBorder { get; set; }
    }

    /// <summary>
    /// 全局氛围色调与语义画刷动态注入引擎：
    /// 确保材质与色调 100% 正交解耦，提供专属直通画刷，
    /// 杜绝预设间色彩入侵与时序死锁，达成浑然天成的温润雅致质感。
    /// </summary>
    public static class AmbianceThemeEngine
    {
        public static event Action? AmbiancePaletteChanged;

        // ==========================================
        //  规范画刷注册表 (Canonical Brushes Registry)
        // ==========================================
        private static readonly Dictionary<string, SolidColorBrush> _canonicalBrushes = new(StringComparer.Ordinal);
        private static readonly object _brushLock = new();

        /// <summary>
        /// 获取或创建指定语义键的全局单例规范画刷。
        /// 确保同一语义键在整个应用程序生命周期内持有唯一不变的 SolidColorBrush 实例。
        /// </summary>
        public static SolidColorBrush? GetOrCreateCanonicalBrush(string key, Color initialColor)
        {
            if (string.IsNullOrEmpty(key)) return null;
            lock (_brushLock)
            {
                if (!_canonicalBrushes.TryGetValue(key, out var brush))
                {
                    try
                    {
                        brush = new SolidColorBrush(initialColor);
                        _canonicalBrushes[key] = brush;
                    }
                    catch
                    {
                        return null;
                    }
                }
                return brush;
            }
        }

        // 视图专属导航与悬浮岛高光画刷属性 (统一接入规范画刷注册表)
        public static SolidColorBrush? ActiveCalendarDayBackgroundBrush => GetOrCreateCanonicalBrush("ActiveCalendarDayBackgroundBrush", CurrentPalette.CalendarActiveBackground);
        public static SolidColorBrush? ActiveCalendarDayRingBrush => GetOrCreateCanonicalBrush("ActiveCalendarDayRingBrush", CurrentPalette.CalendarActiveRing);
        public static SolidColorBrush? NavIndicatorBrush => GetOrCreateCanonicalBrush("NavIndicatorBrush", CurrentPalette.NavIndicatorColor);
        public static SolidColorBrush? NavActiveTextBrush => GetOrCreateCanonicalBrush("NavActiveTextBrush", CurrentPalette.NavIndicatorColor);
        public static SolidColorBrush? NavActiveBackgroundBrush => GetOrCreateCanonicalBrush("NavActiveBackgroundBrush", CurrentPalette.NavActiveBackground);
        public static SolidColorBrush? AccentBrush => GetOrCreateCanonicalBrush("AccentBrush", CurrentPalette.AccentColor);
        public static SolidColorBrush? ShellIslandBackgroundBrush => GetOrCreateCanonicalBrush("ShellIslandBackgroundBrush", CurrentPalette.ShellIslandBackground);
        public static SolidColorBrush? ShellIslandBorderBrush => GetOrCreateCanonicalBrush("ShellIslandBorderBrush", CurrentPalette.ShellIslandBorder);
        public static SolidColorBrush? WordHighlightBackgroundBrush => GetOrCreateCanonicalBrush("WordHighlightBackgroundBrush", Color.FromArgb(96, CurrentPalette.AccentColor.R, CurrentPalette.AccentColor.G, CurrentPalette.AccentColor.B));
        public static SolidColorBrush? WordHighlightForegroundBrush => GetOrCreateCanonicalBrush("WordHighlightForegroundBrush", CurrentPalette.AccentColor);

        public static AmbiancePalette CurrentPalette { get; private set; } = CreateClassicSepiaPalette();

        private static readonly Dictionary<string, Func<AmbiancePalette>> PaletteFactories = new(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = CreateDefaultPalette,
            ["warmpaper"] = CreateWarmPaperPalette,
            ["midnightdark"] = CreateMidnightDarkPalette,
            ["dawnmist"] = CreateDawnMistPalette,
            ["twilightlavender"] = CreateTwilightLavenderPalette,
            ["classicsepia"] = CreateClassicSepiaPalette
        };

        public static AmbiancePalette GetPalette(string? ambiance, bool isDark = false)
        {
            var key = (ambiance ?? "classicsepia").Trim().ToLowerInvariant();
            AmbiancePalette palette = PaletteFactories.TryGetValue(key, out var factory)
                ? factory()
                : CreateClassicSepiaPalette();

            if (string.Equals(palette.Id, "Default", StringComparison.OrdinalIgnoreCase) && isDark)
            {
                palette.CardBackgroundColor = Color.FromArgb(255, 32, 35, 42);
                palette.CardSecondaryBackgroundColor = Color.FromArgb(255, 40, 44, 52);
                palette.CardBorderColor = Color.FromArgb(255, 52, 58, 68);
                palette.ShellIslandBackground = Color.FromArgb(255, 34, 38, 46);
                palette.ShellIslandBorder = Color.FromArgb(255, 54, 60, 72);
                palette.BadgeBackgroundColor = Color.FromArgb(0x28, 30, 142, 106);
                palette.BadgeBorderColor = Color.FromArgb(0x55, 42, 168, 126);
                palette.CalendarActiveBackground = Color.FromArgb(48, 30, 142, 106);
                palette.NavActiveBackground = Color.FromArgb(38, 30, 142, 106);
            }

            return palette;
        }

        public static void ApplyPalette(string? ambiance, bool isDark, FrameworkElement? rootElement, string? backdropType = null)
        {
            var palette = GetPalette(ambiance, isDark);
            CurrentPalette = palette;

            // 1. 同步更新所有直通高光与悬浮岛画刷，确保零延迟、零入侵
            try
            {
                if (NavIndicatorBrush != null) NavIndicatorBrush.Color = palette.NavIndicatorColor;
                if (NavActiveTextBrush != null) NavActiveTextBrush.Color = palette.NavIndicatorColor;
                if (NavActiveBackgroundBrush != null) NavActiveBackgroundBrush.Color = palette.NavActiveBackground;
                if (AccentBrush != null) AccentBrush.Color = palette.AccentColor;

                if (ShellIslandBackgroundBrush != null) ShellIslandBackgroundBrush.Color = palette.ShellIslandBackground;
                if (ShellIslandBorderBrush != null) ShellIslandBorderBrush.Color = palette.ShellIslandBorder;

                if (ActiveCalendarDayBackgroundBrush != null) ActiveCalendarDayBackgroundBrush.Color = palette.CalendarActiveBackground;
                if (ActiveCalendarDayRingBrush != null) ActiveCalendarDayRingBrush.Color = palette.CalendarActiveRing;

                if (WordHighlightBackgroundBrush != null) WordHighlightBackgroundBrush.Color = Color.FromArgb(96, palette.AccentColor.R, palette.AccentColor.G, palette.AccentColor.B);
                if (WordHighlightForegroundBrush != null) WordHighlightForegroundBrush.Color = palette.AccentColor;

                // 同步就地更新日历热力图 10 个全局共享单例画刷，零分配、零 PropertyChanged 风暴
                Journal.Models.DayHeatmapItem.UpdateSharedThemeBrushes(palette.AccentColor, isDark);
            }
            catch { }

            // 2. 注入全局 Application.Resources 字典并净化系统底层 3D 按钮底部黑线渐变画刷
            if (Application.Current != null)
            {
                try
                {
                    UpdateResourceDictionary(Application.Current.Resources, palette, isDark, backdropType);
                    NeutralizeSystemElevationBorders(Application.Current.Resources, palette);
                }
                catch { }
            }

            // 3. 注入主窗口根控件 Resources 字典
            if (rootElement != null)
            {
                try { UpdateResourceDictionary(rootElement.Resources, palette, isDark, backdropType); }
                catch { }
            }

            // 4. 广播事件通知各页面（ShellPage 导航岛、辅助日历、热力图）即时重绘
            try
            {
                AmbiancePaletteChanged?.Invoke();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AmbianceThemeEngine] Notify exception: {ex.Message}");
            }
        }

        /// <summary>
        /// 为独立挂载于 PopupRoot 的 ContentDialog 注入当前氛围色调资源并彻底剥离默认主按钮的底部黑线
        /// </summary>
        public static void ApplyToContentDialog(Microsoft.UI.Xaml.Controls.ContentDialog? dialog)
        {
            if (dialog == null) return;
            try
            {
                var palette = CurrentPalette;
                bool isDark = palette.ForcedDarkTheme ??
                    (dialog.XamlRoot?.Content is FrameworkElement rootFe && rootFe.ActualTheme == ElementTheme.Dark);

                dialog.RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;
                UpdateResourceDictionary(dialog.Resources, palette, isDark, "solid");

                if (Application.Current?.Resources != null &&
                    Application.Current.Resources.TryGetValue("AccentButtonStyle", out var styleObj) &&
                    styleObj is Style accentStyle)
                {
                    if (dialog.DefaultButton == Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary)
                    {
                        dialog.PrimaryButtonStyle = accentStyle;
                    }
                    else if (dialog.DefaultButton == Microsoft.UI.Xaml.Controls.ContentDialogButton.Secondary)
                    {
                        dialog.SecondaryButtonStyle = accentStyle;
                    }
                    else if (dialog.DefaultButton == Microsoft.UI.Xaml.Controls.ContentDialogButton.Close)
                    {
                        dialog.CloseButtonStyle = accentStyle;
                    }
                }

                void OnDialogLoaded(object s, RoutedEventArgs e)
                {
                    dialog.Loaded -= OnDialogLoaded;
                    try
                    {
                        StripDialogDefaultButtonElevationBorder(dialog);
                    }
                    catch { }
                }

                dialog.Loaded += OnDialogLoaded;
            }
            catch { }
        }

        private static void StripDialogDefaultButtonElevationBorder(DependencyObject root, Microsoft.UI.Xaml.Controls.ContentDialog? ownerDialog = null)
        {
            ownerDialog ??= root as Microsoft.UI.Xaml.Controls.ContentDialog;
            if (ownerDialog == null) return;

            string targetButtonName = ownerDialog.DefaultButton switch
            {
                Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary => "PrimaryButton",
                Microsoft.UI.Xaml.Controls.ContentDialogButton.Secondary => "SecondaryButton",
                Microsoft.UI.Xaml.Controls.ContentDialogButton.Close => "CloseButton",
                _ => string.Empty
            };

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is Microsoft.UI.Xaml.Controls.Button btn)
                {
                    if (!string.IsNullOrEmpty(targetButtonName) && string.Equals(btn.Name, targetButtonName, StringComparison.Ordinal))
                    {
                        btn.BorderThickness = new Thickness(0);
                        btn.BorderBrush = GetOrCreateCanonicalBrush("TransparentDialogButtonBorderBrush", Colors.Transparent);
                    }
                    else if (btn.BorderBrush is LinearGradientBrush lgb)
                    {
                        foreach (var stop in lgb.GradientStops)
                        {
                            stop.Color = CurrentPalette.CardBorderColor;
                        }
                    }
                }
                StripDialogDefaultButtonElevationBorder(child, ownerDialog);
            }
        }

        /// <summary>
        /// 就地净化 XamlControlsResources 中导致纯色按钮与弹窗主按钮底部出现黑线的 LinearGradientBrush (Elevation Border)
        /// </summary>
        private static void NeutralizeSystemElevationBorders(ResourceDictionary appResources, AmbiancePalette p)
        {
            if (appResources == null) return;
            try
            {
                NeutralizeElevationBrushesInSingleDict(appResources, p);
                if (appResources.MergedDictionaries != null)
                {
                    foreach (var merged in appResources.MergedDictionaries)
                    {
                        NeutralizeElevationBrushesInSingleDict(merged, p);
                        if (merged.ThemeDictionaries != null)
                        {
                            foreach (var tk in s_themeDictionaryKeys)
                            {
                                if (merged.ThemeDictionaries.TryGetValue(tk, out var tdObj) && tdObj is ResourceDictionary td)
                                {
                                    NeutralizeElevationBrushesInSingleDict(td, p);
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static readonly string[] s_themeDictionaryKeys = { "Light", "Dark", "Default" };
        private static readonly string[] s_accentBorderKeys =
        {
            "AccentControlElevationBorderBrush",
            "AccentButtonBorderBrush",
            "AccentButtonBorderBrushPointerOver",
            "ToggleButtonBorderBrushChecked",
            "ToggleButtonBorderBrushCheckedPointerOver"
        };

        private static void NeutralizeElevationBrushesInSingleDict(ResourceDictionary dict, AmbiancePalette p)
        {
            if (dict == null) return;

            foreach (var key in s_accentBorderKeys)
            {
                try
                {
                    if (dict.TryGetValue(key, out var obj) && obj is LinearGradientBrush lgb)
                    {
                        lgb.Opacity = 0.0;
                        foreach (var stop in lgb.GradientStops)
                        {
                            stop.Color = Colors.Transparent;
                        }
                    }
                }
                catch { }
            }

            try
            {
                if (dict.TryGetValue("ControlElevationBorderBrush", out var regObj) && regObj is LinearGradientBrush regLgb)
                {
                    foreach (var stop in regLgb.GradientStops)
                    {
                        stop.Color = p.CardBorderColor;
                    }
                }
            }
            catch { }
        }

        private static void UpdateResourceDictionary(ResourceDictionary dict, AmbiancePalette p, bool isDark, string? backdropType = null)
        {
            if (dict == null) return;
            try
            {
                // 1. 注入当前字典根层
                PopulateThemeResources(dict, p, isDark, backdropType);

                // 2. 深度注入 ThemeDictionaries ("Light", "Dark", "Default")
                foreach (var themeKey in s_themeDictionaryKeys)
                {
                    ResourceDictionary? targetThemeDict;
                    if (dict.ThemeDictionaries.TryGetValue(themeKey, out var existingObj) && existingObj is ResourceDictionary existingDict)
                    {
                        targetThemeDict = existingDict;
                    }
                    else
                    {
                        targetThemeDict = new ResourceDictionary();
                        dict.ThemeDictionaries[themeKey] = targetThemeDict;
                    }

                    PopulateThemeResources(targetThemeDict, p, isDark, backdropType);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AmbianceThemeEngine] UpdateResourceDictionary error: {ex.Message}");
            }
        }

        private static void PopulateThemeResources(ResourceDictionary target, AmbiancePalette p, bool isDark, string? backdropType)
        {
            var disabledAccent = Color.FromArgb(64, p.AccentColor.R, p.AccentColor.G, p.AccentColor.B);
            var sliderTrackUnfilled = Color.FromArgb(45, p.AccentColor.R, p.AccentColor.G, p.AccentColor.B);
            var sliderTrackUnfilledHover = Color.FromArgb(68, p.AccentColor.R, p.AccentColor.G, p.AccentColor.B);
            var sliderTrackUnfilledPressed = Color.FromArgb(35, p.AccentColor.R, p.AccentColor.G, p.AccentColor.B);

            // 强调色颜色变量
            SetOrUpdateColor(target, "SystemAccentColor", p.AccentColor);
            SetOrUpdateColor(target, "SystemAccentColorDark1", p.AccentPressedColor);
            SetOrUpdateColor(target, "SystemAccentColorDark2", p.AccentPressedColor);
            SetOrUpdateColor(target, "SystemAccentColorDark3", p.AccentPressedColor);
            SetOrUpdateColor(target, "SystemAccentColorLight1", p.AccentHoverColor);
            SetOrUpdateColor(target, "SystemAccentColorLight2", p.AccentHoverColor);
            SetOrUpdateColor(target, "SystemAccentColorLight3", p.AccentHoverColor);

            // 强调画刷族系
            SetOrUpdateBrush(target, "AccentFillColorDefaultBrush", p.AccentColor);
            SetOrUpdateBrush(target, "AccentFillColorSecondaryBrush", p.AccentHoverColor);
            SetOrUpdateBrush(target, "AccentFillColorTertiaryBrush", p.AccentPressedColor);
            SetOrUpdateBrush(target, "AccentFillColorDisabledBrush", disabledAccent);
            SetOrUpdateBrush(target, "AccentFillColorSelectedTextBackgroundBrush", p.AccentColor);
            SetOrUpdateBrush(target, "SystemControlHighlightAccentBrush", p.AccentColor);
            SetOrUpdateBrush(target, "SystemControlForegroundAccentBrush", p.AccentColor);
            SetOrUpdateBrush(target, "SystemControlHighlightAltAccentBrush", p.AccentColor);

            // 纯色强调按钮与选中切换按钮背景画刷同步
            SetOrUpdateBrush(target, "AccentButtonBackground", p.AccentColor);
            SetOrUpdateBrush(target, "AccentButtonBackgroundPointerOver", p.AccentHoverColor);
            SetOrUpdateBrush(target, "AccentButtonBackgroundPressed", p.AccentPressedColor);
            SetOrUpdateBrush(target, "AccentButtonBackgroundDisabled", disabledAccent);
            SetOrUpdateBrush(target, "ToggleButtonBackgroundChecked", p.AccentColor);
            SetOrUpdateBrush(target, "ToggleButtonBackgroundCheckedPointerOver", p.AccentHoverColor);
            SetOrUpdateBrush(target, "ToggleButtonBackgroundCheckedPressed", p.AccentPressedColor);

            // 彻底清除纯色按钮底部的 WinUI 3 原生黑色立体阴影线
            SetOrUpdateColor(target, "ControlStrokeColorOnAccentDefault", Colors.Transparent);
            SetOrUpdateColor(target, "ControlStrokeColorOnAccentSecondary", Colors.Transparent);
            SetOrUpdateColor(target, "ControlStrokeColorOnAccentTertiary", Colors.Transparent);
            SetOrUpdateColor(target, "ControlStrokeColorOnAccentDisabled", Colors.Transparent);
            SetOrUpdateBrush(target, "ControlStrokeColorOnAccentDefaultBrush", Colors.Transparent);
            SetOrUpdateBrush(target, "ControlStrokeColorOnAccentSecondaryBrush", Colors.Transparent);
            SetOrUpdateBrush(target, "ControlStrokeColorOnAccentTertiaryBrush", Colors.Transparent);
            SetOrUpdateBrush(target, "ControlStrokeColorOnAccentDisabledBrush", Colors.Transparent);
            SetOrUpdateBrush(target, "AccentControlElevationBorderBrush", Colors.Transparent);
            SetOrUpdateBrush(target, "AccentButtonBorderBrush", Colors.Transparent);
            SetOrUpdateBrush(target, "AccentButtonBorderBrushPointerOver", Colors.Transparent);
            SetOrUpdateBrush(target, "AccentButtonBorderBrushPressed", Colors.Transparent);
            SetOrUpdateBrush(target, "AccentButtonBorderBrushDisabled", Colors.Transparent);
            SetOrUpdateBrush(target, "ToggleButtonBorderBrushChecked", Colors.Transparent);
            SetOrUpdateBrush(target, "ToggleButtonBorderBrushCheckedPointerOver", Colors.Transparent);
            SetOrUpdateBrush(target, "ToggleButtonBorderBrushCheckedPressed", Colors.Transparent);

            // 消除常规按钮底部的深黑立体线，统一为温润细腻的均匀卡片描边
            SetOrUpdateColor(target, "ControlStrokeColorDefault", p.CardBorderColor);
            SetOrUpdateColor(target, "ControlStrokeColorSecondary", p.CardBorderColor);
            SetOrUpdateBrush(target, "ControlStrokeColorDefaultBrush", p.CardBorderColor);
            SetOrUpdateBrush(target, "ControlStrokeColorSecondaryBrush", p.CardBorderColor);
            SetOrUpdateBrush(target, "ControlElevationBorderBrush", p.CardBorderColor);
            SetOrUpdateBrush(target, "ButtonBorderBrush", p.CardBorderColor);
            SetOrUpdateBrush(target, "ButtonBorderBrushPointerOver", p.BadgeBorderColor);
            SetOrUpdateBrush(target, "ButtonBorderBrushPressed", p.CardBorderColor);

            SetOrUpdateBrush(target, "AccentTextFillColorPrimaryBrush", p.AccentColor);
            SetOrUpdateBrush(target, "AccentTextFillColorSecondaryBrush", p.AccentHoverColor);
            SetOrUpdateBrush(target, "AccentTextFillColorTertiaryBrush", p.AccentPressedColor);

            // 强调背景上的文字前景色：纯白
            SetOrUpdateBrush(target, "TextOnAccentFillColorPrimaryBrush", Colors.White);
            SetOrUpdateBrush(target, "TextOnAccentFillColorSecondaryBrush", Color.FromArgb(230, 255, 255, 255));
            SetOrUpdateBrush(target, "TextOnAccentFillColorDisabledBrush", Color.FromArgb(120, 255, 255, 255));
            SetOrUpdateBrush(target, "AccentButtonForeground", Colors.White);
            SetOrUpdateBrush(target, "AccentButtonForegroundPointerOver", Colors.White);
            SetOrUpdateBrush(target, "AccentButtonForegroundPressed", Color.FromArgb(235, 255, 255, 255));

            // WinUI 3 原生控件全状态关联画刷
            SetOrUpdateBrush(target, "SliderTrackFill", sliderTrackUnfilled);
            SetOrUpdateBrush(target, "SliderTrackFillPointerOver", sliderTrackUnfilledHover);
            SetOrUpdateBrush(target, "SliderTrackFillPressed", sliderTrackUnfilledPressed);
            SetOrUpdateBrush(target, "SliderTrackValueFill", p.AccentColor);
            SetOrUpdateBrush(target, "SliderTrackValueFillPointerOver", p.AccentHoverColor);
            SetOrUpdateBrush(target, "SliderTrackValueFillPressed", p.AccentPressedColor);
            SetOrUpdateBrush(target, "SliderThumbFill", p.AccentColor);
            SetOrUpdateBrush(target, "SliderThumbBackground", p.AccentColor);
            SetOrUpdateBrush(target, "SliderThumbBackgroundPointerOver", p.AccentHoverColor);
            SetOrUpdateBrush(target, "SliderThumbBackgroundPressed", p.AccentPressedColor);
            SetOrUpdateBrush(target, "SliderInnerThumbBackground", Colors.White);

            SetOrUpdateBrush(target, "ToggleSwitchFillOn", p.AccentColor);
            SetOrUpdateBrush(target, "ToggleSwitchFillOnPointerOver", p.AccentHoverColor);
            SetOrUpdateBrush(target, "ToggleSwitchFillOnPressed", p.AccentPressedColor);
            SetOrUpdateBrush(target, "ToggleSwitchStrokeOn", p.AccentColor);
            SetOrUpdateBrush(target, "ToggleSwitchStrokeOnPointerOver", p.AccentHoverColor);
            SetOrUpdateBrush(target, "ToggleSwitchStrokeOnPressed", p.AccentPressedColor);
            SetOrUpdateBrush(target, "ToggleSwitchKnobFillOn", Colors.White);
            SetOrUpdateBrush(target, "ToggleSwitchKnobFillOnPointerOver", Colors.White);
            SetOrUpdateBrush(target, "ToggleSwitchKnobFillOnPressed", Colors.White);

            SetOrUpdateBrush(target, "ComboBoxItemPillFillBrush", p.AccentColor);
            SetOrUpdateBrush(target, "ComboBoxBorderBrush", p.CardBorderColor);
            SetOrUpdateBrush(target, "ComboBoxBorderBrushPointerOver", p.BadgeBorderColor);
            SetOrUpdateBrush(target, "ComboBoxBorderBrushPressed", p.CardBorderColor);
            SetOrUpdateBrush(target, "ListViewItemSelectionIndicatorBrush", p.AccentColor);
            SetOrUpdateBrush(target, "ListViewItemSelectionIndicatorPointerOverBrush", p.AccentHoverColor);
            SetOrUpdateBrush(target, "ListViewItemSelectionIndicatorPressedBrush", p.AccentPressedColor);

            SetOrUpdateBrush(target, "CheckBoxCheckBackgroundFillChecked", p.AccentColor);
            SetOrUpdateBrush(target, "CheckBoxCheckBackgroundFillCheckedPointerOver", p.AccentHoverColor);
            SetOrUpdateBrush(target, "CheckBoxCheckBackgroundFillCheckedPressed", p.AccentPressedColor);
            SetOrUpdateBrush(target, "CheckBoxCheckBackgroundStrokeChecked", p.AccentColor);
            SetOrUpdateBrush(target, "CheckBoxCheckBackgroundStrokeCheckedPointerOver", p.AccentHoverColor);
            SetOrUpdateBrush(target, "CheckBoxCheckBackgroundStrokeCheckedPressed", p.AccentPressedColor);

            SetOrUpdateBrush(target, "RadioButtonOuterEllipseCheckedFill", p.AccentColor);
            SetOrUpdateBrush(target, "RadioButtonOuterEllipseCheckedPointerOverFill", p.AccentHoverColor);
            SetOrUpdateBrush(target, "RadioButtonOuterEllipseCheckedPressedFill", p.AccentPressedColor);
            SetOrUpdateBrush(target, "RadioButtonOuterEllipseCheckedStroke", p.AccentColor);
            SetOrUpdateBrush(target, "RadioButtonOuterEllipseCheckedPointerOverStroke", p.AccentHoverColor);
            SetOrUpdateBrush(target, "RadioButtonOuterEllipseCheckedPressedStroke", p.AccentPressedColor);

            SetOrUpdateBrush(target, "TextControlBorderBrushFocused", p.AccentColor);
            SetOrUpdateBrush(target, "FocusStrokeColorOuter", p.AccentColor);
            SetOrUpdateBrush(target, "ProgressRingForegroundThemeBrush", p.AccentColor);

            // 自定义语义画刷
            SetOrUpdateBrush(target, "AppBadgeBackgroundBrush", p.BadgeBackgroundColor);
            SetOrUpdateBrush(target, "AppBadgeBorderBrush", p.BadgeBorderColor);
            SetOrUpdateBrush(target, "AppAccentTextBrush", p.AccentColor);

            // 和谐卡片画刷：由当前氛围色板的基础色驱动，依据材质微调透光度
            var normBackdrop = backdropType?.Trim().ToLowerInvariant() ?? "micaalt";
            byte cardAlpha = normBackdrop switch
            {
                "acrylic" => 180,
                "solid" or "none" => 255,
                _ => 240
            };

            byte secAlpha = normBackdrop switch
            {
                "acrylic" => 140,
                "solid" or "none" => 255,
                _ => 215
            };

            var baseCardBg = p.CardBackgroundColor;
            var baseSecBg = p.CardSecondaryBackgroundColor;
            var baseBorder = p.CardBorderColor;

            SetOrUpdateBrush(target, "CardBackgroundFillColorDefaultBrush", Color.FromArgb(cardAlpha, baseCardBg.R, baseCardBg.G, baseCardBg.B));
            SetOrUpdateBrush(target, "CardBackgroundFillColorSecondaryBrush", Color.FromArgb(secAlpha, baseSecBg.R, baseSecBg.G, baseSecBg.B));
            SetOrUpdateBrush(target, "CardStrokeColorDefaultBrush", baseBorder);
        }

        private static void SetOrUpdateBrush(ResourceDictionary targetDict, string key, Color color)
        {
            if (targetDict == null || string.IsNullOrEmpty(key)) return;
            try
            {
                var canonicalBrush = GetOrCreateCanonicalBrush(key, color);
                if (canonicalBrush == null)
                {
                    try { targetDict[key] = new SolidColorBrush(color); } catch { }
                    return;
                }

                if (canonicalBrush.Color != color)
                {
                    canonicalBrush.Color = color;
                }

                if (targetDict.TryGetValue(key, out var existingObj) && ReferenceEquals(existingObj, canonicalBrush))
                {
                    return;
                }

                targetDict[key] = canonicalBrush;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AmbianceThemeEngine] SetOrUpdateBrush error ({key}): {ex.Message}");
            }
        }

        private static void SetOrUpdateColor(ResourceDictionary targetDict, string key, Color color)
        {
            if (targetDict == null || string.IsNullOrEmpty(key)) return;
            try
            {
                targetDict[key] = color;
            }
            catch { }
        }

        // ==========================================
        //  6 大浑然天成的柔和沉浸调色板
        // ==========================================

        private static AmbiancePalette CreateDefaultPalette()
        {
            var accent = Color.FromArgb(255, 30, 142, 106);
            return new AmbiancePalette
            {
                Id = "Default",
                DisplayName = "默认 (系统原生 · 翡翠绿)",
                ForcedDarkTheme = null,
                AccentColor = accent,
                AccentHoverColor = Color.FromArgb(255, 42, 168, 126),
                AccentPressedColor = Color.FromArgb(255, 22, 118, 88),
                BadgeBackgroundColor = Color.FromArgb(0x18, 30, 142, 106),
                BadgeBorderColor = Color.FromArgb(0x40, 30, 142, 106),
                CalendarActiveBackground = Color.FromArgb(32, 30, 142, 106),
                CalendarActiveRing = Color.FromArgb(140, 30, 142, 106),
                NavIndicatorColor = accent,
                NavActiveBackground = Color.FromArgb(24, 30, 142, 106),
                OverlayColorLight = Color.FromArgb(255, 250, 251, 253),
                OverlayColorLightDeep = Color.FromArgb(255, 234, 237, 242),
                OverlayColorDark = Color.FromArgb(255, 28, 30, 36),
                OverlayColorDarkDeep = Color.FromArgb(255, 14, 15, 18),
                CardBackgroundColor = Color.FromArgb(255, 255, 255, 255),
                CardSecondaryBackgroundColor = Color.FromArgb(255, 248, 249, 250),
                CardBorderColor = Color.FromArgb(28, 0, 0, 0),
                ShellIslandBackground = Color.FromArgb(255, 255, 255, 255),
                ShellIslandBorder = Color.FromArgb(28, 0, 0, 0)
            };
        }

        private static AmbiancePalette CreateWarmPaperPalette()
        {
            var accent = Color.FromArgb(255, 168, 98, 26);
            return new AmbiancePalette
            {
                Id = "WarmPaper",
                DisplayName = "米黄纸色 (暖琥珀沉香)",
                ForcedDarkTheme = false,
                AccentColor = accent,
                AccentHoverColor = Color.FromArgb(255, 192, 114, 32),
                AccentPressedColor = Color.FromArgb(255, 140, 80, 20),
                BadgeBackgroundColor = Color.FromArgb(0x18, 168, 98, 26),
                BadgeBorderColor = Color.FromArgb(0x40, 168, 98, 26),
                CalendarActiveBackground = Color.FromArgb(28, 168, 98, 26),
                CalendarActiveRing = Color.FromArgb(130, 168, 98, 26),
                NavIndicatorColor = accent,
                NavActiveBackground = Color.FromArgb(24, 168, 98, 26),
                OverlayColorLight = Color.FromArgb(255, 254, 251, 245),
                OverlayColorLightDeep = Color.FromArgb(255, 242, 230, 208),
                OverlayColorDark = Color.FromArgb(255, 36, 32, 26),
                OverlayColorDarkDeep = Color.FromArgb(255, 18, 16, 12),
                CardBackgroundColor = Color.FromArgb(255, 255, 254, 250),
                CardSecondaryBackgroundColor = Color.FromArgb(255, 248, 243, 233),
                CardBorderColor = Color.FromArgb(255, 230, 222, 206),
                ShellIslandBackground = Color.FromArgb(255, 255, 254, 250),
                ShellIslandBorder = Color.FromArgb(255, 230, 222, 206)
            };
        }

        private static AmbiancePalette CreateMidnightDarkPalette()
        {
            var accent = Color.FromArgb(255, 74, 158, 222);
            return new AmbiancePalette
            {
                Id = "MidnightDark",
                DisplayName = "纯黑 (极夜天际冷蓝)",
                ForcedDarkTheme = true,
                AccentColor = accent,
                AccentHoverColor = Color.FromArgb(255, 105, 178, 235),
                AccentPressedColor = Color.FromArgb(255, 52, 132, 194),
                BadgeBackgroundColor = Color.FromArgb(0x20, 74, 158, 222),
                BadgeBorderColor = Color.FromArgb(0x45, 74, 158, 222),
                CalendarActiveBackground = Color.FromArgb(32, 74, 158, 222),
                CalendarActiveRing = Color.FromArgb(140, 74, 158, 222),
                NavIndicatorColor = accent,
                NavActiveBackground = Color.FromArgb(30, 74, 158, 222),
                OverlayColorLight = Color.FromArgb(255, 24, 26, 32),
                OverlayColorLightDeep = Color.FromArgb(255, 10, 11, 14),
                OverlayColorDark = Color.FromArgb(255, 18, 20, 24),
                OverlayColorDarkDeep = Color.FromArgb(255, 8, 9, 12),
                CardBackgroundColor = Color.FromArgb(255, 26, 28, 34),
                CardSecondaryBackgroundColor = Color.FromArgb(255, 34, 37, 44),
                CardBorderColor = Color.FromArgb(255, 45, 50, 60),
                ShellIslandBackground = Color.FromArgb(255, 26, 28, 34),
                ShellIslandBorder = Color.FromArgb(255, 45, 50, 60)
            };
        }

        private static AmbiancePalette CreateDawnMistPalette()
        {
            var accent = Color.FromArgb(255, 30, 130, 121);
            return new AmbiancePalette
            {
                Id = "DawnMist",
                DisplayName = "青墨 (淡雅苍松黛青)",
                ForcedDarkTheme = false,
                AccentColor = accent,
                AccentHoverColor = Color.FromArgb(255, 42, 155, 144),
                AccentPressedColor = Color.FromArgb(255, 20, 105, 98),
                BadgeBackgroundColor = Color.FromArgb(0x18, 30, 130, 121),
                BadgeBorderColor = Color.FromArgb(0x40, 30, 130, 121),
                CalendarActiveBackground = Color.FromArgb(28, 30, 130, 121),
                CalendarActiveRing = Color.FromArgb(130, 30, 130, 121),
                NavIndicatorColor = accent,
                NavActiveBackground = Color.FromArgb(24, 30, 130, 121),
                OverlayColorLight = Color.FromArgb(255, 248, 252, 250),
                OverlayColorLightDeep = Color.FromArgb(255, 226, 238, 234),
                OverlayColorDark = Color.FromArgb(255, 24, 30, 28),
                OverlayColorDarkDeep = Color.FromArgb(255, 12, 16, 15),
                CardBackgroundColor = Color.FromArgb(255, 251, 253, 252),
                CardSecondaryBackgroundColor = Color.FromArgb(255, 241, 246, 244),
                CardBorderColor = Color.FromArgb(255, 212, 226, 222),
                ShellIslandBackground = Color.FromArgb(255, 251, 253, 252),
                ShellIslandBorder = Color.FromArgb(255, 212, 226, 222)
            };
        }

        private static AmbiancePalette CreateTwilightLavenderPalette()
        {
            var accent = Color.FromArgb(255, 116, 89, 200);
            return new AmbiancePalette
            {
                Id = "TwilightLavender",
                DisplayName = "浅紫 (暮色紫罗兰)",
                ForcedDarkTheme = false,
                AccentColor = accent,
                AccentHoverColor = Color.FromArgb(255, 138, 110, 218),
                AccentPressedColor = Color.FromArgb(255, 96, 70, 175),
                BadgeBackgroundColor = Color.FromArgb(0x18, 116, 89, 200),
                BadgeBorderColor = Color.FromArgb(0x40, 116, 89, 200),
                CalendarActiveBackground = Color.FromArgb(28, 116, 89, 200),
                CalendarActiveRing = Color.FromArgb(130, 116, 89, 200),
                NavIndicatorColor = accent,
                NavActiveBackground = Color.FromArgb(24, 116, 89, 200),
                OverlayColorLight = Color.FromArgb(255, 252, 249, 253),
                OverlayColorLightDeep = Color.FromArgb(255, 234, 225, 242),
                OverlayColorDark = Color.FromArgb(255, 30, 26, 36),
                OverlayColorDarkDeep = Color.FromArgb(255, 15, 12, 18),
                CardBackgroundColor = Color.FromArgb(255, 253, 251, 254),
                CardSecondaryBackgroundColor = Color.FromArgb(255, 245, 240, 248),
                CardBorderColor = Color.FromArgb(255, 224, 214, 232),
                ShellIslandBackground = Color.FromArgb(255, 253, 251, 254),
                ShellIslandBorder = Color.FromArgb(255, 224, 214, 232)
            };
        }

        private static AmbiancePalette CreateClassicSepiaPalette()
        {
            var accent = Color.FromArgb(255, 138, 86, 28);
            return new AmbiancePalette
            {
                Id = "ClassicSepia",
                DisplayName = "复古暖褐 (古籍焦糖)",
                ForcedDarkTheme = false,
                AccentColor = accent,
                AccentHoverColor = Color.FromArgb(255, 165, 105, 36),
                AccentPressedColor = Color.FromArgb(255, 112, 68, 20),
                BadgeBackgroundColor = Color.FromArgb(0x18, 138, 86, 28),
                BadgeBorderColor = Color.FromArgb(0x40, 138, 86, 28),
                CalendarActiveBackground = Color.FromArgb(28, 138, 86, 28),
                CalendarActiveRing = Color.FromArgb(130, 138, 86, 28),
                NavIndicatorColor = accent,
                NavActiveBackground = Color.FromArgb(24, 138, 86, 28),
                OverlayColorLight = Color.FromArgb(255, 252, 248, 240),
                OverlayColorLightDeep = Color.FromArgb(255, 236, 224, 204),
                OverlayColorDark = Color.FromArgb(255, 34, 28, 22),
                OverlayColorDarkDeep = Color.FromArgb(255, 16, 13, 10),
                CardBackgroundColor = Color.FromArgb(255, 251, 247, 240),
                CardSecondaryBackgroundColor = Color.FromArgb(255, 242, 235, 222),
                CardBorderColor = Color.FromArgb(255, 223, 210, 191),
                ShellIslandBackground = Color.FromArgb(255, 251, 247, 240),
                ShellIslandBorder = Color.FromArgb(255, 223, 210, 191)
            };
        }
    }
}
