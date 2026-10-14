using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Journal.Common;
using Journal.Models;
using Journal.Services;
using Journal.ViewModels;

namespace Journal.Views
{
    public sealed partial class AnalyticsPage : Page, IHierarchicalNavigable
    {
        public AnalyticsViewModel ViewModel { get; } = new();
        private readonly Dictionary<DateTime, CalendarViewDayItem> _realizedDayItems = new();

        public AnalyticsPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;

            this.Loaded += async (s, e) =>
            {
                ViewModel.Subscribe();
                ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
                ViewModel.PropertyChanged += OnViewModelPropertyChanged;
                AmbianceThemeEngine.AmbiancePaletteChanged -= OnAmbiancePaletteChanged;
                AmbianceThemeEngine.AmbiancePaletteChanged += OnAmbiancePaletteChanged;
                UpdateResponsiveLayout();
                await ViewModel.LoadAnalyticsAsync();
                RefreshReadingAuxiliaryCalendar();
                if (ViewModel.IsReadingMode)
                {
                    SafeSetCalendarDisplayDate(ViewModel.ReadingDate);
                    UpdateReadingHighlights();
                }
            };

            this.Unloaded += (s, e) =>
            {
                AmbianceThemeEngine.AmbiancePaletteChanged -= OnAmbiancePaletteChanged;
                ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
                _realizedDayItems.Clear();
                ViewModel.Unsubscribe();
            };

