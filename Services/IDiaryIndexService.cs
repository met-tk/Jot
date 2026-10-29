using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Journal.Models;

namespace Journal.Services
{
    public interface IDiaryIndexService
    {
        event Action? DataChanged;

        void SetDispatcherQueue(Microsoft.UI.Dispatching.DispatcherQueue dq);
        void NotifyDataChanged();
        void RegisterPendingSave(Task saveTask);
        Task WaitForPendingSaveAsync();

        Task CloseConnectionAsync();
        Task InitializeAsync();
        Task SyncDiaryAsync(DateTime date, string content, string fullPath);
        Task SyncDiariesBatchAsync(IEnumerable<(DateTime Date, string Content, string FullPath)> items);
        Task FullScanAndSyncAsync();

        Task<DiaryEntry?> GetEntryAsync(DateTime date);
        Task DeleteEntryAsync(DateTime date);
        Task DeleteEntriesAsync(IEnumerable<DateTime> dates);
        Task<List<DiaryEntry>> GetAllEntriesAsync();
        Task<List<DiaryEntry>> FilterEntriesByQueryAsync(IReadOnlyList<DiaryEntry> entries, string query, CancellationToken cancellationToken = default);
        Task<AnalyticsStats> CalculateAnalyticsAsync(IReadOnlyList<DiaryEntry>? preloadedEntries = null);
    }
}
