using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Journal.Common;
using Journal.Controls;
using Journal.Models;
using Journal.Services;
using Journal.ViewModels;

namespace Journal.Views
{
    public sealed partial class AnalyticsPage : Page, IHierarchicalNavigable
    {
        public AnalyticsViewModel ViewModel { get; } = new();
        private readonly AuxiliaryCalendarStyler _auxCalendarStyler = new();
        private bool _isSyncingAuxCalendar;

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
                _auxCalendarStyler.Clear();
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
                _isSyncingAuxCalendar = true;
                var target = date.Date;
                if (target >= ReadingAuxiliaryCalendarView.MinDate.DateTime && target <= ReadingAuxiliaryCalendarView.MaxDate.DateTime)
                {
                    ReadingAuxiliaryCalendarView.SetDisplayDate(target);
                    ReadingAuxiliaryCalendarView.SelectedDates.Clear();
                    ReadingAuxiliaryCalendarView.SelectedDates.Add(new DateTimeOffset(target));
                }
            }
            catch { }
            finally
            {
                _isSyncingAuxCalendar = false;
            }
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

        private async void LengthTierItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: LengthTierItem tier })
            {
                await ViewModel.SearchByLengthTierAsync(tier);
            }
        }

        private async void HourBar_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(sender as UIElement);
            if (!point.Properties.IsLeftButtonPressed) return;

            if (sender is FrameworkElement { Tag: HourlyHeatItem hourItem })
            {
                e.Handled = true;
                await ViewModel.SearchByHourAsync(hourItem.Hour);
            }
        }

        private async void HourRecordItem_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(sender as UIElement);
            if (!point.Properties.IsLeftButtonPressed) return;

            if (sender is FrameworkElement { Tag: HourRecordItem record })
            {
                e.Handled = true;
                await ViewModel.OpenReadingDateAsync(record.Date);
                SafeSetCalendarDisplayDate(record.Date);
            }
        }

        private async void WordDiaryCard_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(sender as UIElement);
            if (!point.Properties.IsLeftButtonPressed) return;

            var card = (sender as FrameworkElement)?.Tag as DiaryCardItem
                       ?? (sender as FrameworkElement)?.DataContext as DiaryCardItem;
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
            ShellPage.Current?.NavigateToEditor(ViewModel.ReadingDate, ViewModel.SelectedWord);
        }

        private void ReadingEdit_AcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            if (ViewModel.IsReadingMode)
            {
                args.Handled = true;
                ShellPage.Current?.NavigateToEditor(ViewModel.ReadingDate, ViewModel.SelectedWord);
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

        private void OnAmbiancePaletteChanged()
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (!this.IsLoaded) return;
                RefreshReadingAuxiliaryCalendar();
            });
        }

        private void RefreshReadingAuxiliaryCalendar()
        {
            if (ReadingAuxiliaryCalendarView == null || !this.IsLoaded) return;
            try
            {
                _auxCalendarStyler.Refresh(ViewModel.ActiveDates);
            }
            catch { }
        }

        private void ReadingAuxiliaryCalendarView_CalendarViewDayItemChanging(CalendarView sender, CalendarViewDayItemChangingEventArgs args)
        {
            _auxCalendarStyler.HandleDayItemChanging(args, ViewModel.ActiveDates);
        }

        private async void ReadingAuxiliaryCalendarView_SelectedDatesChanged(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
        {
            if (_isSyncingAuxCalendar) return;

            if (sender.SelectedDates.Count > 0)
            {
                var selected = sender.SelectedDates[0].Date;
                if (selected != ViewModel.ReadingDate.Date && ViewModel.ActiveDates.Contains(selected))
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
            else if (ViewModel.IsHourRecordsMode)
            {
                ViewModel.CloseHourRecords();
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
