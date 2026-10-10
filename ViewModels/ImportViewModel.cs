using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Journal.Models;
using Journal.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Journal.ViewModels
{
    public enum ImportFilterMode
    {
        All = 0,
        ConflictOnly = 1,
        NewOnly = 2
    }

    public enum ImportPreviewTabMode
    {
        SideBySide = 0,
        MergedSimulation = 1
    }

    public partial class ImportViewModel : ObservableObject
    {
        private readonly ImportService _importService = ImportService.Instance;
        private readonly DiaryStorageService _storageService = DiaryStorageService.Instance;
        private readonly DiaryIndexService _indexService = DiaryIndexService.Instance;

        private readonly List<RawImportSource> _rawSources = new();

        public ObservableCollection<ImportedEntryPreview> AllEntries { get; } = new();
        public ObservableCollection<ImportedEntryPreview> FilteredEntries { get; } = new();
        public ObservableCollection<string> Warnings { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasEntries))]
        [NotifyPropertyChangedFor(nameof(CanExecuteImport))]
        private int _totalScannedCount;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasEntries))]
        [NotifyPropertyChangedFor(nameof(CanExecuteImport))]
        private int _validCount;

        [ObservableProperty]
        private int _conflictedCount;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanExecuteImport))]
        private int _selectedCount;

        [ObservableProperty]
        private int _totalWords;

        [ObservableProperty]
        private string _dateRangeText = "尚未载入文件";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanExecuteImport))]
        private bool _isScanning;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanExecuteImport))]
        private bool _isImporting;

        [ObservableProperty]
        private double _progressValue;

        [ObservableProperty]
        private string _statusMessage = "请拖拽日记文件或点击上方按钮进行解析";

        [ObservableProperty]
        private ImportFilterMode _filterMode = ImportFilterMode.All;

        [ObservableProperty]
        private ImportPreviewTabMode _previewTabMode = ImportPreviewTabMode.SideBySide;

        [ObservableProperty]
        private ImportConflictStrategy _conflictStrategy = ImportConflictStrategy.MergeAppend;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsCustomRegexVisible))]
        private ImportSplitRulePreset _selectedRulePreset = ImportSplitRulePreset.AutoDetection;

        [ObservableProperty]
        private string _customRegexPattern = string.Empty;

        public bool IsCustomRegexVisible => SelectedRulePreset == ImportSplitRulePreset.CustomRegex;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSelectedEntry))]
        [NotifyPropertyChangedFor(nameof(SelectedEntryHasConflict))]
        [NotifyPropertyChangedFor(nameof(SelectedEntryExistingWords))]
        [NotifyPropertyChangedFor(nameof(SelectedEntryIncomingWords))]
        [NotifyPropertyChangedFor(nameof(SelectedEntryMergedWords))]
        private ImportedEntryPreview? _selectedEntry;

        public bool HasEntries => ValidCount > 0;
        public bool HasSelectedEntry => SelectedEntry != null;
        public bool SelectedEntryHasConflict => SelectedEntry?.HasConflict ?? false;
        public int SelectedEntryExistingWords => SelectedEntry?.ExistingWordCount ?? 0;
        public int SelectedEntryIncomingWords => SelectedEntry?.WordCount ?? 0;
        public int SelectedEntryMergedWords => SelectedEntry != null 
            ? _storageService.CalculateWordCount(SelectedEntry.SimulatedMergedContent) 
            : 0;

        public bool CanExecuteImport => HasEntries && SelectedCount > 0 && !IsImporting && !IsScanning;

        public ImportViewModel()
        {
            _conflictStrategy = ImportConflictStrategy.MergeAppend;
        }

        partial void OnConflictStrategyChanged(ImportConflictStrategy value)
        {
            UpdateAllSimulatedMergedContent(value);
            OnPropertyChanged(nameof(SelectedEntryMergedWords));
        }

        partial void OnSelectedRulePresetChanged(ImportSplitRulePreset value)
        {
            OnPropertyChanged(nameof(IsCustomRegexVisible));
            if (_rawSources.Count > 0)
            {
                _ = ReapplyRuleAsync();
            }
        }

        partial void OnFilterModeChanged(ImportFilterMode value)
        {
            ApplyFilter();
        }

        partial void OnPreviewTabModeChanged(ImportPreviewTabMode value)
        {
            // 切换预览模式 Tab 时刷新选中条目相关 UI
            OnPropertyChanged(nameof(SelectedEntry));
        }

        partial void OnSelectedEntryChanged(ImportedEntryPreview? value)
        {
            OnPropertyChanged(nameof(HasSelectedEntry));
            OnPropertyChanged(nameof(SelectedEntryHasConflict));
            OnPropertyChanged(nameof(SelectedEntryExistingWords));
            OnPropertyChanged(nameof(SelectedEntryIncomingWords));
            OnPropertyChanged(nameof(SelectedEntryMergedWords));
        }

        public void ApplyFilter()
        {
            FilteredEntries.Clear();
            IEnumerable<ImportedEntryPreview> source = AllEntries;

            switch (FilterMode)
            {
                case ImportFilterMode.ConflictOnly:
                    source = source.Where(e => e.HasConflict);
                    break;
                case ImportFilterMode.NewOnly:
                    source = source.Where(e => !e.HasConflict);
                    break;
                case ImportFilterMode.All:
                default:
                    break;
            }

            foreach (var item in source)
            {
                FilteredEntries.Add(item);
            }

            if (SelectedEntry == null || !FilteredEntries.Contains(SelectedEntry))
            {
                SelectedEntry = FilteredEntries.FirstOrDefault();
            }
        }

        private void UpdateAllSimulatedMergedContent(ImportConflictStrategy strategy)
        {
            foreach (var entry in AllEntries)
            {
                if (entry.HasConflict)
                {
                    entry.SimulatedMergedContent = ImportService.GenerateSimulatedMergedContent(
                        entry.ExistingContent,
                        entry.Content,
                        strategy,
                        entry.TargetDate);
                }
                else
                {
                    entry.SimulatedMergedContent = entry.Content;
                }
            }
        }

        [RelayCommand]
        public async Task PickFilesAsync()
        {
            if (IsImporting || IsScanning) return;

            try
            {
                var hwnd = MainWindow.GetActiveWindowHandle();
                if (hwnd == IntPtr.Zero) return;

                var picker = new FileOpenPicker();
                InitializeWithWindow.Initialize(picker, hwnd);

                picker.ViewMode = PickerViewMode.List;
                picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                picker.FileTypeFilter.Add(".md");
                picker.FileTypeFilter.Add(".txt");
                picker.FileTypeFilter.Add(".markdown");
                picker.FileTypeFilter.Add(".json");
                picker.FileTypeFilter.Add(".zip");

                var files = await picker.PickMultipleFilesAsync();
                if (files != null && files.Count > 0)
                {
                    var paths = files.Select(f => f.Path).ToList();
                    await ProcessPathsAsync(paths);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"选取文件失败: {ex.Message}";
            }
        }

        [RelayCommand]
        public async Task PickFolderAsync()
        {
            if (IsImporting || IsScanning) return;

            try
            {
                var hwnd = MainWindow.GetActiveWindowHandle();
                if (hwnd == IntPtr.Zero) return;

                var picker = new FolderPicker();
                InitializeWithWindow.Initialize(picker, hwnd);

                picker.ViewMode = PickerViewMode.List;
                picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                picker.FileTypeFilter.Add("*");

                var folder = await picker.PickSingleFolderAsync();
                if (folder != null)
                {
                    await ProcessPathsAsync(new[] { folder.Path });
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"选取文件夹失败: {ex.Message}";
            }
        }

        public async Task ProcessPathsAsync(IEnumerable<string> rawPaths)
        {
            if (IsImporting || IsScanning || rawPaths == null || !rawPaths.Any()) return;

            IsScanning = true;
            ProgressValue = 0;
            StatusMessage = "正在深层遍历与分析日记文件...";

            try
            {
                var allFilePaths = new List<string>();
                var supportedExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    ".md", ".txt", ".markdown", ".json", ".zip"
                };

                var enumOptions = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true
                };

                foreach (var p in rawPaths)
                {
                    if (File.Exists(p))
                    {
                        if (supportedExts.Contains(Path.GetExtension(p)))
                        {
                            allFilePaths.Add(p);
                        }
                    }
                    else if (Directory.Exists(p))
                    {
                        var dirFiles = Directory.EnumerateFiles(p, "*.*", enumOptions)
                            .Where(f => supportedExts.Contains(Path.GetExtension(f)));
                        allFilePaths.AddRange(dirFiles);
                    }
                }

                if (allFilePaths.Count == 0)
                {
                    StatusMessage = "未发现支持的日记文件 (支持 .md, .txt, .markdown, .json, .zip)";
                    IsScanning = false;
                    return;
                }

                StatusMessage = $"正在将 {allFilePaths.Count} 个文件载入内存池并应用规则识别...";
                var warnings = new List<string>();
                var loaded = await _importService.LoadRawSourcesAsync(allFilePaths, warnings);

                _rawSources.Clear();
                _rawSources.AddRange(loaded);

                Warnings.Clear();
                foreach (var w in warnings)
                {
                    Warnings.Add(w);
                }

                await ApplyCurrentRuleToSourcesAsync();
            }
            catch (Exception ex)
            {
                StatusMessage = $"解析异常: {ex.Message}";
            }
            finally
            {
                IsScanning = false;
            }
        }

        [RelayCommand]
        public async Task ReapplyRuleAsync()
        {
            if (IsImporting || IsScanning || _rawSources.Count == 0) return;

            IsScanning = true;
            StatusMessage = "正在根据当前规则重新识别日记条目...";
            try
            {
                await ApplyCurrentRuleToSourcesAsync();
            }
            finally
            {
                IsScanning = false;
            }
        }

        private async Task ApplyCurrentRuleToSourcesAsync()
        {
            var rule = new CustomSplitRule
            {
                Preset = SelectedRulePreset,
                CustomRegexPattern = CustomRegexPattern,
                MergeSameDay = true
            };

            var summary = await _importService.ProcessSourcesWithRuleAsync(_rawSources, rule);

            // 解除旧条目事件监听
            foreach (var old in AllEntries)
            {
                old.SelectionChanged -= OnEntrySelectionChanged;
                old.DateChanged -= OnEntryDateChanged;
                old.ContentChanged -= OnEntryContentChanged;
            }

            AllEntries.Clear();

            foreach (var warn in summary.Warnings)
            {
                if (!Warnings.Contains(warn))
                {
                    Warnings.Add(warn);
                }
            }

            foreach (var entry in summary.Entries)
            {
                entry.SelectionChanged += OnEntrySelectionChanged;
                entry.DateChanged += OnEntryDateChanged;
                entry.ContentChanged += OnEntryContentChanged;
                AllEntries.Add(entry);
            }

            TotalScannedCount = summary.TotalFilesScanned;
            ValidCount = summary.ValidCount;
            ConflictedCount = summary.ConflictedCount;
            TotalWords = summary.TotalWords;
            DateRangeText = summary.DateRangeText;

            UpdateSelectedCount();
            UpdateAllSimulatedMergedContent(ConflictStrategy);
            ApplyFilter();

            StatusMessage = $"已应用规则识别：共生成 {ValidCount} 篇候选日记（{ConflictedCount} 篇与本地记录重合）";
        }

        private void OnEntrySelectionChanged()
        {
            UpdateSelectedCount();
        }

        private void OnEntryContentChanged(ImportedEntryPreview entry)
        {
            if (entry == null) return;

            entry.WordCount = _storageService.CalculateWordCount(entry.Content);
            entry.TimestampsCount = _storageService.CountTimestamps(entry.Content);
            entry.Excerpt = _storageService.ExtractExcerpt(entry.Content, 120);

            if (entry.HasConflict)
            {
                entry.SimulatedMergedContent = ImportService.GenerateSimulatedMergedContent(
                    entry.ExistingContent,
                    entry.Content,
                    ConflictStrategy,
                    entry.TargetDate);
            }
            else
            {
                entry.SimulatedMergedContent = entry.Content;
            }

            TotalWords = AllEntries.Sum(e => e.WordCount);

            if (SelectedEntry == entry)
            {
                OnPropertyChanged(nameof(SelectedEntryIncomingWords));
                OnPropertyChanged(nameof(SelectedEntryMergedWords));
            }
        }

        private async void OnEntryDateChanged(ImportedEntryPreview entry)
        {
            if (entry == null) return;

            try
            {
                // 针对当前被修改的条目重新判定本地冲突状态
                bool exists = _storageService.DiaryExists(entry.TargetDate);
                entry.HasConflict = exists;
                if (exists)
                {
                    entry.ExistingContent = await _storageService.LoadDiaryContentAsync(entry.TargetDate);
                    entry.ExistingWordCount = _storageService.CalculateWordCount(entry.ExistingContent);
                    entry.ExistingTimestampsCount = _storageService.CountTimestamps(entry.ExistingContent);
                    entry.SimulatedMergedContent = ImportService.GenerateSimulatedMergedContent(
                        entry.ExistingContent,
                        entry.Content,
                        ConflictStrategy,
                        entry.TargetDate);
                }
                else
                {
                    entry.ExistingContent = string.Empty;
                    entry.ExistingWordCount = 0;
                    entry.ExistingTimestampsCount = 0;
                    entry.SimulatedMergedContent = entry.Content;
                }

                // 更新冲突数统计与筛选
                ConflictedCount = AllEntries.Count(e => e.HasConflict);
                ApplyFilter();

                if (SelectedEntry == entry)
                {
                    OnPropertyChanged(nameof(SelectedEntryExistingWords));
                    OnPropertyChanged(nameof(SelectedEntryMergedWords));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ImportViewModel] OnEntryDateChanged 发生异常: {ex.Message}");
            }
        }

        private void UpdateSelectedCount()
        {
            SelectedCount = AllEntries.Count(e => e.IsSelected);
            OnPropertyChanged(nameof(CanExecuteImport));
        }

        [RelayCommand]
        public void SelectAll()
        {
            if (IsImporting) return;
            foreach (var item in FilteredEntries)
            {
                item.IsSelected = true;
            }
            UpdateSelectedCount();
        }

        [RelayCommand]
        public void DeselectAll()
        {
            if (IsImporting) return;
            foreach (var item in FilteredEntries)
            {
                item.IsSelected = false;
            }
            UpdateSelectedCount();
        }

        [RelayCommand]
        public void InvertSelection()
        {
            if (IsImporting) return;
            foreach (var item in FilteredEntries)
            {
                item.IsSelected = !item.IsSelected;
            }
            UpdateSelectedCount();
        }

        [RelayCommand]
        public void RemoveEntry(ImportedEntryPreview? entry)
        {
            if (entry == null || IsImporting) return;

            entry.SelectionChanged -= OnEntrySelectionChanged;
            entry.DateChanged -= OnEntryDateChanged;
            entry.ContentChanged -= OnEntryContentChanged;

            AllEntries.Remove(entry);
            FilteredEntries.Remove(entry);

            if (SelectedEntry == entry)
            {
                SelectedEntry = FilteredEntries.FirstOrDefault();
            }

            ValidCount = AllEntries.Count;
            ConflictedCount = AllEntries.Count(e => e.HasConflict);
            TotalWords = AllEntries.Sum(e => e.WordCount);
            UpdateSelectedCount();

            StatusMessage = $"已从导入候选中移除 {entry.DateDisplay} 条目";
        }

        [RelayCommand]
        public void RemoveSelected()
        {
            if (IsImporting) return;

            var toRemove = FilteredEntries.Where(e => e.IsSelected).ToList();
            if (toRemove.Count == 0) return;

            foreach (var entry in toRemove)
            {
                entry.SelectionChanged -= OnEntrySelectionChanged;
                entry.DateChanged -= OnEntryDateChanged;
                entry.ContentChanged -= OnEntryContentChanged;

                AllEntries.Remove(entry);
                FilteredEntries.Remove(entry);
            }

            if (SelectedEntry != null && !FilteredEntries.Contains(SelectedEntry))
            {
                SelectedEntry = FilteredEntries.FirstOrDefault();
            }

            ValidCount = AllEntries.Count;
            ConflictedCount = AllEntries.Count(e => e.HasConflict);
            TotalWords = AllEntries.Sum(e => e.WordCount);
            UpdateSelectedCount();

            StatusMessage = $"已批量移除 {toRemove.Count} 篇已选条目";
        }

        [RelayCommand]
        public async Task ImportSingleEntryAsync(ImportedEntryPreview? entry)
        {
            if (entry == null || IsImporting) return;

            IsImporting = true;
            StatusMessage = $"正在单独导入 {entry.DateDisplay} ...";

            try
            {
                bool success = await _importService.ExecuteImportSingleAsync(entry, ConflictStrategy);
                if (success)
                {
                    entry.IsSelected = false;
                    UpdateSelectedCount();
                    StatusMessage = $"成功导入 {entry.DateDisplay} 日记！已写入本地并同步索引";
                    ConflictedCount = AllEntries.Count(e => e.HasConflict);
                    ApplyFilter();
                    OnPropertyChanged(nameof(SelectedEntryExistingWords));
                    OnPropertyChanged(nameof(SelectedEntryMergedWords));
                }
                else
                {
                    StatusMessage = $"导入 {entry.DateDisplay} 失败";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"单篇导入异常: {ex.Message}";
            }
            finally
            {
                IsImporting = false;
            }
        }

        [RelayCommand]
        public void Clear()
        {
            if (IsImporting || IsScanning) return;
            foreach (var entry in AllEntries)
            {
                entry.SelectionChanged -= OnEntrySelectionChanged;
                entry.DateChanged -= OnEntryDateChanged;
                entry.ContentChanged -= OnEntryContentChanged;
            }

            _rawSources.Clear();
            AllEntries.Clear();
            FilteredEntries.Clear();
            Warnings.Clear();
            SelectedEntry = null;

            TotalScannedCount = 0;
            ValidCount = 0;
            ConflictedCount = 0;
            SelectedCount = 0;
            TotalWords = 0;
            DateRangeText = "尚未载入文件";
            StatusMessage = "已清空候选列表，请重新拖拽或选择文件";
            ProgressValue = 0;
        }

        [RelayCommand]
        public async Task ExecuteImportAsync()
        {
            if (!CanExecuteImport) return;

            IsImporting = true;
            ProgressValue = 0;
            StatusMessage = "开始导入日记并同步索引...";

            try
            {
                var summary = new ImportPreviewSummary
                {
                    TotalFilesScanned = TotalScannedCount,
                    Entries = AllEntries.Where(e => e.IsSelected).ToList()
                };

                var progress = new Progress<(int Current, int Total, string Status)>(report =>
                {
                    ProgressValue = report.Total > 0 ? (double)report.Current / report.Total * 100 : 0;
                    StatusMessage = $"[{report.Current}/{report.Total}] {report.Status}";
                });

                var result = await _importService.ExecuteImportAsync(summary, ConflictStrategy, progress);

                StatusMessage = $"导入完成: 成功写入 {result.SuccessCount} 篇 (合并追加 {result.MergedCount} 篇, 覆盖 {result.OverwrittenCount} 篇, 跳过 {result.SkippedCount} 篇)";

                // 触发全局数据刷新，更新时间线、日历与统计大盘
                _indexService.NotifyDataChanged();

                // 刷新已有条目的冲突状态
                foreach (var entry in AllEntries)
                {
                    if (_storageService.DiaryExists(entry.TargetDate))
                    {
                        entry.HasConflict = true;
                        entry.ExistingContent = await _storageService.LoadDiaryContentAsync(entry.TargetDate);
                        entry.ExistingWordCount = _storageService.CalculateWordCount(entry.ExistingContent);
                        entry.ExistingTimestampsCount = _storageService.CountTimestamps(entry.ExistingContent);
                        entry.SimulatedMergedContent = ImportService.GenerateSimulatedMergedContent(
                            entry.ExistingContent,
                            entry.Content,
                            ConflictStrategy,
                            entry.TargetDate);
                    }
                }

                ConflictedCount = AllEntries.Count(e => e.HasConflict);
                ApplyFilter();
            }
            catch (Exception ex)
            {
                StatusMessage = $"导入失败: {ex.Message}";
            }
            finally
            {
                IsImporting = false;
            }
        }
    }
}
