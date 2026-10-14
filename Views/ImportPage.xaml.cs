using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Journal.Common;
using Journal.Models;
using Journal.ViewModels;

namespace Journal.Views
{
    public sealed partial class ImportPage : Page, IHierarchicalNavigable
    {
        public ImportViewModel ViewModel { get; } = new();

        private static readonly SolidColorBrush s_conflictBadgeBgBrush = new(Windows.UI.Color.FromArgb(0x28, 0xE0, 0x80, 0x00));
        private static readonly SolidColorBrush s_newBadgeBgBrush = new(Windows.UI.Color.FromArgb(0x22, 0x10, 0x89, 0x3E));
        private static readonly SolidColorBrush s_conflictBadgeFgBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xD0, 0x70, 0x00));
        private static readonly SolidColorBrush s_newBadgeFgBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x10, 0x89, 0x3E));

        public ImportPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;

            this.Loaded += (s, e) =>
            {
                UpdateTabButtonStyles();
                Journal.Services.AmbianceThemeEngine.AmbiancePaletteChanged -= OnAmbiancePaletteChanged;
                Journal.Services.AmbianceThemeEngine.AmbiancePaletteChanged += OnAmbiancePaletteChanged;
            };

            this.Unloaded += (s, e) =>
            {
                Journal.Services.AmbianceThemeEngine.AmbiancePaletteChanged -= OnAmbiancePaletteChanged;
            };
        }

        private void OnAmbiancePaletteChanged()
        {
            this.DispatcherQueue?.TryEnqueue(() =>
            {
                if (!this.IsLoaded) return;
                UpdateTabButtonStyles();
            });
        }

        #region 双模选项卡切换与样式联动

        private void TabBtnSideBySide_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.PreviewTabMode = ImportPreviewTabMode.SideBySide;
            UpdateTabButtonStyles();
        }

        private void TabBtnMerged_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.PreviewTabMode = ImportPreviewTabMode.MergedSimulation;
            UpdateTabButtonStyles();
        }

        private void UpdateTabButtonStyles()
        {
            var res = Application.Current?.Resources;
            if (res == null) return;

            if (ViewModel.PreviewTabMode == ImportPreviewTabMode.SideBySide)
            {
                if (res.TryGetValue("AccentButtonStyle", out var accentStyle) && accentStyle is Style s1)
                {
                    TabBtnSideBySide.Style = s1;
                }
                if (res.TryGetValue("DefaultButtonStyle", out var defaultStyle) && defaultStyle is Style s2)
                {
                    TabBtnMerged.Style = s2;
                }
            }
            else
            {
                if (res.TryGetValue("DefaultButtonStyle", out var defaultStyle) && defaultStyle is Style s1)
                {
                    TabBtnSideBySide.Style = s1;
                }
                if (res.TryGetValue("AccentButtonStyle", out var accentStyle) && accentStyle is Style s2)
                {
                    TabBtnMerged.Style = s2;
                }
            }
        }

        #endregion

        #region 冲突策略、切分规则与微操作交互响应

        private void ConflictStrategyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb && cb.SelectedItem is ComboBoxItem item)
            {
                string tag = item.Tag?.ToString() ?? "MergeAppend";
                ViewModel.ConflictStrategy = tag switch
                {
                    "Overwrite" => ImportConflictStrategy.Overwrite,
                    "Skip" => ImportConflictStrategy.Skip,
                    _ => ImportConflictStrategy.MergeAppend
                };
            }
        }

        private void FilterModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb)
            {
                ViewModel.FilterMode = cb.SelectedIndex switch
                {
                    1 => ImportFilterMode.ConflictOnly,
                    2 => ImportFilterMode.NewOnly,
                    _ => ImportFilterMode.All
                };
            }
        }

        private void RulePresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb)
            {
                ViewModel.SelectedRulePreset = (ImportSplitRulePreset)cb.SelectedIndex;
            }
        }

        private async void ImportSingleEntryBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.DataContext is ImportedEntryPreview entry)
            {
                await ViewModel.ImportSingleEntryAsync(entry);
            }
        }

        private void RemoveEntryBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.DataContext is ImportedEntryPreview entry)
            {
                ViewModel.RemoveEntry(entry);
            }
        }

        private async void ImportSelectedEntryBtn_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedEntry != null)
            {
                await ViewModel.ImportSingleEntryAsync(ViewModel.SelectedEntry);
            }
        }

        #endregion

        #region 原生文件与文件夹拖拽投放 (Drag and Drop)

        private void Page_DragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.DragUIOverride.Caption = "释放以载入并解析日记";
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.IsContentVisible = true;
                e.DragUIOverride.IsGlyphVisible = true;
            }
            else
            {
                e.AcceptedOperation = DataPackageOperation.None;
            }
        }

        private async void Page_Drop(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

            var def = e.GetDeferral();
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                if (items != null && items.Count > 0)
                {
                    var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
                    await ViewModel.ProcessPathsAsync(paths);
                }
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = $"处理拖拽文件失败: {ex.Message}";
            }
            finally
            {
                def.Complete();
            }
        }

        #endregion

        #region 全局鼠标侧键与分层导航 (IHierarchicalNavigable)

        public Task<bool> TryHandleNavigateBackAsync()
        {
            // 若用户需要后退，交给上层 ShellPage 路由后退
            return Task.FromResult(false);
        }

        public Task<bool> TryHandleNavigateForwardAsync()
        {
            return Task.FromResult(false);
        }

        #endregion

        #region 静态 XAML 辅助转换方法

        public static Visibility GetVisibility(bool isVisible) => isVisible ? Visibility.Visible : Visibility.Collapsed;
        public static Visibility GetInvertedVisibility(bool isVisible) => isVisible ? Visibility.Collapsed : Visibility.Visible;
        public static Visibility GetCountVisibility(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;
        public static bool GetInvertedBool(bool val) => !val;

        public static Visibility GetTabVisibility(ImportPreviewTabMode currentMode, int targetTab)
        {
            int current = (int)currentMode;
            return current == targetTab ? Visibility.Visible : Visibility.Collapsed;
        }

        public static int ConflictStrategyToIndex(ImportConflictStrategy strategy)
        {
            return strategy switch
            {
                ImportConflictStrategy.Overwrite => 1,
                ImportConflictStrategy.Skip => 2,
                _ => 0
            };
        }

        public static int FilterModeToIndex(ImportFilterMode filter)
        {
            return (int)filter;
        }

        public static int RulePresetToIndex(ImportSplitRulePreset preset)
        {
            return (int)preset;
        }

        public static Brush GetConflictBadgeBackground(bool hasConflict)
        {
            return hasConflict ? s_conflictBadgeBgBrush : s_newBadgeBgBrush;
        }

        public static Brush GetConflictBadgeForeground(bool hasConflict)
        {
            return hasConflict ? s_conflictBadgeFgBrush : s_newBadgeFgBrush;
        }

        public static string StrategyToDisplayName(ImportConflictStrategy strategy)
        {
            return strategy switch
            {
                ImportConflictStrategy.Overwrite => "覆盖已有日记",
                ImportConflictStrategy.Skip => "跳过已有日期",
                _ => "智能合并追加 (推荐)"
            };
        }

        #endregion
    }
}
