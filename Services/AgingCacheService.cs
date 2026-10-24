using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Journal.Services
{
    /// <summary>
    /// 岁月沉淀多级缓存服务实现
    /// </summary>
    public sealed class AgingCacheService : IAgingCacheService
    {
        private static AgingCacheService? s_instance;
        public static AgingCacheService Instance => s_instance ??= new AgingCacheService();

        private static readonly EnumerationOptions s_safeEnumerationOptions = new()
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false
        };

        private readonly string _cacheDirectoryPath;
        private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _inFlightTasks = new(StringComparer.OrdinalIgnoreCase);

        public string CacheDirectoryPath => _cacheDirectoryPath;

        public AgingCacheService(string? customCacheDir = null)
        {
            _cacheDirectoryPath = ResolveCacheDirectory(customCacheDir);
        }

        public async Task<string> GetOrCreateCachedFileAsync(
            string sourcePath,
            string mode,
            Func<string, string, Task> processAction)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                throw new ArgumentException("Source path cannot be empty.", nameof(sourcePath));
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException($"Source texture file not found: {sourcePath}", sourcePath);
            ArgumentNullException.ThrowIfNull(processAction);

            string normMode = NormalizeMode(mode);
            string cacheKey = ComputeCacheKey(sourcePath, normMode);
            string targetPath = GetCachedFilePath(cacheKey, normMode);

            // 1. 快速检查磁盘有效缓存 (防并发 TOCTOU 与 0 字节损坏文件)
            if (IsValidCachedFile(targetPath))
            {
                return targetPath;
            }

            // 若存在损坏的 0 字节文件，进行防御性清理以自愈
            TryDeleteZeroByteFile(targetPath);

            // 2. 单飞防并发重入执行 (使用 Lazy 保证多线程并发下委托严格只执行一次)
            var lazyTask = _inFlightTasks.GetOrAdd(
                targetPath,
                key => new Lazy<Task<string>>(() => ExecuteGenerationAsync(sourcePath, key, processAction)));
            return await lazyTask.Value;
        }

        /// <summary>
        /// 提供指定 cacheKey 与 mode 的扩展重载
        /// </summary>
        public async Task<string> GetOrCreateCachedFileAsync(
            string sourcePath,
            string cacheKey,
            string mode,
            Func<string, string, Task> processAction)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                throw new ArgumentException("Source path cannot be empty.", nameof(sourcePath));
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException($"Source texture file not found: {sourcePath}", sourcePath);
            ArgumentNullException.ThrowIfNull(processAction);

            string normMode = NormalizeMode(mode);
            string targetPath = GetCachedFilePath(cacheKey, normMode);

            if (IsValidCachedFile(targetPath))
            {
                return targetPath;
            }

            TryDeleteZeroByteFile(targetPath);

            var lazyTask = _inFlightTasks.GetOrAdd(
                targetPath,
                key => new Lazy<Task<string>>(() => ExecuteGenerationAsync(sourcePath, key, processAction)));
            return await lazyTask.Value;
        }

        private async Task<string> ExecuteGenerationAsync(
            string sourcePath,
            string targetPath,
            Func<string, string, Task> processAction)
        {
            try
            {
                // 二次检查，避免排队期间已有并发同伴完成落盘
                if (IsValidCachedFile(targetPath))
                {
                    return targetPath;
                }

                string dir = Path.GetDirectoryName(targetPath) ?? _cacheDirectoryPath;
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string tempPath = Path.Combine(dir, $"{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
                try
                {
                    await processAction(sourcePath, tempPath);

                    if (!IsValidCachedFile(tempPath))
                    {
                        throw new InvalidOperationException($"Cache processing failed to generate a valid non-empty file at {tempPath}.");
                    }

                    // 原子移动替换
                    SafeAtomicMove(tempPath, targetPath);
                    return targetPath;
                }
                finally
                {
                    if (File.Exists(tempPath))
                    {
                        SafeDeleteFile(new FileInfo(tempPath));
                    }
                }
            }
            finally
            {
                _inFlightTasks.TryRemove(targetPath, out _);
            }
        }

        public string ComputeCacheKey(string sourcePath, string mode)
        {
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException($"Source texture file not found: {sourcePath}", sourcePath);

            string normalizedPath = Path.GetFullPath(sourcePath).ToLowerInvariant();
            var fileInfo = new FileInfo(sourcePath);
            long lastWriteTicks = fileInfo.LastWriteTimeUtc.Ticks;
            long fileLength = fileInfo.Length;
            string normMode = NormalizeMode(mode);

            string payload = $"{normalizedPath}|{lastWriteTicks}|{fileLength}|{normMode}|v2";
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public string GetCachedFilePath(string cacheKey, string mode)
        {
            string normMode = NormalizeMode(mode);
            string fileName = $"{cacheKey}_{normMode}.png";
            return Path.Combine(_cacheDirectoryPath, fileName);
        }

        public bool HasValidCache(string sourcePath, string mode)
        {
            try
            {
                if (!File.Exists(sourcePath)) return false;
                string cacheKey = ComputeCacheKey(sourcePath, mode);
                string path = GetCachedFilePath(cacheKey, mode);
                return IsValidCachedFile(path);
            }
            catch
            {
                return false;
            }
        }

        public long GetCacheSizeBytes()
        {
            if (!Directory.Exists(_cacheDirectoryPath)) return 0;
            long total = 0;
            try
            {
                var dir = new DirectoryInfo(_cacheDirectoryPath);
                foreach (var file in dir.EnumerateFiles("*.png", s_safeEnumerationOptions))
                {
                    try
                    {
                        total += file.Length;
                    }
                    catch (FileNotFoundException) { }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (System.Security.SecurityException) { }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AgingCacheService] GetCacheSizeBytes error: {ex.Message}");
            }
            return total;
        }

        public void ClearAllDiskCaches()
        {
            if (!Directory.Exists(_cacheDirectoryPath)) return;
            try
            {
                var dir = new DirectoryInfo(_cacheDirectoryPath);
                foreach (var file in dir.EnumerateFiles("*.png", s_safeEnumerationOptions))
                {
                    SafeDeleteFile(file);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AgingCacheService] ClearAllDiskCaches error: {ex.Message}");
            }
        }

        private static string NormalizeMode(string? mode)
        {
            if (string.IsNullOrWhiteSpace(mode)) return "gray";
            string m = mode.Trim().ToLowerInvariant();
            if (m is "wearmask-v3" or "wearmask-v4") return m;
            return (m.Contains("inv") || m.Contains("dark")) ? "inv" : "gray";
        }

        private static string ResolveCacheDirectory(string? customPath)
        {
            if (!string.IsNullOrWhiteSpace(customPath))
            {
                EnsureDirectoryWritable(customPath);
                return customPath;
            }

            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrWhiteSpace(localAppData))
                {
                    string primary = Path.Combine(localAppData, "Jot", "AgingCache");
                    EnsureDirectoryWritable(primary);
                    return primary;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AgingCacheService] Primary cache path inaccessible: {ex.Message}");
            }

            try
            {
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string fallback = Path.Combine(docs, "JournalData", ".cache", "AgingCache");
                EnsureDirectoryWritable(fallback);
                return fallback;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AgingCacheService] Secondary cache path inaccessible: {ex.Message}");
            }

            string baseFallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".cache", "AgingCache");
            EnsureDirectoryWritable(baseFallback);
            return baseFallback;
        }

        private static void EnsureDirectoryWritable(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            string probe = Path.Combine(path, $".probe_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }

        /// <summary>
        /// 安全获取指定文件的字节大小。
        /// 若文件不存在、已被并发删除、被独占锁定或访问异常，均安全返回 false，绝不向外冒泡异常。
        /// </summary>
        private static bool TryGetFileLength(string? path, out long length)
        {
            length = -1;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists)
                {
                    return false;
                }

                length = fi.Length;
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (System.Security.SecurityException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AgingCacheService] TryGetFileLength unexpected exception for '{path}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 判定指定路径的文件是否为有效缓存文件（即：物理存在且大小大于 0 字节）。
        /// 具备全链路异常防护，绝不抛出 FileNotFoundException、IOException 等竞态异常。
        /// </summary>
        private static bool IsValidCachedFile(string? path)
        {
            return TryGetFileLength(path, out long length) && length > 0;
        }

        /// <summary>
        /// 防御性检测并清理 0 字节损毁缓存文件。
        /// 严格保证仅在文件确为 0 字节时尝试删除，且异常全隔离，绝不影响主流程。
        /// </summary>
        private static void TryDeleteZeroByteFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (TryGetFileLength(path, out long length) && length == 0)
                {
                    var fi = new FileInfo(path);
                    SafeDeleteFile(fi);
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (IOException ex)
            {
                Debug.WriteLine($"[AgingCacheService] TryDeleteZeroByteFile IOException for '{path}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.WriteLine($"[AgingCacheService] TryDeleteZeroByteFile UnauthorizedAccessException for '{path}': {ex.Message}");
            }
            catch (System.Security.SecurityException ex)
            {
                Debug.WriteLine($"[AgingCacheService] TryDeleteZeroByteFile SecurityException for '{path}': {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AgingCacheService] TryDeleteZeroByteFile unexpected exception for '{path}': {ex.Message}");
            }
        }

        /// <summary>
        /// 安全删除文件，自动清除只读属性并静默吞吐并发锁冲突与已删除异常。
        /// </summary>
        private static bool SafeDeleteFile(FileInfo file)
        {
            try
            {
                if (file.Exists)
                {
                    if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                    {
                        file.Attributes = FileAttributes.Normal;
                    }
                    file.Delete();
                    return true;
                }
            }
            catch (FileNotFoundException)
            {
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (IOException ex)
            {
                Debug.WriteLine($"[AgingCacheService] File locked, skipping delete: {file.Name} ({ex.Message})");
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.WriteLine($"[AgingCacheService] Access denied deleting {file.Name}: {ex.Message}");
            }
            catch (System.Security.SecurityException ex)
            {
                Debug.WriteLine($"[AgingCacheService] Security exception deleting {file.Name}: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AgingCacheService] Unexpected error deleting {file.Name}: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// 具备瞬态锁与 Delete-Pending 状态重试的原子文件移动。
        /// </summary>
        private static void SafeAtomicMove(string sourceFile, string destinationFile)
        {
            const int maxRetries = 5;
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    File.Move(sourceFile, destinationFile, overwrite: true);
                    return;
                }
                catch (IOException) when (i < maxRetries - 1)
                {
                    Thread.Sleep(15);
                }
                catch (UnauthorizedAccessException) when (i < maxRetries - 1)
                {
                    Thread.Sleep(15);
                }
            }
            // 最终尝试
            File.Move(sourceFile, destinationFile, overwrite: true);
        }
    }
}
