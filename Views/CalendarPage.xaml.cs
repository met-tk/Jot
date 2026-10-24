using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Journal.Common;
using Journal.Controls;
using Journal.Models;
using Journal.Services;
using Journal.ViewModels;

namespace Journal.Views
{
    public sealed partial class CalendarPage : Page, IHierarchicalNavigable
    {
        public CalendarViewModel ViewModel { get; } = new();

        private readonly Stack<(CalendarViewMode Mode, DateTime Date, int Year, int Month)> _forwardHistory = new();
        private readonly HashSet<CalendarGridPanel> _yearGridPanels = new();
        private readonly Dictionary<DateTime, CalendarViewDayItem> _realizedDayItems = new();
        private CalendarGridPanel? _monthGridPanel;
        private double _currentZoomFactor = 1.0;
        private bool _isSyncingAuxCalendar;

        public CalendarPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;

            this.Loaded += OnPageLoaded;
            this.Unloaded += OnPageUnloaded;

            this.SizeChanged += (s, e) =>
            {
                UpdateResponsiveLayout();
                if (ViewModel.IsMonthView)
                {
                    UpdateMonthGridCellHeight();
                }
                else if (ViewModel.IsYearView)
                {
                    YearItemsRepeater?.InvalidateMeasure();
                }
            };

            ViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ViewModel.IsYearView) && ViewModel.IsYearView)
                {
                    DispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, () => ApplyZoomLayout());
                }
                else if (e.PropertyName == nameof(ViewModel.IsMonthView) && ViewModel.IsMonthView)
                {
                    DispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, () => ApplyZoomLayout());
                }
                else if (e.PropertyName == nameof(ViewModel.IsDayView) && ViewModel.IsDayView)
                {
                    DispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, () =>
                    {
                        UpdateResponsiveLayout();
                        RefreshDayAuxiliaryCalendar();
                        SyncAuxiliaryCalendarToDate(ViewModel.SelectedDate);
                    });
                }
                else if (e.PropertyName == nameof(ViewModel.SelectedDate) || e.PropertyName == nameof(ViewModel.SelectedDayHasDiary))
                {
                    if (ViewModel.IsDayView)
                    {
                        DispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, () =>
                        {
                            RefreshDayAuxiliaryCalendar();
                            SyncAuxiliaryCalendarToDate(ViewModel.SelectedDate);
                        });
                    }
                }
                else if (e.PropertyName == nameof(ViewModel.CurrentMonthDays) && ViewModel.IsMonthView)
                {
                    DispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, () => UpdateMonthGridCellHeight());
                }
            };

            // 挂载 Ctrl + 滚轮缩放监听 (handledEventsToo: true 穿透 ScrollViewer 内部拦截)
            this.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnPointerWheelChanged), handledEventsToo: true);
        }

        private async void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                SyncThemeFlag();
                ViewModel.Subscribe();
                AmbianceThemeEngine.AmbiancePaletteChanged -= OnAmbiancePaletteChanged;
                AmbianceThemeEngine.AmbiancePaletteChanged += OnAmbiancePaletteChanged;
                ApplyStoredZoom();
                UpdateResponsiveLayout();

                await ViewModel.RefreshDataAsync();

                if (ViewModel.IsDayView)
                {
                    RefreshDayAuxiliaryCalendar();
                    SyncAuxiliaryCalendarToDate(ViewModel.SelectedDate);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CalendarPage] OnPageLoaded exception: {ex.Message}");
            }
        }

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            ViewModel.Unsubscribe();
            AmbianceThemeEngine.AmbiancePaletteChanged -= OnAmbiancePaletteChanged;
            _yearGridPanels.Clear();
            _realizedDayItems.Clear();
            _monthGridPanel = null;
        }

        private void OnAmbiancePaletteChanged()
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (!this.IsLoaded) return;
                SyncThemeFlag();
                if (ViewModel.IsDayView)
                {
                    RefreshDayAuxiliaryCalendar();
                }
            });
        }

        private void SyncThemeFlag()
        {
            string ambiance = AppSettingsService.Instance.CurrentSettings.ThemeAmbiance ?? "default";
            bool isDark = string.Equals(ambiance, "midnightdark", StringComparison.OrdinalIgnoreCase) ||
                          this.ActualTheme == ElementTheme.Dark;
            DayHeatmapItem.IsDarkTheme = isDark;
        }

        private void YearCalendarGridPanel_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is CalendarGridPanel panel)
                {
                    _yearGridPanels.Add(panel);
                    panel.InvalidateMeasure();
                    panel.InvalidateArrange();
                }
            }
            catch { }
        }

        private void YearCalendarGridPanel_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is CalendarGridPanel panel)
                {
                    _yearGridPanels.Remove(panel);
                }
            }
            catch { }
        }

        private void MonthCalendarGridPanel_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is CalendarGridPanel panel)
                {
                    _monthGridPanel = panel;
                    UpdateMonthGridCellHeight();
                }
            }
            catch { }
        }

        private void MonthCalendarGridPanel_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ReferenceEquals(_monthGridPanel, sender))
                {
                    _monthGridPanel = null;
                }
            }
            catch { }
        }

        private void ApplyStoredZoom()
        {
            double factor = AppSettingsService.Instance.CurrentSettings.CalendarZoomFactor;
            _currentZoomFactor = Math.Clamp(factor, 0.8, 1.5);
            ApplyZoomLayout();
        }

        private void ApplyZoomLayout()
        {
            double yearCellH = Math.Round(24.0 * _currentZoomFactor);
            CalendarGridPanel.YearZoomCellHeight = yearCellH;

            // 年视图：自适应更新 UniformGridLayout 的项尺寸与内部 42 天日历格高度
            if (YearUniformGridLayout != null)
            {
                double targetWidth = Math.Clamp(230.0 * _currentZoomFactor, 196.0, 360.0);
                double targetHeight = Math.Round(6 * yearCellH + 5 * 4.0 + 78.0);
                YearUniformGridLayout.MinItemWidth = targetWidth;
                YearUniformGridLayout.MinItemHeight = targetHeight;

                foreach (var panel in _yearGridPanels.ToList())
                {
                    panel.InvalidateMeasure();
                    panel.InvalidateArrange();
                }
                YearItemsRepeater?.InvalidateMeasure();
            }

            // 月视图：通过调整 CalendarGridPanel 的 MinAdaptiveCellHeight 改变单日卡片高度
            UpdateMonthGridCellHeight();
        }

        private void UpdateMonthGridCellHeight()
        {
            var gridPanel = _monthGridPanel ?? (MonthScrollViewer != null ? FindVisualChild<CalendarGridPanel>(MonthScrollViewer) : null);
            if (gridPanel != null)
            {
                double scrollH = (MonthScrollViewer != null && MonthScrollViewer.ActualHeight > 120)
                    ? MonthScrollViewer.ActualHeight
                    : Math.Max(0, this.ActualHeight - 80.0);
                double viewportAvailableHeight = scrollH > 120 ? (scrollH - 72.0) : 0;
                double naturalCellHeight = viewportAvailableHeight > 0 ? (viewportAvailableHeight - 5 * 6.0) / 6.0 : 76.0;
                double baseHeight = Math.Max(72.0, naturalCellHeight);
                gridPanel.MinAdaptiveCellHeight = Math.Clamp(baseHeight * _currentZoomFactor, 54.0, 220.0);
                gridPanel.InvalidateMeasure();
                gridPanel.InvalidateArrange();
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed) return typed;
                var found = FindVisualChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        private void UpdateResponsiveLayout()
        {
            if (DayCalendarPanelCol != null && DayAuxiliaryPanel != null)
            {
                bool isCompact = this.ActualWidth < 880;
                DayCalendarPanelCol.Width = isCompact ? new GridLength(0) : GridLength.Auto;
                DayAuxiliaryPanel.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private static void ApplyCalendarDayItemStyle(CalendarViewDayItem item, bool isActive)
        {
            try
            {
                item.SetDensityColors(null);
                if (isActive)
                {
                    item.Background = AmbianceThemeEngine.ActiveCalendarDayBackgroundBrush;
                    item.BorderBrush = AmbianceThemeEngine.ActiveCalendarDayRingBrush;
                    item.BorderThickness = new Thickness(1.5);
                    item.CornerRadius = new CornerRadius(20);
                }
                else
                {
                    item.ClearValue(Control.BackgroundProperty);
                    item.ClearValue(Control.BorderBrushProperty);
                    item.ClearValue(Control.BorderThicknessProperty);
                }
            }
            catch { }
        }

        private void RefreshDayAuxiliaryCalendar()
        {
            if (DayAuxiliaryCalendarView == null || !this.IsLoaded) return;
            try
            {
                foreach (var kvp in _realizedDayItems.ToList())
                {
                    if (kvp.Value != null)
                    {
                        ApplyCalendarDayItemStyle(kvp.Value, ViewModel.ActiveDates.Contains(kvp.Key));
                    }
                }
            }
            catch { }
        }

        private void SyncAuxiliaryCalendarToDate(DateTime date)
        {
            if (!this.IsLoaded || DayAuxiliaryCalendarView == null) return;
            try
            {
                _isSyncingAuxCalendar = true;
                var target = date.Date;

                if (target >= DayAuxiliaryCalendarView.MinDate.DateTime && target <= DayAuxiliaryCalendarView.MaxDate.DateTime)
                {
                    DayAuxiliaryCalendarView.SetDisplayDate(target);
                    DayAuxiliaryCalendarView.SelectedDates.Clear();
                    DayAuxiliaryCalendarView.SelectedDates.Add(new DateTimeOffset(target));
                }
            }
            catch { }
            finally
            {
                _isSyncingAuxCalendar = false;
            }
        }

        private void DayAuxiliaryCalendarView_CalendarViewDayItemChanging(CalendarView sender, CalendarViewDayItemChangingEventArgs args)
        {
            if (args.Item == null) return;

            if (args.InRecycleQueue)
            {
                var staleKeys = _realizedDayItems
                    .Where(kv => ReferenceEquals(kv.Value, args.Item))
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var key in staleKeys)
                {
                    _realizedDayItems.Remove(key);
                }
                return;
            }

            if (args.Phase == 0)
            {
                var oldKeys = _realizedDayItems
                    .Where(kv => ReferenceEquals(kv.Value, args.Item))
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var key in oldKeys)
                {
                    _realizedDayItems.Remove(key);
                }

                var date = args.Item.Date.Date;
                _realizedDayItems[date] = args.Item;
                ApplyCalendarDayItemStyle(args.Item, ViewModel.ActiveDates.Contains(date));
            }
        }

        private async void DayAuxiliaryCalendarView_SelectedDatesChanged(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
        {
            if (_isSyncingAuxCalendar) return;

            try
            {
                if (args.AddedDates.Count > 0)
                {
                    var target = args.AddedDates[0].Date;
                    if (target != ViewModel.SelectedDate.Date)
                    {
                        await ViewModel.DrillDownToDayAsync(target);
                    }
                    SyncAuxiliaryCalendarToDate(target);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CalendarPage] DayAuxiliaryCalendarView_SelectedDatesChanged exception: {ex.Message}");
            }
        }

        private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var keyState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            bool isCtrlDown = keyState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) || e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control);

            if (isCtrlDown && (ViewModel.IsYearView || ViewModel.IsMonthView))
            {
                e.Handled = true;
                var wheelDelta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
                double delta = wheelDelta > 0 ? 0.08 : -0.08;
                _currentZoomFactor = Math.Clamp(_currentZoomFactor + delta, 0.8, 1.5);

                ApplyZoomLayout();

                // 异步防抖持久化日历缩放偏好
                AppSettingsService.Instance.Update(s => s.CalendarZoomFactor = _currentZoomFactor);
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            try
            {
                // 数据加载与日历同步统一由 OnPageLoaded 执行，避免首次导航时 OnNavigatedTo 与 Loaded 并发触发两次数据库查询与 UI 构建
                SyncThemeFlag();
                ApplyZoomLayout();
                UpdateResponsiveLayout();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CalendarPage] OnNavigatedTo exception: {ex.Message}");
            }
        }

        public static Visibility GetVisibility(bool isVisible) => isVisible ? Visibility.Visible : Visibility.Collapsed;
        public static Visibility GetInverseVisibility(bool isVisible) => isVisible ? Visibility.Collapsed : Visibility.Visible;

        private async void MonthHeader_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int month)
            {
                _forwardHistory.Clear();
                await ViewModel.DrillDownToMonthAsync(month);
                ApplyZoomLayout();
            }
        }

        private async void DayItem_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(sender as UIElement);
            if (point.Properties.IsLeftButtonPressed)
            {
                if (sender is FrameworkElement elem && elem.DataContext is DayHeatmapItem dayItem)
                {
                    e.Handled = true;
                    _forwardHistory.Clear();
                    await ViewModel.DrillDownToDayAsync(dayItem.Date);
                }
            }
        }

        private void EditCurrentDayDiary_Click(object sender, RoutedEventArgs e)
        {
            ShellPage.Current?.NavigateToEditor(ViewModel.SelectedDate);
        }

        private void EditCurrentDayDiary_AcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            if (ViewModel.IsDayView)
            {
                args.Handled = true;
                ShellPage.Current?.NavigateToEditor(ViewModel.SelectedDate);
            }
        }

        private async void DeleteCurrentDayDiary_Click(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.SelectedDayHasDiary) return;
            var targetDate = ViewModel.SelectedDate.Date;

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "删除日记确认",
                Content = $"确定要删除 {targetDate:yyyy年MM月dd日} 的这篇日记吗？\n删除后将从日历与目录索引移除，原文件会安全移入数据目录的 Trash 回收站。",
                PrimaryButtonText = "确认删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            AmbianceThemeEngine.ApplyToContentDialog(dialog);

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                bool ok = await ViewModel.DeleteCurrentDayDiaryAsync();
                if (ok)
                {
                    RefreshDayAuxiliaryCalendar();
                    SyncAuxiliaryCalendarToDate(targetDate);
                }
            }
        }

        public async Task<bool> TryHandleNavigateBackAsync()
        {
            if (ViewModel.ViewMode == CalendarViewMode.DayDetail)
            {
                _forwardHistory.Push((CalendarViewMode.DayDetail, ViewModel.SelectedDate, ViewModel.CurrentYear, ViewModel.CurrentMonth));
                await ViewModel.BackToUpperLevelAsync();
                return true;
            }
            else if (ViewModel.ViewMode == CalendarViewMode.MonthDetail)
            {
                _forwardHistory.Push((CalendarViewMode.MonthDetail, ViewModel.SelectedDate, ViewModel.CurrentYear, ViewModel.CurrentMonth));
                await ViewModel.BackToUpperLevelAsync();
                return true;
            }

            // 年视图下已处于顶层，返回 false 允许全局 Frame 路由后退
            return false;
        }

        public async Task<bool> TryHandleNavigateForwardAsync()
        {
            if (_forwardHistory.Count > 0)
            {
                var next = _forwardHistory.Pop();
                if (next.Mode == CalendarViewMode.DayDetail)
                {
                    await ViewModel.DrillDownToDayAsync(next.Date);
                    return true;
                }
                else if (next.Mode == CalendarViewMode.MonthDetail)
                {
                    ViewModel.CurrentYear = next.Year;
                    await ViewModel.DrillDownToMonthAsync(next.Month);
                    return true;
                }
            }

            return false;
        }
    }
}