            this.SizeChanged += (s, e) =>
            {
                UpdateResponsiveLayout();
            };
        }

        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModel.ReadingDiaryContent) ||
                e.PropertyName == nameof(ViewModel.ReadingDiaryTitle) ||
                e.PropertyName == nameof(ViewModel.SelectedWord) ||
                e.PropertyName == nameof(ViewModel.ViewMode))
            {
                DispatcherQueue?.TryEnqueue(() =>
                {
                    if (ViewModel.IsReadingMode)
                    {
                        RefreshReadingAuxiliaryCalendar();
                        UpdateReadingHighlights();
                    }
                });
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            UpdateResponsiveLayout();
        }

        private void SafeSetCalendarDisplayDate(DateTime date)
        {
            if (!this.IsLoaded || ReadingAuxiliaryCalendarView == null || !ReadingAuxiliaryCalendarView.IsLoaded) return;
            try
            {
                if (date >= ReadingAuxiliaryCalendarView.MinDate.DateTime && date <= ReadingAuxiliaryCalendarView.MaxDate.DateTime)
                {
                    ReadingAuxiliaryCalendarView.SetDisplayDate(date);
                }
            }
            catch { }
        }

        public static Visibility GetVisibility(bool isVisible) => isVisible ? Visibility.Visible : Visibility.Collapsed;
        public static Visibility GetInverseVisibility(bool isVisible) => isVisible ? Visibility.Collapsed : Visibility.Visible;
        public static Visibility GetEmptyVisibility(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void UpdateResponsiveLayout()
        {
            if (ReadingCalendarPanelCol == null || ReadingAuxiliaryPanel == null) return;

            bool isCompact = this.ActualWidth < 880;
            ReadingCalendarPanelCol.Width = isCompact ? new GridLength(0) : GridLength.Auto;
            ReadingAuxiliaryPanel.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        }

        #region 词云穿透与日记卡片交互

        private async void WordCloudItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is WordCloudItem item)
            {
                await ViewModel.SearchByWordAsync(item.Word);
            }
        }

        private async void WordDiaryCard_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(sender as UIElement);
            if (!point.Properties.IsLeftButtonPressed) return;

            var card = (sender as FrameworkElement)?.DataContext as DiaryCardItem 
                       ?? (sender as FrameworkElement)?.Tag as DiaryCardItem;
            if (card != null)
            {
                e.Handled = true;
                await ViewModel.OpenReadingDiaryAsync(card);
                SafeSetCalendarDisplayDate(card.Date);
                UpdateReadingHighlights();
            }
        }

        private void WordDiaryCard_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement root && root.Tag is DiaryCardItem card)
            {
                string? keyword = card.HighlightKeyword ?? ViewModel.SelectedWord;
                if (root.FindName("CardTitleText") is TextBlock titleTb)
                {
                    ApplyTextHighlight(titleTb, card.Title, keyword);
                }
                if (root.FindName("CardExcerptText") is TextBlock excerptTb)
                {
                    ApplyTextHighlight(excerptTb, card.Excerpt, keyword);
                }
            }
        }

        private void ReadingEdit_Click(object sender, RoutedEventArgs e)
        {
            ShellPage.Current?.NavigateToEditor(ViewModel.ReadingDate);
        }

        private void ReadingEdit_AcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            if (ViewModel.IsReadingMode)
            {
                args.Handled = true;
                ShellPage.Current?.NavigateToEditor(ViewModel.ReadingDate);
            }
        }

        private void UpdateReadingHighlights()
        {
            if (!this.IsLoaded) return;
            if (ReadingDiaryContentText != null)
            {
                ApplyTextHighlight(ReadingDiaryContentText, ViewModel.ReadingDiaryContent, ViewModel.SelectedWord);
            }
            if (ReadingDiaryTitleText != null)
            {
                ApplyTextHighlight(ReadingDiaryTitleText, ViewModel.ReadingDiaryTitle, ViewModel.SelectedWord);
            }
        }

        public static void ApplyTextHighlight(TextBlock textBlock, string? text, string? keyword)
        {
            if (textBlock == null) return;
            textBlock.TextHighlighters.Clear();

            if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(keyword))
            {
                return;
            }

            var ranges = new List<TextRange>();
            int index = 0;
            const int MaxMatches = 200;
            while (true)
            {
                index = text.IndexOf(keyword, index, StringComparison.OrdinalIgnoreCase);
                if (index < 0) break;

                ranges.Add(new TextRange(index, keyword.Length));
                index += keyword.Length;
                if (ranges.Count >= MaxMatches) break;
            }

            if (ranges.Count == 0) return;

            var highlighter = new TextHighlighter();
            var bgBrush = AmbianceThemeEngine.WordHighlightBackgroundBrush;
            var fgBrush = AmbianceThemeEngine.WordHighlightForegroundBrush;

            if (bgBrush != null) highlighter.Background = bgBrush;
            if (fgBrush != null) highlighter.Foreground = fgBrush;

            foreach (var range in ranges)
            {
                highlighter.Ranges.Add(range);
            }

            textBlock.TextHighlighters.Add(highlighter);
        }

        #endregion

        #region 当月辅助日历联动

        private static SolidColorBrush? ActiveDayBackgroundBrush => AmbianceThemeEngine.ActiveCalendarDayBackgroundBrush;
        private static SolidColorBrush? ActiveDayBorderBrush => AmbianceThemeEngine.ActiveCalendarDayRingBrush;

        private void OnAmbiancePaletteChanged()
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (!this.IsLoaded) return;
                RefreshReadingAuxiliaryCalendar();
            });
        }

        private static void ApplyCalendarDayItemStyle(CalendarViewDayItem item, bool hasDiary)
        {
            try
            {
                item.SetDensityColors(null);
                if (hasDiary)
                {
                    item.Background = ActiveDayBackgroundBrush;
                    item.BorderBrush = ActiveDayBorderBrush;
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

        private void RefreshReadingAuxiliaryCalendar()
        {
            if (ReadingAuxiliaryCalendarView == null || !this.IsLoaded) return;
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

        private void ReadingAuxiliaryCalendarView_CalendarViewDayItemChanging(CalendarView sender, CalendarViewDayItemChangingEventArgs args)
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

                var itemDate = args.Item.Date.Date;
                _realizedDayItems[itemDate] = args.Item;
                ApplyCalendarDayItemStyle(args.Item, ViewModel.ActiveDates.Contains(itemDate));
            }
        }

        private async void ReadingAuxiliaryCalendarView_SelectedDatesChanged(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
        {
            if (sender.SelectedDates.Count > 0)
            {
                var selected = sender.SelectedDates[0].Date;
                if (ViewModel.ActiveDates.Contains(selected))
                {
                    await ViewModel.OpenReadingDateAsync(selected);
                }
            }
        }

        #endregion

        #region 全局鼠标侧键与分层导航 (IHierarchicalNavigable)

        public Task<bool> TryHandleNavigateBackAsync()
        {
            if (ViewModel.IsReadingMode)
            {
                ViewModel.CloseReading();
                return Task.FromResult(true);
            }
            else if (ViewModel.IsWordListMode)
            {
                ViewModel.CloseWordList();
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task<bool> TryHandleNavigateForwardAsync()
        {
            return Task.FromResult(false);
        }

        #endregion
    }
}
