using System;
using System.Collections.Generic;
using System.Linq;

namespace Journal.Models
{
    /// <summary>
    /// 导入日记冲突处理策略
    /// </summary>
    public enum ImportConflictStrategy
    {
        /// <summary>
        /// 智能合并追加（推荐）：保留已有日记内容，在末尾附加上导入的新段落与时间戳
        /// </summary>
        MergeAppend,

        /// <summary>
        /// 覆盖已有日记：彻底替换为导入的内容
        /// </summary>
        Overwrite,

        /// <summary>
        /// 跳过已有日期：若已有该日期的日记，则忽略跳过
        /// </summary>
        Skip
    }

    /// <summary>
    /// 日期识别来源渠道
    /// </summary>
    public enum DateDetectionSource
    {
        FileName,       // 文件名 (yyyy-MM-dd / yyyyMMdd / yyyy年MM月dd日 等)
        Frontmatter,    // YAML 标头 (date: ...)
        Header,         // 正文首行大标题 (# yyyy-MM-dd)
        Timestamp,      // 正文时间戳标记 ([yyyy-MM-dd HH:mm:ss])
        FileMetadata,   // 文件系统创建/修改日期
        DefaultFallback // 默认回退
    }

    /// <summary>
    /// 日记导入拆分规则预设
    /// </summary>
    public enum ImportSplitRulePreset
    {
        /// <summary>
        /// 智能探测 (推荐)：包含多日锚点时自动切分，否则整篇导入
        /// </summary>
        AutoDetection = 0,

        /// <summary>
        /// 不拆分：将每个文本文件作为整篇单日日记导入
        /// </summary>
        NoSplit = 1,

        /// <summary>
        /// 按 Markdown 标题拆分：如 # 2026-10-01、## 2026年10月1日
        /// </summary>
        MarkdownHeaders = 2,

        /// <summary>
        /// 按完整时间戳拆分：如 [2026-10-01 08:30:00] 或 [2026-10-01]
        /// </summary>
        BracketTimestamps = 3,

        /// <summary>
        /// 按分割线拆分：如 --- 2026-10-01 --- 或 === 2026.10.01 ===
        /// </summary>
        Dividers = 4,

        /// <summary>
        /// 按独占纯日期行拆分：如独立一行的 2026-10-01 或 2026年10月1日
        /// </summary>
        StandaloneDates = 5,

        /// <summary>
        /// 自定义正则表达式匹配
        /// </summary>
        CustomRegex = 6
    }

    /// <summary>
    /// 自定义拆分规则配置
    /// </summary>
    public class CustomSplitRule
    {
        public ImportSplitRulePreset Preset { get; set; } = ImportSplitRulePreset.AutoDetection;
        public string CustomRegexPattern { get; set; } = string.Empty;
        public bool MergeSameDay { get; set; } = true;
    }

    /// <summary>
    /// 原始文件文本源模型 (内存缓存池)
    /// </summary>
    public class RawImportSource
    {
        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string RawContent { get; set; } = string.Empty;
        public DateTime? FileTime { get; set; }
    }

    /// <summary>
    /// 待导入单篇日记预览条目
    /// </summary>
    public partial class ImportedEntryPreview : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        public string SourceName { get; set; } = string.Empty;

        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
        [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(DateDisplay))]
        [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(WeekDayDisplay))]
        [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(DateOffset))]
        private DateTime _targetDate;

        public DateTimeOffset DateOffset
        {
            get => new DateTimeOffset(TargetDate);
            set => TargetDate = value.DateTime;
        }

        public DateDetectionSource DetectionSource { get; set; }

        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
        private string _content = string.Empty;

        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
        private int _wordCount;

        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
        private int _timestampsCount;

        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
        private string _excerpt = string.Empty;

        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
        [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(ConflictDisplayText))]
        private bool _hasConflict;

        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
        [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(ConflictDisplayText))]
        private int _existingWordCount;

        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
        private int _existingTimestampsCount;

        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
        private string _existingContent = string.Empty;

        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
        private string _simulatedMergedContent = string.Empty;

        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
        private bool _isSelected = true;

        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
        [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(ConflictDisplayText))]
        private bool _isImported;

        public string DateDisplay => TargetDate.ToString("yyyy年MM月dd日");
        public string WeekDayDisplay => TargetDate.ToString("dddd");

        public string ConflictDisplayText
        {
            get
            {
                if (IsImported) return "已入库";
                return HasConflict ? $"已存在 ({ExistingWordCount}字)" : "新记录";
            }
        }

        public event Action? SelectionChanged;
        public event Action<ImportedEntryPreview>? DateChanged;
        public event Action<ImportedEntryPreview>? ContentChanged;

        partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();
        partial void OnTargetDateChanged(DateTime value) => DateChanged?.Invoke(this);
        partial void OnContentChanged(string value) => ContentChanged?.Invoke(this);
    }

    /// <summary>
    /// 导入预检分析摘要
    /// </summary>
    public class ImportPreviewSummary
    {
        public int TotalFilesScanned { get; set; }
        public List<ImportedEntryPreview> Entries { get; set; } = new();
        public int ValidCount => Entries.Count;
        public int ConflictedCount => Entries.Count(e => e.HasConflict);
        public int TotalWords => Entries.Sum(e => e.WordCount);
        public DateTime? MinDate => Entries.Count > 0 ? Entries.Min(e => e.TargetDate) : null;
        public DateTime? MaxDate => Entries.Count > 0 ? Entries.Max(e => e.TargetDate) : null;
        public List<string> Warnings { get; set; } = new();

        public string DateRangeText
        {
            get
            {
                if (MinDate.HasValue && MaxDate.HasValue)
                {
                    if (MinDate.Value.Date == MaxDate.Value.Date)
                        return MinDate.Value.ToString("yyyy-MM-dd");
                    return $"{MinDate.Value:yyyy-MM-dd} 至 {MaxDate.Value:yyyy-MM-dd}";
                }
                return "无有效日期";
            }
        }
    }

    /// <summary>
    /// 导入执行最终结果
    /// </summary>
    public class ImportExecutionResult
    {
        public bool Success { get; set; }
        public int ImportedCount { get; set; }
        public int MergedCount { get; set; }
        public int OverwrittenCount { get; set; }
        public int SkippedCount { get; set; }
        public int FailedCount { get; set; }
        public int TotalWordsAdded { get; set; }
        public string? ErrorMessage { get; set; }

        public int SuccessCount => ImportedCount + MergedCount + OverwrittenCount;
    }
}
