using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using Journal.Models;

namespace Journal.Services
{
    /// <summary>
    /// 导出格式枚举（已移除冗余 PDF / ZIP，支持 Markdown 批量、TXT 批量与 JSON 结构化导出）
    /// </summary>
    public enum ExportFormat
    {
        Markdown,
        Txt,
        Json
    }

    /// <summary>
    /// JSON 结构化导出单篇日记数据契约（与 ImportService 100% 往返无损兼容）
    /// </summary>
    public record ExportedDiaryJsonItem(
        string Date,
        string Title,
        int WordCount,
        int TimestampsCount,
        string Content,
        DateTime LastModified
    );

    /// <summary>
    /// JSON 结构化导出根包数据契约
    /// </summary>
    public record ExportedDiaryJsonPackage(
        string ExportedAt,
        int EntryCount,
        int TotalWords,
        List<ExportedDiaryJsonItem> Entries
    );

    /// <summary>
    /// 导出执行结果
    /// </summary>
    public record ExportResult(
        bool Success,
        int EntryCount,
        int TotalWords,
        string OutputPath,
        string? ErrorMessage = null
    );

    /// <summary>
    /// 日记批量与单篇无损导出引擎（支持 Markdown / TXT 批量多文件导出及 JSON 完整结构化导出）
    /// </summary>
    public class ExportService
    {
        private static ExportService? _instance;
        public static ExportService Instance => _instance ??= new ExportService();

        private static readonly JsonSerializerOptions JsonExportOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>
        /// 基于勾选的日记列表执行批量或单篇导出
        /// - 当格式为 Markdown / Txt 且 destinationPath 为文件夹路径时：按日批量生成多个独立的 {yyyy-MM-dd}.md / {yyyy-MM-dd}.txt 文件；
        /// - 当格式为 Markdown / Txt 且 destinationPath 为单文件路径时：生成纯净无冗余包装的可导入文本文件；
        /// - 当格式为 Json 时：生成包含完整日记正文 Content 与元数据的标准 JSON 文件。
        /// </summary>
        public async Task<ExportResult> ExportEntriesAsync(
            IEnumerable<DiaryEntry> entries,
            ExportFormat format,
            string destinationPath,
            string rangeDescription = "")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(destinationPath))
                {
                    return new ExportResult(false, 0, 0, string.Empty, "未指定导出目标路径");
                }

                var entryList = entries.OrderBy(e => e.Date).ToList();
                if (entryList.Count == 0)
                {
                    return new ExportResult(false, 0, 0, destinationPath, "未选中任何有效的日记篇目");
                }

                int totalWords = entryList.Sum(e => e.WordCount);

                switch (format)
                {
                    case ExportFormat.Markdown:
                        if (IsDirectoryTarget(destinationPath, ".md"))
                        {
                            await ExportToDirectoryAsync(entryList, destinationPath, ".md");
                        }
                        else
                        {
                            EnsureParentDirectoryExists(destinationPath);
                            await ExportToMarkdownFileAsync(entryList, destinationPath);
                        }
                        break;

                    case ExportFormat.Txt:
                        if (IsDirectoryTarget(destinationPath, ".txt"))
                        {
                            await ExportToDirectoryAsync(entryList, destinationPath, ".txt");
                        }
                        else
                        {
                            EnsureParentDirectoryExists(destinationPath);
                            await ExportToTxtFileAsync(entryList, destinationPath);
                        }
                        break;

                    case ExportFormat.Json:
                        EnsureParentDirectoryExists(destinationPath);
                        await ExportToJsonFileAsync(entryList, destinationPath);
                        break;

                    default:
                        throw new NotSupportedException($"不支持的导出格式: {format}");
                }

