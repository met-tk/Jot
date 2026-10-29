using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using Windows.UI.Core;
using Journal.Common;

namespace Journal.Views
{
    public sealed partial class ShellPage : Page
    {
        public static ShellPage? Current { get; private set; }
        private string _currentTag = string.Empty;
        private DateTime _lastNavTimestamp = DateTime.MinValue;

        public ShellPage()
        {
            this.InitializeComponent();
            Current = this;

            // 全局穿透监听 PreviewKeyDown (捕获 Ctrl+1~6 快捷切换，支持主键盘与小键盘)
            this.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnShellPreviewKeyDown), handledEventsToo: true);

            // 全局穿透监听 PointerPressed (捕获 XButton1/XButton2)
            this.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnGlobalPointerPressed), handledEventsToo: true);

            // 监听 Frame 导航以自动保持左侧胶囊高亮与实际页面一致
            ContentFrame.Navigated += ContentFrame_Navigated;

            // 监听全局氛围色调变更通知，确保左侧菜单高亮 100% 独立正交、即时刷新，杜绝预设间色彩残留
            this.Loaded += (s, e) =>
            {
                Journal.Services.AmbianceThemeEngine.AmbiancePaletteChanged -= OnAmbiancePaletteChanged;
                Journal.Services.AmbianceThemeEngine.AmbiancePaletteChanged += OnAmbiancePaletteChanged;
                RefreshNavigationThemeBrushes();
            };
            this.Unloaded += (s, e) =>
            {
                Journal.Services.AmbianceThemeEngine.AmbiancePaletteChanged -= OnAmbiancePaletteChanged;
            };
        }

        private void OnAmbiancePaletteChanged()
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                RefreshNavigationThemeBrushes();
            });
        }

        private void RefreshNavigationThemeBrushes()
        {
            var indicatorBrush = Journal.Services.AmbianceThemeEngine.NavIndicatorBrush;
            var activeTextBrush = Journal.Services.AmbianceThemeEngine.NavActiveTextBrush;
            var activeBgBrush = Journal.Services.AmbianceThemeEngine.NavActiveBackgroundBrush;

            if (indicatorBrush == null || activeTextBrush == null || activeBgBrush == null) return;

            // 1. 即时刷新指示条背景
            IndicatorToday.Background = indicatorBrush;
            IndicatorCalendar.Background = indicatorBrush;
            IndicatorList.Background = indicatorBrush;
            IndicatorAnalytics.Background = indicatorBrush;
            IndicatorImport.Background = indicatorBrush;
            IndicatorSettings.Background = indicatorBrush;

            // 2. 即时刷新激活图标前景色
            IconTodayActive.Foreground = activeTextBrush;
            IconCalendarActive.Foreground = activeTextBrush;
            IconListActive.Foreground = activeTextBrush;
            IconAnalyticsActive.Foreground = activeTextBrush;
            IconImportActive.Foreground = activeTextBrush;
            IconSettingsActive.Foreground = activeTextBrush;

            // 3. 即时刷新激活状态背后的微透胶囊底板
            BgToday.Background = activeBgBrush;
            BgCalendar.Background = activeBgBrush;
            BgList.Background = activeBgBrush;
            BgAnalytics.Background = activeBgBrush;
            BgImport.Background = activeBgBrush;
            BgSettings.Background = activeBgBrush;

            // 4. 即时刷新左侧悬浮胶囊导航岛外框背景与边框，杜绝跨色调切换时的底板残留
            if (ShellIslandContainer != null)
            {
                ShellIslandContainer.Background = Journal.Services.AmbianceThemeEngine.ShellIslandBackgroundBrush;
                ShellIslandContainer.BorderBrush = Journal.Services.AmbianceThemeEngine.ShellIslandBorderBrush;
            }
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshNavigationThemeBrushes();
            if (string.IsNullOrEmpty(_currentTag))
            {
                SelectTab("Today");
            }
        }

        private void NavButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                SelectTab(tag);
            }
        }

        public void SelectTab(string tag)
        {
            Type? targetType = tag switch
            {
                "Today" => typeof(EditorPage),
                "Calendar" => typeof(CalendarPage),
                "List" => typeof(DiaryListPage),
                "Analytics" => typeof(AnalyticsPage),
                "Import" => typeof(ImportPage),
                "Settings" => typeof(SettingsPage),
                _ => null
            };

            if (targetType == null) return;

            // 若当前已经在目标页面上，避免重复触发导航与重置
            if (string.Equals(_currentTag, tag, StringComparison.OrdinalIgnoreCase) && 
                ContentFrame.CurrentSourcePageType == targetType)
            {
                if (tag == "Today" && ContentFrame.Content is EditorPage editorPage)
                {
                    if (editorPage.ViewModel.CurrentDate.Date != DateTime.Today)
                    {
                        ContentFrame.Navigate(typeof(EditorPage), DateTime.Today);
                    }
                }
                return;
            }

            _currentTag = tag;
            UpdateNavVisualState(tag);

            try
            {
                switch (tag)
                {
                    case "Today":
                        ContentFrame.Navigate(typeof(EditorPage), DateTime.Today);
                        break;
                    case "Calendar":
                        ContentFrame.Navigate(typeof(CalendarPage));
                        break;
                    case "List":
                        ContentFrame.Navigate(typeof(DiaryListPage));
                        break;
                    case "Analytics":
                        ContentFrame.Navigate(typeof(AnalyticsPage));
                        break;
                    case "Import":
                        ContentFrame.Navigate(typeof(ImportPage));
                        break;
                    case "Settings":
                        ContentFrame.Navigate(typeof(SettingsPage));
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ShellPage] SelectTab failed: {ex.Message}");
            }
        }

        private void UpdateNavVisualState(string activeTag)
        {
            SetItemVisual(BgToday, IndicatorToday, IconTodayNormal, IconTodayActive, activeTag == "Today");
            SetItemVisual(BgCalendar, IndicatorCalendar, IconCalendarNormal, IconCalendarActive, activeTag == "Calendar");
            SetItemVisual(BgList, IndicatorList, IconListNormal, IconListActive, activeTag == "List");
            SetItemVisual(BgAnalytics, IndicatorAnalytics, IconAnalyticsNormal, IconAnalyticsActive, activeTag == "Analytics");
            SetItemVisual(BgImport, IndicatorImport, IconImportNormal, IconImportActive, activeTag == "Import");
            SetItemVisual(BgSettings, IndicatorSettings, IconSettingsNormal, IconSettingsActive, activeTag == "Settings");
        }

        private static void SetItemVisual(Border bg, Border indicator, FontIcon normalIcon, FontIcon activeIcon, bool isSelected)
        {
            bg.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
            indicator.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
            activeIcon.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
            normalIcon.Visibility = isSelected ? Visibility.Collapsed : Visibility.Visible;
        }

        public void NavigateToEditor(DateTime date, string? searchKeyword = null)
        {
            _currentTag = "Today";
            UpdateNavVisualState("Today");
            bool navigated = ContentFrame.Navigate(typeof(EditorPage), date);
            if (!navigated && ContentFrame.Content is EditorPage page)
            {
                _ = page.LoadDateExplicitlyAsync(date);
            }

            if (!string.IsNullOrWhiteSpace(searchKeyword) && ContentFrame.Content is EditorPage activeEditor)
            {
                activeEditor.OpenFindBarWithKeyword(searchKeyword);
            }
        }

        public EditorPage? CurrentEditorPage => ContentFrame.Content as EditorPage;

        private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
        {
            string? matchedTag = e.SourcePageType switch
            {
                Type t when t == typeof(EditorPage) => "Today",
                Type t when t == typeof(CalendarPage) => "Calendar",
                Type t when t == typeof(DiaryListPage) => "List",
                Type t when t == typeof(AnalyticsPage) => "Analytics",
                Type t when t == typeof(ImportPage) => "Import",
                Type t when t == typeof(SettingsPage) => "Settings",
                _ => null
            };

            if (!string.IsNullOrEmpty(matchedTag))
            {
                _currentTag = matchedTag;
                UpdateNavVisualState(matchedTag);
            }
        }

        private void OnShellPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            bool isCtrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                           & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            bool isAlt = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
                          & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            bool isShift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                            & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

            if (isCtrl && !isAlt && !isShift)
            {
                switch (e.Key)
                {
                    case VirtualKey.Number1:
                    case VirtualKey.NumberPad1:
                        e.Handled = true;
                        SelectTab("Today");
                        break;
                    case VirtualKey.Number2:
                    case VirtualKey.NumberPad2:
                        e.Handled = true;
                        SelectTab("Calendar");
                        break;
                    case VirtualKey.Number3:
                    case VirtualKey.NumberPad3:
                        e.Handled = true;
                        SelectTab("List");
                        break;
                    case VirtualKey.Number4:
                    case VirtualKey.NumberPad4:
                        e.Handled = true;
                        SelectTab("Analytics");
                        break;
                    case VirtualKey.Number5:
                    case VirtualKey.NumberPad5:
                        e.Handled = true;
                        SelectTab("Import");
                        break;
                    case VirtualKey.Number6:
                    case VirtualKey.NumberPad6:
                        e.Handled = true;
                        SelectTab("Settings");
                        break;
                }
            }
        }

        private async void OnGlobalPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(this);
            var props = point.Properties;

            if (props.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.XButton1Pressed || props.IsXButton1Pressed)
            {
                e.Handled = true;
                await TriggerGlobalBackAsync();
            }
            else if (props.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.XButton2Pressed || props.IsXButton2Pressed)
            {
                e.Handled = true;
                await TriggerGlobalForwardAsync();
            }
        }

        public async Task<bool> TriggerGlobalBackAsync()
        {
            if (DateTime.UtcNow - _lastNavTimestamp < TimeSpan.FromMilliseconds(200)) return false;
            _lastNavTimestamp = DateTime.UtcNow;

            if (ContentFrame.Content is IHierarchicalNavigable navigable)
            {
                bool handled = await navigable.TryHandleNavigateBackAsync();
                if (handled) return true;
            }

            if (ContentFrame.CanGoBack)
            {
                ContentFrame.GoBack();
                return true;
            }
            return false;
        }

        public async Task<bool> TriggerGlobalForwardAsync()
        {
            if (DateTime.UtcNow - _lastNavTimestamp < TimeSpan.FromMilliseconds(200)) return false;
            _lastNavTimestamp = DateTime.UtcNow;

            if (ContentFrame.Content is IHierarchicalNavigable navigable)
            {
                bool handled = await navigable.TryHandleNavigateForwardAsync();
                if (handled) return true;
            }

            if (ContentFrame.CanGoForward)
            {
                ContentFrame.GoForward();
                return true;
            }
            return false;
        }
    }
}

