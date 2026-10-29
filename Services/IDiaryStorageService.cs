using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Journal.Services
{
    /// <summary>
    /// 回收站日记条目元数据
    /// </summary>
    public record TrashDiaryItem(
        string FilePath,
        string FileName,
        DateTime DiaryDate,
        DateTime DeletedAt,
        long SizeBytes,
        string PreviewTitle);

    /// <summary>
    /// 本地日记文件存储、备份与回收站生命周期管理契约
    /// </summary>
    public interface IDiaryStorageService
    {
        string BaseDirectory { get; set; }
        string BackupDirectory { get; }
        string TrashDirectory { get; }

        string GetDiaryPath(DateTime date, bool ensureDirectory = false);
        bool DiaryExists(DateTime date);
        void MoveFileToTrash(string filePath, DateTime? date = null);
        void DeleteDiaryFile(DateTime date);
        Task<string> LoadDiaryContentAsync(DateTime date);
        Task<string> ReadDiaryAsync(DateTime date);
        Task SaveDiaryContentAsync(DateTime date, string content);
        void CreateBackupSnapshot(DateTime date, string content);
        void PruneBackups(int maxTotalBackups = 150, int maxPerDayBackups = 5);
        void CleanupEmptyDirectories();

        IReadOnlyList<TrashDiaryItem> GetTrashItems();
        Task<bool> RestoreFromTrashAsync(string trashFilePath);
        int EmptyTrash();
        int PruneExpiredTrash(int retentionDays = 30);

        string GenerateTimestamp(bool isFirstForDay, DateTime? targetDate = null);
        bool HasExistingTimestamp(string content);
        int CountTimestamps(string content);
        int CalculateWordCount(string content);
        string ExtractExcerpt(string content, int maxLength = 100);
        DiarySummary ExtractSummary(string content, int maxExcerptLength = 120);
    }
}