                return new ExportResult(true, entryList.Count, totalWords, destinationPath, null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExportEntriesAsync failed: {ex}");
                return new ExportResult(false, 0, 0, destinationPath, ex.Message);
            }
        }

        private static bool IsDirectoryTarget(string path, string expectedFileExtension)
        {
            if (Directory.Exists(path)) return true;
            string ext = Path.GetExtension(path);
            return !string.Equals(ext, expectedFileExtension, StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureParentDirectoryExists(string filePath)
        {
            string? targetDir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }
        }

        #region Markdown (.md) 与 TXT (.txt) 批量多文件及纯净单文件导出

        /// <summary>
        /// 按日批量导出多个独立的 {yyyy-MM-dd}.md 或 {yyyy-MM-dd}.txt 文件至指定文件夹。
        /// 每个文件内容保持 100% 纯净原始日记正文（保留内部 [HH:mm:ss] 时间戳，无冗余包装），
        /// 可直接通过导入模块的“选择文件夹”或“选择文件”100% 零污染无损导入还原。
        /// </summary>
        public async Task<string> ExportToDirectoryAsync(
            IEnumerable<DiaryEntry> entries,
            string destinationFolderPath,
            string fileExtension = ".md")
        {
            if (!Directory.Exists(destinationFolderPath))
            {
                Directory.CreateDirectory(destinationFolderPath);
            }

            string normalizedExt = fileExtension.StartsWith('.') ? fileExtension.ToLowerInvariant() : "." + fileExtension.ToLowerInvariant();
            var entriesList = entries.OrderBy(e => e.Date).ToList();

            foreach (var entry in entriesList)
            {
                string rawContent = await LoadEntryContentAsync(entry);
                string cleanContent = (rawContent ?? string.Empty).Trim();
                string fileName = $"{entry.Date:yyyy-MM-dd}{normalizedExt}";
                string fullFilePath = Path.Combine(destinationFolderPath, fileName);
                await File.WriteAllTextAsync(fullFilePath, cleanContent, Encoding.UTF8);
            }

            return destinationFolderPath;
        }

        /// <summary>
        /// 导出为单一纯净 Markdown 文件（兼容 ImportService 的 # yyyy-MM-dd 标题切分规则）
        /// </summary>
        public async Task<string> ExportToMarkdownFileAsync(
            IEnumerable<DiaryEntry> entries,
            string destinationFilePath,
            string title = "",
            string rangeDescription = "")
        {
            string markdownContent = await BuildCombinedMarkdownContentAsync(entries, title, rangeDescription);
            EnsureParentDirectoryExists(destinationFilePath);
            await File.WriteAllTextAsync(destinationFilePath, markdownContent, Encoding.UTF8);
            return destinationFilePath;
        }

        /// <summary>
        /// 导出为单一纯净 TXT 文件（兼容 ImportService 的 # yyyy-MM-dd 标题切分规则）
        /// </summary>
        public async Task<string> ExportToTxtFileAsync(
            IEnumerable<DiaryEntry> entries,
            string destinationFilePath)
        {
            string txtContent = await BuildCombinedMarkdownContentAsync(entries);
            EnsureParentDirectoryExists(destinationFilePath);
            await File.WriteAllTextAsync(destinationFilePath, txtContent, Encoding.UTF8);
            return destinationFilePath;
        }

        /// <summary>
        /// 构建纯净、无冗余元数据的合并文本：
        /// - 若仅 1 篇日记：直接输出 `# yyyy-MM-dd\n{正文}`；
        /// - 若多篇日记：每篇以独占一行的 `# yyyy-MM-dd` 作为标准日期锚点，后跟纯净原始正文，
        ///   绝不插入 HTML 锚点、封面表格、目录索引或引用块，确保 ImportService 100% 识别。
        /// </summary>
        public async Task<string> BuildCombinedMarkdownContentAsync(
            IEnumerable<DiaryEntry> entries,
            string title = "",
            string rangeDescription = "",
            bool includeToc = false)
        {
            var entriesList = entries.OrderBy(e => e.Date).ToList();
            var sb = new StringBuilder();

            for (int i = 0; i < entriesList.Count; i++)
            {
                var entry = entriesList[i];
                string content = (await LoadEntryContentAsync(entry)).Trim();

                sb.AppendLine($"# {entry.Date:yyyy-MM-dd}");
                if (!string.IsNullOrWhiteSpace(content))
                {
                    sb.AppendLine(content);
                }

                if (i < entriesList.Count - 1)
                {
                    sb.AppendLine();
                }
            }

            return sb.ToString().TrimEnd();
        }

        #endregion

        #region JSON 结构化无损导出

        /// <summary>
        /// 导出为包含完整正文 Content 与标准 yyyy-MM-dd 日期的结构化 JSON 文件，
        /// 中文字符不转义，且与 ImportService.ParseJsonStreamAsync 100% 往返无损兼容。
        /// </summary>
        public async Task<string> ExportToJsonFileAsync(IEnumerable<DiaryEntry> entries, string destinationJsonPath)
        {
            var entriesList = entries.OrderBy(e => e.Date).ToList();
            var exportedItems = new List<ExportedDiaryJsonItem>(entriesList.Count);
            int totalWords = 0;

            foreach (var entry in entriesList)
            {
                string rawContent = await LoadEntryContentAsync(entry);
                string cleanContent = (rawContent ?? string.Empty).Trim();
                int wordCount = entry.WordCount > 0
                    ? entry.WordCount
                    : DiaryStorageService.Instance.CalculateWordCount(cleanContent);
                int timestampsCount = entry.TimestampsCount > 0
                    ? entry.TimestampsCount
                    : DiaryStorageService.Instance.CountTimestamps(cleanContent);
                string resolvedTitle = !string.IsNullOrWhiteSpace(entry.Title)
                    ? entry.Title
                    : (!string.IsNullOrWhiteSpace(entry.CustomTitle) ? entry.CustomTitle : entry.FirstSentence);

                totalWords += wordCount;

                exportedItems.Add(new ExportedDiaryJsonItem(
                    Date: entry.Date.ToString("yyyy-MM-dd"),
                    Title: resolvedTitle ?? string.Empty,
                    WordCount: wordCount,
                    TimestampsCount: timestampsCount,
                    Content: cleanContent,
                    LastModified: entry.LastModified != default ? entry.LastModified : DateTime.Now
                ));
            }

            var package = new ExportedDiaryJsonPackage(
                ExportedAt: DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                EntryCount: exportedItems.Count,
                TotalWords: totalWords,
                Entries: exportedItems
            );

            EnsureParentDirectoryExists(destinationJsonPath);
            string json = JsonSerializer.Serialize(package, JsonExportOptions);
            await File.WriteAllTextAsync(destinationJsonPath, json, Encoding.UTF8);
            return destinationJsonPath;
        }

        private async Task<string> LoadEntryContentAsync(DiaryEntry entry)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(entry.FullPath) && File.Exists(entry.FullPath))
                {
                    return await File.ReadAllTextAsync(entry.FullPath, Encoding.UTF8);
                }
                return await DiaryStorageService.Instance.LoadDiaryContentAsync(entry.Date);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load diary content for {entry.Date:yyyy-MM-dd}: {ex.Message}");
                return string.Empty;
            }
        }

        #endregion
    }
}
