using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Journal.Models;
using Journal.Services;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Journal.ViewModels
{
    public class SelectionOptionItem
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;

        public override string ToString() => Title;
    }

    /// <summary>
    /// 设置中心视图模型：管理声音、动效、字体、窗口材质与本地存储偏好
    /// </summary>
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly IAppSettingsService _settingsService;
        private readonly ISoundEffectService _soundService;
        private bool _isUpdatingFromModel;
        private bool _isSavingLocally;

        public List<SelectionOptionItem> AvailableSoundThemeOptions { get; } = new()
        {
            new() { Id = "Typewriter", Title = "复古打字机", Subtitle = "机械字锤敲击与回车铃声" },
            new() { Id = "CherryBlue", Title = "Cherry 青轴", Subtitle = "清脆段落咔嗒声" },
            new() { Id = "CherryBrown", Title = "Cherry 茶轴", Subtitle = "轻微段落柔和回弹" },
            new() { Id = "CherryRed", Title = "Cherry 红轴", Subtitle = "顺滑直上直下轻音" },
            new() { Id = "MahjongThock", Title = "麻将音", Subtitle = "饱满圆润的客制化键音" },
            new() { Id = "RaindropSlate", Title = "水滴声", Subtitle = "清澈水滴落石声" },
            new() { Id = "OldMembrane", Title = "薄膜键盘", Subtitle = "老式台式机键盘闷响" },
            new() { Id = "WoodBlock", Title = "木鱼", Subtitle = "温润木鱼敲击声" },
            new() { Id = "VintageTypewriterPro", Title = "机械打字机", Subtitle = "重锤机械打字机实录" },
            new() { Id = "ClassicTypewriterReal", Title = "老式打字机", Subtitle = "经典铁质打字机实录" },
            new() { Id = "IPhoneKeyboard", Title = "iPhone 键盘", Subtitle = "手机触屏按键音实录" },
            new() { Id = "DataMatrixSfx", Title = "电报音", Subtitle = "高频数字脉冲音效" }
        };

        public List<SelectionOptionItem> AvailableSaveSoundThemeOptions { get; } = new()
        {
            new() { Id = "TypewriterBell", Title = "打字机铃声", Subtitle = "手动保存时播放打字机铜铃声" },
            new() { Id = "None", Title = "无", Subtitle = "手动保存时不播放提示音" }
        };

        public List<string> AvailableFontFamilies => FontService.Instance.GetInstalledFontFamilies();

        public List<SelectionOptionItem> AvailableBackdropOptions { get; } = new()
        {
            new() { Id = "MicaAlt", Title = "Mica Alt (深层云母)", Subtitle = "深邃分层质感 · 桌面光影立体漫射" },
            new() { Id = "Mica", Title = "Mica (浅层原生云母)", Subtitle = "轻透采光质感 · 柔和采撷壁纸光色" },
            new() { Id = "Acrylic", Title = "Acrylic (亚克力毛玻璃)", Subtitle = "高透磨砂质感 · 浓郁高斯模糊与光晕" },
            new() { Id = "Solid", Title = "纯色经典 (无透光)", Subtitle = "零透光极简 · 纯净实色背景与零 GPU 开销" }
        };

        public List<SelectionOptionItem> AvailableAmbianceOptions { get; } = new()
        {
            new() { Id = "Default", Title = "默认 (系统原生 · 翡翠绿)", Subtitle = "现代 Fluent 默认经典绿意" },
            new() { Id = "WarmPaper", Title = "米黄纸色 (暖琥珀沉香)", Subtitle = "温和羊皮纸面与琥珀暖金" },
            new() { Id = "MidnightDark", Title = "纯黑 (极夜天际冷蓝)", Subtitle = "极致 OLED 纯黑与天际冷蓝" },
            new() { Id = "DawnMist", Title = "青墨 (淡雅苍松黛青)", Subtitle = "清幽微透墨青与黛青雅致" },
            new() { Id = "TwilightLavender", Title = "浅紫 (暮色紫罗兰)", Subtitle = "暮色粉紫与优雅罗兰调" },
            new() { Id = "ClassicSepia", Title = "复古暖褐 (古籍焦糖)", Subtitle = "复古羊皮卷与古卷沉香" }
        };

        [ObservableProperty]
        private bool _isSoundEnabled;

        [ObservableProperty]
        private string _selectedTheme = "Typewriter";

        [ObservableProperty]
        private string _selectedSaveTheme = "TypewriterBell";

        [ObservableProperty]
        private double _soundVolumePercent = 80.0;

        [ObservableProperty]
        private bool _isHapticVibrationEnabled;

        [ObservableProperty]
        private string _selectedFontFamily = "Segoe UI Variable Text";

        [ObservableProperty]
        private string _selectedUiFontFamily = "Segoe UI Variable Text";

        [ObservableProperty]
        private double _fontSize = 15.0;

        [ObservableProperty]
        private double _lineSpacing = 1.5;

        [ObservableProperty]
        private string _selectedBackdrop = "MicaAlt";

        [ObservableProperty]
        private string _selectedAmbiance = "Default";

        [ObservableProperty]
        private double _backgroundTintOpacityPercent = 0.0;

        [ObservableProperty]
        private string _storageDirectoryPath = string.Empty;

        // 版本号取自 csproj 的 <Version>（随构建自动同步），避免硬编码后各版本间漂移
        [ObservableProperty]
        private string _appVersionText =
            $"Jot v{System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0"}";

        // 岁月沉淀拟物渲染系统配置项
        [ObservableProperty]
        private bool _isAgingEnabled = true;

        [ObservableProperty]
        private string? _agingPaperTexturePath;

        [ObservableProperty]
        private string? _agingInkTexturePath;

        [ObservableProperty]
        private double _agingIntensityPercent = 85.0;

        [ObservableProperty]
        private double _agingPaperIntensityPercent = 85.0;

        [ObservableProperty]
        private double _agingInkIntensityPercent = 85.0;

        [ObservableProperty]
        private string _agingPaperTextureDisplayName = "内置经典纸纹";

        [ObservableProperty]
        private string _agingInkTextureDisplayName = "内置经典墨渍";

        [ObservableProperty]
        private string _agingCacheSizeText = "0 KB";

        [ObservableProperty]
        private string _agingStatusMessage = string.Empty;

        [ObservableProperty]
        private string _samplePreviewText = "天地者万物之逆旅也，光阴者百代之过客也。";

        public FontFamily PreviewFontFamily => AppSettings.BuildXamlFontFamily(SelectedFontFamily);
        public double PreviewLineHeight => Math.Round(FontSize * LineSpacing, 1);

        public string MaterialPreviewSummary
        {
            get
            {
                var backdropName = SelectedBackdrop switch
                {
                    "Solid" or "None" => "纯色经典 (无透光)",
                    "Acrylic" => "Acrylic 毛玻璃",
                    "Mica" => "Mica 原生云母",
                    _ => "Mica Alt 深层云母"
                };
                var ambianceName = SelectedAmbiance switch
                {
                    "WarmPaper" => "米黄纸色 (暖琥珀)",
                    "MidnightDark" => "纯黑 (天际冷蓝)",
                    "DawnMist" => "青墨 (苍松黛青)",
                    "TwilightLavender" => "浅紫 (暮色罗兰)",
                    "ClassicSepia" => "复古暖褐 (古籍焦糖)",
                    _ => "默认 (翡翠绿)"
                };
                return $"当前外观：{backdropName} · {ambianceName}（底色浓度：{BackgroundTintOpacityPercent}%）";
            }
        }

        public SettingsViewModel(IAppSettingsService? settingsService = null, ISoundEffectService? soundService = null)
        {
            _settingsService = settingsService ?? AppSettingsService.Instance;
            _soundService = soundService ?? SoundEffectService.Instance;

            StorageDirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "JournalData");

            LoadFromSettings(_settingsService.CurrentSettings);
            Subscribe();
        }

        public void Subscribe()
        {
            _settingsService.SettingsChanged -= OnSettingsChanged;
            _settingsService.SettingsChanged += OnSettingsChanged;
        }

        public void Unsubscribe()
        {
            _settingsService.SettingsChanged -= OnSettingsChanged;
        }

        private void LoadFromSettings(AppSettings s)
        {
            _isUpdatingFromModel = true;
            try
            {
                IsSoundEnabled = s.IsSoundEnabled;
                SelectedTheme = s.SoundTheme;
                SelectedSaveTheme = string.IsNullOrWhiteSpace(s.SaveSoundTheme) ? "TypewriterBell" : s.SaveSoundTheme;
                SoundVolumePercent = Math.Round(s.SoundVolume * 100.0, 0);

                IsHapticVibrationEnabled = s.IsHapticVibrationEnabled;

                SelectedFontFamily = s.FontFamily;
                SelectedUiFontFamily = string.IsNullOrWhiteSpace(s.UiFontFamily) ? "Segoe UI Variable Text" : s.UiFontFamily;
                FontSize = s.FontSize;
                LineSpacing = Math.Round(s.LineSpacing, 2);

                SelectedBackdrop = s.BackdropType;
                SelectedAmbiance = string.IsNullOrWhiteSpace(s.ThemeAmbiance) ? "Default" : s.ThemeAmbiance;
                BackgroundTintOpacityPercent = Math.Round(s.BackgroundTintOpacity * 100.0, 0);

                IsAgingEnabled = s.IsAgingEnabled;
                AgingPaperTexturePath = s.AgingPaperTexturePath;
                AgingInkTexturePath = s.AgingInkTexturePath;
                AgingIntensityPercent = Math.Round(s.AgingIntensity * 100.0, 0);
                AgingPaperIntensityPercent = Math.Round(s.AgingPaperIntensity * 100.0, 0);
                AgingInkIntensityPercent = Math.Round(s.AgingInkIntensity * 100.0, 0);
                UpdateAgingTextureDisplayNames();
                RefreshAgingCacheSize();

                OnPropertyChanged(nameof(PreviewFontFamily));
                OnPropertyChanged(nameof(PreviewLineHeight));
                OnPropertyChanged(nameof(MaterialPreviewSummary));
            }
            finally
            {
                _isUpdatingFromModel = false;
            }
        }

        private void OnSettingsChanged(object? sender, AppSettings e)
        {
            if (_isSavingLocally || _isUpdatingFromModel) return;
            LoadFromSettings(e);
        }

        partial void OnIsSoundEnabledChanged(bool value)
        {
            if (_isUpdatingFromModel || _isSavingLocally) return;
            _isSavingLocally = true;
            try
            {
                _soundService.SetEnabled(value);
                _settingsService.Update(s => s.IsSoundEnabled = value);
            }
            finally
            {
                _isSavingLocally = false;
            }
        }

        partial void OnSelectedThemeChanged(string value)
        {
            if (_isUpdatingFromModel || _isSavingLocally) return;
            _isSavingLocally = true;
            try
            {
                _soundService.SetTheme(value);
                _settingsService.Update(s => s.SoundTheme = value);
                _soundService.PlayKeyClick();
            }
            finally
            {
                _isSavingLocally = false;
            }
        }

        partial void OnSelectedSaveThemeChanged(string value)
        {
            if (_isUpdatingFromModel || _isSavingLocally) return;
            _isSavingLocally = true;
            try
            {
                _soundService.SetSaveTheme(value);
                _settingsService.Update(s => s.SaveSoundTheme = value);
                _soundService.PlaySaveSound();
            }
            finally
            {
                _isSavingLocally = false;
            }
        }

        partial void OnSoundVolumePercentChanged(double value)
        {
            if (_isUpdatingFromModel || _isSavingLocally) return;
            _isSavingLocally = true;
            try
            {
                double normalized = Math.Clamp(value / 100.0, 0.0, 1.0);
                _soundService.SetVolume(normalized);
                _settingsService.Update(s => s.SoundVolume = normalized);
            }
            finally
            {
                _isSavingLocally = false;
            }
        }

        partial void OnIsHapticVibrationEnabledChanged(bool value)
        {
            if (_isUpdatingFromModel || _isSavingLocally) return;
            _isSavingLocally = true;
            try
            {
                _settingsService.Update(s => s.IsHapticVibrationEnabled = value);
            }
            finally
            {
                _isSavingLocally = false;
            }
        }

        partial void OnSelectedFontFamilyChanged(string value)
        {
            OnPropertyChanged(nameof(PreviewFontFamily));
            if (_isUpdatingFromModel || _isSavingLocally) return;
            _isSavingLocally = true;
            try
            {
                _settingsService.Update(s => s.FontFamily = value);
            }
            finally
            {
                _isSavingLocally = false;
            }
        }

        partial void OnSelectedUiFontFamilyChanged(string value)
        {
            if (_isUpdatingFromModel || _isSavingLocally) return;
            _isSavingLocally = true;
            try
            {
                _settingsService.Update(s => s.UiFontFamily = value);
            }
            finally
            {
                _isSavingLocally = false;
            }
        }

        partial void OnFontSizeChanged(double value)
        {
            OnPropertyChanged(nameof(PreviewLineHeight));
            if (_isUpdatingFromModel || _isSavingLocally) return;
            _isSavingLocally = true;
            try
            {
                _settingsService.Update(s => s.FontSize = Math.Clamp(value, 12.0, 24.0));
            }
            finally
            {
                _isSavingLocally = false;
            }
        }

        partial void OnLineSpacingChanged(double value)
        {
            OnPropertyChanged(nameof(PreviewLineHeight));
            if (_isUpdatingFromModel || _isSavingLocally) return;
            _isSavingLocally = true;
            try
            {
                _settingsService.Update(s => s.LineSpacing = Math.Clamp(Math.Round(value, 2), 1.2, 2.2));
            }
            finally
            {
                _isSavingLocally = false;
            }
        }

        partial void OnSelectedBackdropChanged(string value)
        {
            OnPropertyChanged(nameof(MaterialPreviewSummary));
            if (_isUpdatingFromModel || _isSavingLocally) return;
            _isSavingLocally = true;
            try
            {
                _settingsService.Update(s => s.BackdropType = value);
            }
            finally
            {
                _isSavingLocally = false;
            }
        }

        partial void OnSelectedAmbianceChanged(string value)
        {
            OnPropertyChanged(nameof(MaterialPreviewSummary));
            if (_isUpdatingFromModel || _isSavingLocally) return;
            _isSavingLocally = true;
            try
            {
                _settingsService.Update(s => s.ThemeAmbiance = value);
            }
            finally
            {
                _isSavingLocally = false;
            }
        }

        partial void OnBackgroundTintOpacityPercentChanged(double value)
        {
            OnPropertyChanged(nameof(MaterialPreviewSummary));
            if (_isUpdatingFromModel || _isSavingLocally) return;
            _isSavingLocally = true;
            try
            {
                double normalized = Math.Clamp(value / 100.0, 0.0, 0.9);
                _settingsService.Update(s => s.BackgroundTintOpacity = normalized);
            }
            finally
            {
                _isSavingLocally = false;
            }
        }

        [RelayCommand]
        public void SetPreviewAncient()
        {
            SamplePreviewText = "天地者万物之逆旅也，光阴者百代之过客也。";
        }

        [RelayCommand]
        public void SetPreviewModern()
        {
            SamplePreviewText = "这是一段用于测试字体、字号与行间距排版效果的日记正文样例。";
        }

        [RelayCommand]
        public void SetPreviewEnglish()
        {
            SamplePreviewText = "The quick brown fox jumps over the lazy dog. 1234567890.";
        }

        [RelayCommand]
        public void ResetFontSettings()
        {
            SelectedFontFamily = "Segoe UI Variable Text";
            SelectedUiFontFamily = "Segoe UI Variable Text";
            FontSize = 15.0;
            LineSpacing = 1.5;
        }

        [RelayCommand]
        public void PreviewKeySound()
        {
            _soundService.PlayKeyClick();
        }

        [RelayCommand]
        public void PreviewEnterSound()
        {
            _soundService.PlayEnterKey();
        }

        [RelayCommand]
        public void PreviewSaveSound()
        {
            _soundService.PlaySaveSound();
        }

        [RelayCommand]
        public void OpenStorageFolder()
        {
            try
            {
                if (!Directory.Exists(StorageDirectoryPath))
                {
                    Directory.CreateDirectory(StorageDirectoryPath);
                }

                Process.Start(new ProcessStartInfo("explorer.exe", StorageDirectoryPath)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OpenStorageFolder] Failed: {ex.Message}");
            }
        }

        #region 岁月沉淀拟物渲染系统命令与响应

        partial void OnIsAgingEnabledChanged(bool value)
        {
            if (_isUpdatingFromModel || _isSavingLocally) return;
            _isSavingLocally = true;
            try
            {
                _settingsService.Update(s => s.IsAgingEnabled = value);
                AgingStatusMessage = value ? "岁月沉淀拟物渲染已启用" : "岁月沉淀已关闭，恢复原生纸张与字迹";
            }
            finally
            {
                _isSavingLocally = false;
            }
        }

        partial void OnAgingIntensityPercentChanged(double value)
        {
            if (_isUpdatingFromModel || _isSavingLocally) return;
            _isSavingLocally = true;
            try
            {
                double normalized = Math.Clamp(value / 100.0, 0.0, 1.0);
                _settingsService.Update(s => s.AgingIntensity = normalized);
            }
            finally
            {
                _isSavingLocally = false;
            }
        }

        partial void OnAgingPaperIntensityPercentChanged(double value)
        {
            if (_isUpdatingFromModel || _isSavingLocally) return;
            _isSavingLocally = true;
            try
            {
                double normalized = Math.Clamp(value / 100.0, 0.0, 1.0);
                _settingsService.Update(s => s.AgingPaperIntensity = normalized);
            }
            finally
            {
                _isSavingLocally = false;
            }
        }

        partial void OnAgingInkIntensityPercentChanged(double value)
        {
            if (_isUpdatingFromModel || _isSavingLocally) return;
            _isSavingLocally = true;
            try
            {
                double normalized = Math.Clamp(value / 100.0, 0.0, 1.0);
                _settingsService.Update(s => s.AgingInkIntensity = normalized);
            }
            finally
            {
                _isSavingLocally = false;
            }
        }

        [RelayCommand]
        public async Task SelectPaperTextureAsync()
        {
            try
            {
                var hwnd = MainWindow.GetActiveWindowHandle();
                if (hwnd == IntPtr.Zero) return;

                var picker = new FileOpenPicker();
                InitializeWithWindow.Initialize(picker, hwnd);
                picker.ViewMode = PickerViewMode.Thumbnail;
                picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".bmp");

                var file = await picker.PickSingleFileAsync();
                if (file != null && File.Exists(file.Path))
                {
                    string storedPath = await AgingTextureStorageService.Instance.SaveCustomTextureAsync(file.Path, "paper");
                    await AgingEngineService.Instance.GetPreprocessedPaperTexturePathAsync(storedPath, isDarkMode: false);
                    AgingPaperTexturePath = storedPath;
                    _settingsService.Update(s => s.AgingPaperTexturePath = storedPath);
                    UpdateAgingTextureDisplayNames();
                    RefreshAgingCacheSize();
                    AgingTextureStorageService.Instance.CleanupOrphanTextures(storedPath, AgingInkTexturePath);
                    AgingStatusMessage = "已成功载入并持久化存储自定义纸张纹理！";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SelectPaperTextureAsync Error] {ex.Message}");
                AgingStatusMessage = $"选择纸张纹理失败: {ex.Message}";
            }
        }

        [RelayCommand]
        public void ResetPaperTexture()
        {
            AgingPaperTexturePath = null;
            _settingsService.Update(s => s.AgingPaperTexturePath = null);
            UpdateAgingTextureDisplayNames();
            RefreshAgingCacheSize();
            AgingTextureStorageService.Instance.CleanupOrphanTextures(null, AgingInkTexturePath);
            AgingStatusMessage = "已重置为内置经典纸张纹理";
        }

        [RelayCommand]
        public async Task SelectInkTextureAsync()
        {
            try
            {
                var hwnd = MainWindow.GetActiveWindowHandle();
                if (hwnd == IntPtr.Zero) return;

                var picker = new FileOpenPicker();
                InitializeWithWindow.Initialize(picker, hwnd);
                picker.ViewMode = PickerViewMode.Thumbnail;
                picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".bmp");

                var file = await picker.PickSingleFileAsync();
                if (file != null && File.Exists(file.Path))
                {
                    string storedPath = await AgingTextureStorageService.Instance.SaveCustomTextureAsync(file.Path, "ink");
                    await AgingEngineService.Instance.GetInkWeatheringMaskPathAsync(storedPath);
                    AgingInkTexturePath = storedPath;
                    _settingsService.Update(s => s.AgingInkTexturePath = storedPath);
                    UpdateAgingTextureDisplayNames();
                    RefreshAgingCacheSize();
                    AgingTextureStorageService.Instance.CleanupOrphanTextures(AgingPaperTexturePath, storedPath);
                    AgingStatusMessage = "已成功载入并持久化存储自定义字迹老化纹理！";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SelectInkTextureAsync Error] {ex.Message}");
                AgingStatusMessage = $"选择字迹纹理失败: {ex.Message}";
            }
        }

        [RelayCommand]
        public void ResetInkTexture()
        {
            AgingInkTexturePath = null;
            _settingsService.Update(s => s.AgingInkTexturePath = null);
            UpdateAgingTextureDisplayNames();
            RefreshAgingCacheSize();
            AgingTextureStorageService.Instance.CleanupOrphanTextures(AgingPaperTexturePath, null);
            AgingStatusMessage = "已重置为内置经典字迹老化纹理";
        }

        [RelayCommand]
        public void ClearAgingCache()
        {
            try
            {
                AgingCacheService.Instance.ClearAllDiskCaches();
                RefreshAgingCacheSize();
                AgingStatusMessage = AgingCacheService.Instance.GetCacheSizeBytes() == 0
                    ? "已清理磁盘中的预处理贴图缓存。当前显示的贴图会在下次切换时更新。"
                    : "部分贴图缓存正在使用，请稍后再清理。";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClearAgingCache Error] {ex.Message}");
                AgingStatusMessage = $"清空缓存失败: {ex.Message}";
            }
        }

        public void RefreshAgingCacheSize()
        {
            try
            {
                long bytes = AgingCacheService.Instance.GetCacheSizeBytes();
                if (bytes < 1024)
                {
                    AgingCacheSizeText = $"{bytes} B";
                }
                else if (bytes < 1024 * 1024)
                {
                    AgingCacheSizeText = $"{bytes / 1024.0:F1} KB";
                }
                else
                {
                    AgingCacheSizeText = $"{bytes / (1024.0 * 1024.0):F2} MB";
                }
            }
            catch
            {
                AgingCacheSizeText = "0 KB";
            }
        }

        private void UpdateAgingTextureDisplayNames()
        {
            AgingPaperTextureDisplayName = AgingTextureStorageService.GetDisplayName(
                AgingPaperTexturePath, $"内置经典纸纹 ({AgingConstants.DefaultPaperTextureFileName})");

            AgingInkTextureDisplayName = AgingTextureStorageService.GetDisplayName(
                AgingInkTexturePath, $"内置经典墨渍 ({AgingConstants.DefaultInkTextureFileName})");
        }

        #endregion
    }
}
