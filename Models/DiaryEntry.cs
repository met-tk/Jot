using System;
using SQLite;

namespace Journal.Models
{
    /// <summary>
    /// 日记元数据索引实体，用于 SQLite 极速检索与大跨度聚合统计
    /// </summary>
    [Table("DiaryEntries")]
    public class DiaryEntry
    {
        [PrimaryKey]
        public string Id { get; set; } = string.Empty; // 格式: "yyyy-MM-dd"

        [Indexed]
        public DateTime Date { get; set; }

        public string Title { get; set; } = string.Empty;

        public string RelativePath { get; set; } = string.Empty;

        public string FullPath { get; set; } = string.Empty;

        public int WordCount { get; set; }

        public int TimestampsCount { get; set; }

        public string Excerpt { get; set; } = string.Empty;

        public bool HasExcerpt { get; set; }

        public string? CustomTitle { get; set; }

        public string FirstSentence { get; set; } = string.Empty;

        public DateTime LastModified { get; set; }
    }
}
