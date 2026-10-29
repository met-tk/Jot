using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using Journal.Common;
using Journal.Controls;
using Journal.Models;
using Journal.Services;
using Journal.ViewModels;

namespace Journal.Views
{
    public sealed partial class DiaryListPage : Page, IHierarchicalNavigable
    {
        public DiaryListViewModel ViewModel { get; } = new();
        private DateTime? _selectedAuxDate;
        private readonly AuxiliaryCalendarStyler _auxCalendarStyler = new();

        public DiaryListPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;

            this.Loaded += async (s, e) =>
            {
                this.SizeChanged -= Page_SizeChanged;
                this.SizeChanged += Page_SizeChanged;
                ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
                ViewModel.PropertyChanged += OnViewModelPropertyChanged;
                AmbianceThemeEngine.AmbiancePaletteChanged -= OnAmbiancePaletteChanged;
                AmbianceThemeEngine.AmbiancePaletteChanged += OnAmbiancePaletteChanged;
                ViewModel.Subscribe();
                await ViewModel.LoadDataAsync();
                RefreshAuxiliaryCalendar();
            };

            this.Unloaded += (s, e) =>
            {
                this.SizeChanged -= Page_SizeChanged;
                ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
                AmbianceThemeEngine.AmbiancePaletteChanged -= OnAmbiancePaletteChanged;
                _highlightCts?.Cancel();
                _highlightCts?.Dispose();
                _highlightCts = null;
                _auxCalendarStyler.Clear();
                _cardElementMap.Clear();
                ViewModel.Unsubscribe();
            };
        }

        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModel.SearchKeyword) ||
                e.PropertyName == nameof(ViewModel.ReadingDiaryContent) ||
                e.PropertyName == nameof(ViewModel.ReadingDiaryTitle))
            {
                DispatcherQueue?.TryEnqueue(() =>
                {
                    if (ViewModel.IsReadingDay)
                    {
                        ApplyReadingSearchHighlight();
                    }
                });
            }
        }

        public Task<bool> TryHandleNavigateBackAsync()
        {
            if (ViewModel.IsReadingDay)
            {
                ViewModel.CloseReadingDay();
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task<bool> TryHandleNavigateForwardAsync()
        {
            return Task.FromResult(false);
        }

        private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (CalendarPanelCol == null || AuxiliaryPanel == null) return;

            bool isCompact = e.NewSize.Width < 880;
            CalendarPanelCol.Width = isCompact ? new GridLength(0) : GridLength.Auto;
            AuxiliaryPanel.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            // 数据刷新统一在 Loaded 事件中执行（NavigationCacheMode.Required 下每次切回也会触发 Loaded），避免重复查询
        }

        #region 静态绑定辅助转换器

        public static Visibility GetVisibility(bool isVisible) => isVisible ? Visibility.Visible : Visibility.Collapsed;
        public static Visibility GetInverseVisibility(bool isVisible) => isVisible ? Visibility.Collapsed : Visibility.Visible;
        public static Visibility GetEmptyVisibility(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;
        public static Visibility GetExcerptVisibility(string? excerpt) => string.IsNullOrWhiteSpace(excerpt) ? Visibility.Collapsed : Visibility.Visible;
        public static string GetSelectionBadgeText(int count, int words) => $"已勾选 {count} 篇日记（共 {words:N0} 字）";

        #endregion

        #region 卡片点击与只读阅读交互

        private async void DiaryCard_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(sender as UIElement);
            if (!point.Properties.IsLeftButtonPressed) return;

            if (sender is FrameworkElement elem)
            {
                var card = elem.Tag as DiaryCardItem ?? elem.DataContext as DiaryCardItem;
                if (card != null)
                {
                    e.Handled = true;
                    if (ViewModel.IsMultiSelectMode)
                    {
                        card.IsSelected = !card.IsSelected;
                    }
                    else
                    {
                        await ViewModel.OpenReadingDayAsync(card);
                        SyncAuxiliaryCalendarToDate(card.Date);
                        ApplyReadingSearchHighlight();
                    }
                }
            }
        }

        private void ApplyReadingSearchHighlight()
        {
            if (!this.IsLoaded) return;
            string? kw = ViewModel.SearchKeyword?.Trim();
            if (ReadingDiaryTitleText != null)
            {
                AnalyticsPage.ApplyTextHighlight(ReadingDiaryTitleText, ViewModel.ReadingDiaryTitle, kw);
            }
            if (ReadingDiaryContentText != null)
            {
                AnalyticsPage.ApplyTextHighlight(ReadingDiaryContentText, ViewModel.ReadingDiaryContent, kw);
            }
        }

        private void ReadingEdit_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.ReadingCard != null)
            {
                ShellPage.Current?.NavigateToEditor(ViewModel.ReadingCard.Date, ViewModel.SearchKeyword?.Trim());
            }
        }

        private void ReadingEdit_AcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            if (ViewModel.IsReadingDay && ViewModel.ReadingCard != null)
            {
                args.Handled = true;
                ShellPage.Current?.NavigateToEditor(ViewModel.ReadingCard.Date, ViewModel.SearchKeyword?.Trim());
            }
        }

        private async void ReadingDelete_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.ReadingCard == null) return;
            var card = ViewModel.ReadingCard;

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "删除日记确认",
                Content = $"确定要删除 {card.Date:yyyy年MM月dd日} 的这篇日记吗？\n删除后将从目录与日历索引移除，原文件会安全移入数据目录的 Trash 回收站。",
                PrimaryButtonText = "确认删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            AmbianceThemeEngine.ApplyToContentDialog(dialog);

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                bool ok = await ViewModel.DeleteDiaryAsync(card.Date, card.FilePath);
                if (ok)
                {
                    RefreshAuxiliaryCalendar();
                }
            }
        }

        #endregion

        #region 右键菜单交互 (MenuFlyout)

        private DiaryCardItem? _lastContextCard;

        private void DiaryCard_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            if (sender is FrameworkElement fe)
            {
                _lastContextCard = fe.Tag as DiaryCardItem ?? fe.DataContext as DiaryCardItem;
                if (_lastContextCard != null && SharedCardContextFlyout != null)
                {
                    if (args.TryGetPosition(fe, out var point))
                    {
                        SharedCardContextFlyout.ShowAt(fe, point);
                    }
                    else
                    {
                        SharedCardContextFlyout.ShowAt(fe);
                    }
                    args.Handled = true;
                }
            }
        }

        private DiaryCardItem? GetCardFromSender(object sender)
        {
            if (sender is FrameworkElement fe)
            {
                if (fe.Tag is DiaryCardItem tagCard) return tagCard;
                if (fe.DataContext is DiaryCardItem directCard) return directCard;
            }

            if (sender is MenuFlyoutItem mfi)
            {
                if (mfi.Tag is DiaryCardItem tCard) return tCard;
                if (mfi.DataContext is DiaryCardItem dCard) return dCard;

                // 向上追溯附着的 Target 宿主控件
                DependencyObject? current = mfi;
                while (current != null)
                {
                    if (current is MenuFlyout mf && mf.Target is FrameworkElement targetFe)
                    {
                        if (targetFe.Tag is DiaryCardItem targetTagCard) return targetTagCard;
                        if (targetFe.DataContext is DiaryCardItem targetDataCard) return targetDataCard;
                    }
                    current = VisualTreeHelper.GetParent(current) ?? (current as FrameworkElement)?.Parent;
                }
            }

            return _lastContextCard;
        }

        private void MenuEdit_Click(object sender, RoutedEventArgs e)
        {
            var card = GetCardFromSender(sender);
            if (card != null)
            {
                ShellPage.Current?.NavigateToEditor(card.Date, ViewModel.SearchKeyword?.Trim());
            }
        }

        private async void MenuCopy_Click(object sender, RoutedEventArgs e)
        {
            var card = GetCardFromSender(sender);
            if (card == null) return;

            try
            {
                string content = await DiaryStorageService.Instance.LoadDiaryContentAsync(card.Date);
                if (string.IsNullOrWhiteSpace(content))
                {
                    content = card.Excerpt ?? string.Empty;
                }

                var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage
                {
                    RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy
                };
                dataPackage.SetText(content.Trim());
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
                Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MenuCopy_Click error: {ex.Message}");
                ShowSimpleDialog("复制失败", $"无法将日记内容复制到剪贴板：{ex.Message}");
            }
        }

        private async void MenuDelete_Click(object sender, RoutedEventArgs e)
        {
            var card = GetCardFromSender(sender);
            if (card == null) return;

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "删除日记确认",
                Content = $"确定要删除 {card.Date:yyyy年MM月dd日} 的这篇日记吗？\n删除后将从目录与日历索引移除，原文件会安全移入数据目录的 Trash 回收站。",
                PrimaryButtonText = "确认删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            AmbianceThemeEngine.ApplyToContentDialog(dialog);

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                bool ok = await ViewModel.DeleteDiaryAsync(card.Date, card.FilePath);
                if (ok)
                {
                    RefreshAuxiliaryCalendar();
                }
            }
        }

        #endregion

        #region 多选批量操作与导出

        private async void BatchDelete_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedCardsCount == 0)
            {
                ShowSimpleDialog("未选择日记", "请先在时间线中勾选需要删除的日记篇目。");
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "批量删除确认",
                Content = $"确定要删除已选中的 {ViewModel.SelectedCardsCount} 篇日记吗？\n删除后将从目录与日历索引移除，原文件会安全移入数据目录的 Trash 回收站。",
                PrimaryButtonText = $"确认删除 ({ViewModel.SelectedCardsCount})",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            AmbianceThemeEngine.ApplyToContentDialog(dialog);

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                int count = await ViewModel.DeleteBatchSelectedAsync();
                RefreshAuxiliaryCalendar();
                ShowSimpleDialog("批量删除完成", $"已成功移入回收站并从索引清除 {count} 篇日记。");
            }
        }

        private void BatchExport_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Flyout != null)
            {
                btn.Flyout.ShowAt(btn);
            }
        }

        private async void ExportBatchMarkdown_Click(object sender, RoutedEventArgs e)
        {
            await PickAndExportBatchFolderAsync(ExportFormat.Markdown);
        }

        private async void ExportBatchTxt_Click(object sender, RoutedEventArgs e)
        {
            await PickAndExportBatchFolderAsync(ExportFormat.Txt);
        }

        private async void ExportBatchJson_Click(object sender, RoutedEventArgs e)
        {
            await PickAndExportBatchFileAsync(ExportFormat.Json, "日记结构化数据", ".json", "JSON 数据文件 (*.json)");
        }

        private async Task PickAndExportBatchFolderAsync(ExportFormat format)
        {
            if (ViewModel.SelectedCardsCount == 0)
            {
                ShowSimpleDialog("未勾选日记", "请在时间线中勾选至少一篇日记后再进行批量导出。");
                return;
            }

            string? folderPath = await PickExportFolderPathAsync();
            if (string.IsNullOrWhiteSpace(folderPath)) return;

            var result = await ViewModel.ExportSelectedAsync(format, folderPath);
            ShowExportResultDialog(result);
        }

        private async Task PickAndExportBatchFileAsync(ExportFormat format, string prefix, string ext, string desc)
        {
            if (ViewModel.SelectedCardsCount == 0)
            {
                ShowSimpleDialog("未勾选日记", "请在时间线中勾选至少一篇日记后再进行批量导出。");
                return;
            }

            string defaultName = $"{prefix}_{DateTime.Now:yyyyMMdd_HHmm}";
            string? destinationPath = await PickSaveFilePathAsync(defaultName, ext, desc);
            if (string.IsNullOrWhiteSpace(destinationPath)) return;

            var result = await ViewModel.ExportSelectedAsync(format, destinationPath);
            ShowExportResultDialog(result);
        }

        private async Task<string?> PickExportFolderPathAsync()
        {
            try
            {
                var folderPicker = new FolderPicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary
                };
                folderPicker.FileTypeFilter.Add("*");

                IntPtr hwnd = MainWindow.GetActiveWindowHandle();
                if (hwnd != IntPtr.Zero)
                {
                    WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);
                }

                var folder = await folderPicker.PickSingleFolderAsync();
                return folder?.Path;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PickExportFolderPathAsync error: {ex.Message}");
                return null;
            }
        }

        private async Task<string?> PickSaveFilePathAsync(string defaultFileName, string extension, string fileTypeDescription)
        {
            try
            {
                var savePicker = new FileSavePicker();
                IntPtr hwnd = MainWindow.GetActiveWindowHandle();
                if (hwnd != IntPtr.Zero)
                {
                    WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);
                }

                savePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                savePicker.SuggestedFileName = defaultFileName;
                savePicker.FileTypeChoices.Add(fileTypeDescription, new List<string> { extension });

                var file = await savePicker.PickSaveFileAsync();
                return file?.Path;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PickSaveFilePathAsync error: {ex.Message}");
                return null;
            }
        }

        private void ShowExportResultDialog(ExportResult result)
        {
            if (result.Success)
            {
                bool isFolder = Directory.Exists(result.OutputPath);
                string message = isFolder
                    ? $"已成功批量导出 {result.EntryCount} 篇独立日记文件（共 {result.TotalWords:N0} 字）。\n导出目录：\n{result.OutputPath}"
                    : $"已成功导出 {result.EntryCount} 篇日记（共 {result.TotalWords:N0} 字）。\n文件路径：\n{result.OutputPath}";

                var dialog = new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    Title = "导出成功",
                    Content = message,
                    PrimaryButtonText = "打开所在文件夹",
                    CloseButtonText = "知道了",
                    DefaultButton = ContentDialogButton.Primary
                };
                AmbianceThemeEngine.ApplyToContentDialog(dialog);

                dialog.PrimaryButtonClick += (s, e) =>
                {
                    try
                    {
                        if (Directory.Exists(result.OutputPath))
                        {
                            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{result.OutputPath}\"") { UseShellExecute = true });
                        }
                        else
                        {
                            string dir = Path.GetDirectoryName(result.OutputPath) ?? string.Empty;
                            if (Directory.Exists(dir))
                            {
                                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{result.OutputPath}\"") { UseShellExecute = true });
                            }
                        }
                    }
                    catch { }
                };

                _ = dialog.ShowAsync();
            }
            else
            {
                ShowSimpleDialog("导出未完成", result.ErrorMessage ?? "导出过程中发生未知异常。");
            }
        }

        private void ShowSimpleDialog(string title, string content)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = title,
                Content = content,
                CloseButtonText = "知道了"
            };
            AmbianceThemeEngine.ApplyToContentDialog(dialog);
            _ = dialog.ShowAsync();
        }

        #endregion

        #region 辅助迷你日历联动

        private bool _isSyncingAuxCalendar = false;

        private void OnAmbiancePaletteChanged()
        {
            this.DispatcherQueue?.TryEnqueue(() =>
            {
                if (!this.IsLoaded) return;
                RefreshAuxiliaryCalendar();
            });
        }

        private void RefreshAuxiliaryCalendar()
        {
            if (AuxiliaryCalendarView == null || !this.IsLoaded) return;

            try
            {
                _auxCalendarStyler.Refresh(ViewModel.ActiveDates);

                if (ViewModel.IsReadingDay && ViewModel.ReadingCard != null)
                {
                    SyncAuxiliaryCalendarToDate(ViewModel.ReadingCard.Date);
                }
                else if (!_selectedAuxDate.HasValue && ViewModel.Groups.Count > 0 && ViewModel.Groups[0].Entries.Count > 0)
                {
                    var latestDate = ViewModel.Groups[0].Entries[0].Date.Date;
                    if (latestDate >= AuxiliaryCalendarView.MinDate.DateTime && latestDate <= AuxiliaryCalendarView.MaxDate.DateTime)
                    {
                        AuxiliaryCalendarView.SetDisplayDate(latestDate);
                    }
                }
            }
            catch { }
        }

        private void SyncAuxiliaryCalendarToDate(DateTime date)
        {
            if (AuxiliaryCalendarView == null || !this.IsLoaded) return;
            try
            {
                _isSyncingAuxCalendar = true;
                var target = date.Date;
                _selectedAuxDate = target;
                if (target >= AuxiliaryCalendarView.MinDate.DateTime && target <= AuxiliaryCalendarView.MaxDate.DateTime)
                {
                    AuxiliaryCalendarView.SetDisplayDate(target);
                    AuxiliaryCalendarView.SelectedDates.Clear();
                    AuxiliaryCalendarView.SelectedDates.Add(new DateTimeOffset(target));
                }

                if (ViewModel.ActiveDates.Contains(target))
                {
                    AuxDateStatusText.Text = $"{target:yyyy年MM月dd日} 已有日记";
                    AuxWriteButton.Content = "编辑此篇日记";
                    AuxWriteButton.Visibility = Visibility.Visible;
                }
                else
                {
                    AuxDateStatusText.Text = $"{target:yyyy年MM月dd日} 暂无日记";
                    AuxWriteButton.Content = "为这天新建日记";
                    AuxWriteButton.Visibility = Visibility.Visible;
                }
            }
            catch { }
            finally
            {
                _isSyncingAuxCalendar = false;
            }
        }

        private void AuxiliaryCalendarView_DayItemChanging(CalendarView sender, CalendarViewDayItemChangingEventArgs args)
        {
            _auxCalendarStyler.HandleDayItemChanging(args, ViewModel.ActiveDates);
        }

        private readonly Dictionary<string, FrameworkElement> _cardElementMap = new();

        private void DiaryCardRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
        {
            if (args.Element is FrameworkElement elem)
            {
                BindAndHighlightCardElement(elem);
            }
        }

        private void DiaryCardRepeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
        {
            if (args.Element is FrameworkElement elem)
            {
                UnregisterCardElement(elem);
            }
        }

        private void DiaryCard_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement elem)
            {
                BindAndHighlightCardElement(elem);
            }
        }

        private void DiaryCard_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement elem)
            {
                UnregisterCardElement(elem);
            }
        }

        private void BindAndHighlightCardElement(FrameworkElement elem)
        {
            try
            {
                var card = elem.Tag as DiaryCardItem;
                if (card == null)
                {
                    try { card = elem.DataContext as DiaryCardItem; } catch { }
                }

                if (card != null)
                {
                    _cardElementMap[card.Id] = elem;
                    string? kw = card.HighlightKeyword ?? ViewModel.SearchKeyword?.Trim();
                    if (elem.FindName("CardTitleText") is TextBlock titleTb)
                    {
                        AnalyticsPage.ApplyTextHighlight(titleTb, card.Title, kw);
                    }
                    if (elem.FindName("CardExcerptText") is TextBlock excerptTb)
                    {
                        AnalyticsPage.ApplyTextHighlight(excerptTb, card.Excerpt, kw);
                    }
                }
            }
            catch { }
        }

        private void UnregisterCardElement(FrameworkElement elem)
        {
            try
            {
                var card = elem.Tag as DiaryCardItem;
                if (card != null)
                {
                    _cardElementMap.Remove(card.Id);
                }
                else
                {
                    foreach (var kvp in _cardElementMap)
                    {
                        if (ReferenceEquals(kvp.Value, elem))
                        {
                            _cardElementMap.Remove(kvp.Key);
                            break;
                        }
                    }
                }
            }
            catch { }
        }

        private async void AuxiliaryCalendarView_SelectedDatesChanged(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
        {
            if (_isSyncingAuxCalendar) return;

            if (sender.SelectedDates.Count == 0)
            {
                _selectedAuxDate = null;
                AuxDateStatusText.Text = "点击日期查看日记";
                AuxWriteButton.Visibility = Visibility.Collapsed;
                return;
            }

            var selected = sender.SelectedDates[0];
            var date = selected.Date;
            _selectedAuxDate = date;

            if (ViewModel.IsReadingDay)
            {
                var card = ViewModel.Groups.SelectMany(g => g.Entries).FirstOrDefault(e => e.Date.Date == date);
                if (card != null)
                {
                    await ViewModel.OpenReadingDayAsync(card);
                    ApplyReadingSearchHighlight();
                }
            }
            else
            {
                HighlightDiaryCardForDate(date);
            }

            if (ViewModel.ActiveDates.Contains(date))
            {
                AuxDateStatusText.Text = ViewModel.IsReadingDay
                    ? $"{date:yyyy年MM月dd日} 已有日记"
                    : $"{date:yyyy年MM月dd日} 已有日记，左侧已定位高亮";
                AuxWriteButton.Content = "编辑此篇日记";
                AuxWriteButton.Visibility = Visibility.Visible;
            }
            else
            {
                AuxDateStatusText.Text = $"{date:yyyy年MM月dd日} 暂无日记";
                AuxWriteButton.Content = "为这天新建日记";
                AuxWriteButton.Visibility = Visibility.Visible;
            }
        }

        private System.Threading.CancellationTokenSource? _highlightCts;

        private async void HighlightDiaryCardForDate(DateTime targetDate)
        {
            _highlightCts?.Cancel();
            _highlightCts?.Dispose();
            _highlightCts = new System.Threading.CancellationTokenSource();
            var token = _highlightCts.Token;

            foreach (var group in ViewModel.Groups)
            {
                foreach (var entry in group.Entries)
                {
                    entry.IsHighlighted = false;
                }
            }

            DiaryCardItem? matchedCard = null;
            int matchedGroupIndex = -1;
            for (int gIdx = 0; gIdx < ViewModel.Groups.Count; gIdx++)
            {
                var group = ViewModel.Groups[gIdx];
                matchedCard = group.Entries.FirstOrDefault(e => e.Date.Date == targetDate.Date);
                if (matchedCard != null)
                {
                    matchedGroupIndex = gIdx;
                    break;
                }
            }

            if (matchedCard != null)
            {
                matchedCard.IsHighlighted = true;

                if (_cardElementMap.TryGetValue(matchedCard.Id, out var elem))
                {
                    try
                    {
                        elem.StartBringIntoView(new BringIntoViewOptions
                        {
                            AnimationDesired = true,
                            VerticalAlignmentRatio = 0.3
                        });
                    }
                    catch { }
                }
                else if (matchedGroupIndex >= 0 && GroupsRepeater != null)
                {
                    try
                    {
                        var groupElem = GroupsRepeater.GetOrCreateElement(matchedGroupIndex);
                        groupElem?.StartBringIntoView(new BringIntoViewOptions
                        {
                            AnimationDesired = true,
                            VerticalAlignmentRatio = 0.2
                        });
                    }
                    catch { }
                }

                try
                {
                    await Task.Delay(3500, token);
                    if (!token.IsCancellationRequested)
                    {
                        matchedCard.IsHighlighted = false;
                    }
                }
                catch (TaskCanceledException) { }
            }
        }

        private void AuxWriteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedAuxDate.HasValue)
            {
                ShellPage.Current?.NavigateToEditor(_selectedAuxDate.Value);
            }
        }

        #endregion
    }
}
