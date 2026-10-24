using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Journal.Services
{
    /// <summary>
    /// “岁月沉淀”自定义贴图持久化存储服务
    /// 负责用户自定义纸纹与墨渍母本的持久化归档、内容寻址去重、格式有效性嗅探、
    /// 历史外部路径平滑自愈迁移与孤儿母本垃圾回收。
    /// </summary>
    public sealed class AgingTextureStorageService
    {
        private static AgingTextureStorageService? s_instance;
        public static AgingTextureStorageService Instance => s_instance ??= new AgingTextureStorageService();

        /// <summary>
        /// 最大允许单张贴图大小（50MB），防止异常大图导致内存溢出
        /// </summary>
        public const long MaxTextureSizeBytes = 50 * 1024 * 1024;

        private static readonly Regex s_storedFileNameRegex = new(
            @"^(?:paper|ink)_[a-fA-F0-9]{12}_(.+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly string _storageDirectoryPath;

        public string StorageDirectoryPath => _storageDirectoryPath;

        public AgingTextureStorageService(string? customStorageDir = null)
        {
            _storageDirectoryPath = ResolveStorageDirectory(customStorageDir);
        }

        /// <summary>
        /// 将用户选择的外部贴图文件安全拷贝并持久化存储到应用专用贴图目录中。
        /// 具备白名单槽位校验、防路径穿越、格式校验、SHA-256 内容寻址去重与原子替换。
        /// </summary>
        /// <param name="sourceFilePath">用户选择的原始文件路径</param>
        /// <param name="slotPrefix">贴图类型前缀，严格限制为 "paper" 或 "ink"</param>
        /// <returns>持久化存储后的规范化文件完整绝对路径</returns>
        public async Task<string> SaveCustomTextureAsync(string sourceFilePath, string slotPrefix)
        {
            string validatedSlot = ValidateSlotPrefix(slotPrefix);
            var fileInfo = ValidateSourceFile(sourceFilePath);
            string ext = ValidateExtensionAndHeader(sourceFilePath);

            // 计算文件内容 SHA-256（取前 12 位十六进制作为哈希指纹）
            string hash = await ComputeFileHash12Async(sourceFilePath).ConfigureAwait(false);

            string destinationPath = BuildAndValidateDestinationPath(validatedSlot, hash, sourceFilePath, ext);

            // 若已存在相同内容的文件，直接复用已有持久化资产 (CAS 去重)
            if (File.Exists(destinationPath))
            {
                try
                {
                    var destInfo = new FileInfo(destinationPath);
                    if (destInfo.Length == fileInfo.Length)
                    {
                        return Path.GetFullPath(destinationPath);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AgingTextureStorageService] Verify existing dest file error: {ex.Message}");
                }
            }

            // 原子写入持久化目录（临时文件 -> Move 覆盖）
            EnsureDirectoryExists(_storageDirectoryPath);
            string tempFilePath = Path.Combine(_storageDirectoryPath, $".tmp_{Guid.NewGuid():N}{ext}");

            try
            {
                using (var sourceStream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var destStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await sourceStream.CopyToAsync(destStream).ConfigureAwait(false);
                    await destStream.FlushAsync().ConfigureAwait(false);
                }

                // 校验临时文件完整性
                var tempInfo = new FileInfo(tempFilePath);
                if (tempInfo.Length != fileInfo.Length)
                {
                    throw new IOException("贴图持久化存储拷贝不完整。");
                }

                // 原子移动覆盖
                SafeAtomicMove(tempFilePath, destinationPath);
                return Path.GetFullPath(destinationPath);
            }
            finally
            {
                if (File.Exists(tempFilePath))
                {
                    try { File.Delete(tempFilePath); } catch { }
                }
            }
        }

        /// <summary>
        /// 同步持久化保存贴图（完全原生同步实现，杜绝 Sync-over-Async 死锁隐患）
        /// </summary>
        public string SaveCustomTexture(string sourceFilePath, string slotPrefix)
        {
            string validatedSlot = ValidateSlotPrefix(slotPrefix);
            var fileInfo = ValidateSourceFile(sourceFilePath);
            string ext = ValidateExtensionAndHeader(sourceFilePath);

            string hash = ComputeFileHash12Sync(sourceFilePath);
            string destinationPath = BuildAndValidateDestinationPath(validatedSlot, hash, sourceFilePath, ext);

            if (File.Exists(destinationPath))
            {
                try
                {
                    var destInfo = new FileInfo(destinationPath);
                    if (destInfo.Length == fileInfo.Length)
                    {
                        return Path.GetFullPath(destinationPath);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AgingTextureStorageService] Verify existing dest file error: {ex.Message}");
                }
            }

            EnsureDirectoryExists(_storageDirectoryPath);
            string tempFilePath = Path.Combine(_storageDirectoryPath, $".tmp_{Guid.NewGuid():N}{ext}");

            try
            {
                using (var sourceStream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var destStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    sourceStream.CopyTo(destStream);
                    destStream.Flush();
                }

                var tempInfo = new FileInfo(tempFilePath);
                if (tempInfo.Length != fileInfo.Length)
                {
                    throw new IOException("贴图持久化存储拷贝不完整。");
                }

                SafeAtomicMove(tempFilePath, destinationPath);
                return Path.GetFullPath(destinationPath);
            }
            finally
            {
                if (File.Exists(tempFilePath))
                {
                    try { File.Delete(tempFilePath); } catch { }
                }
            }
        }

        /// <summary>
        /// 异步自愈迁移旧配置中的贴图路径。
        /// 若路径已位于持久化存储目录中且存在，保持原样；
        /// 若路径位于外部且物理文件存在，自动复制进持久化目录并返回新路径；
        /// 若路径物理文件已丢失，返回 null（回退至内置经典）。
        /// </summary>
        public async Task<string?> MigrateLegacyTextureIfNeededAsync(string? currentPath, string slotPrefix)
        {
            if (string.IsNullOrWhiteSpace(currentPath)) return null;

            try
            {
                if (!File.Exists(currentPath))
                {
                    Debug.WriteLine($"[AgingTextureStorageService] Legacy texture not found, reset to default: {currentPath}");
                    return null;
                }

                string fullCurrent = Path.GetFullPath(currentPath);
                string storageDirFull = Path.GetFullPath(_storageDirectoryPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

                // 检查是否已经在持久化目录下
                if (fullCurrent.StartsWith(storageDirFull, StringComparison.OrdinalIgnoreCase))
                {
                    return fullCurrent;
                }

                // 外部路径存在，执行自动迁移复制
                Debug.WriteLine($"[AgingTextureStorageService] Migrating external texture: {fullCurrent}");
                string migratedPath = await SaveCustomTextureAsync(fullCurrent, slotPrefix).ConfigureAwait(false);
                return migratedPath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AgingTextureStorageService] MigrateLegacyTextureIfNeededAsync error: {ex.Message}");
                return File.Exists(currentPath) ? currentPath : null;
            }
        }

        /// <summary>
        /// 同步自愈迁移
        /// </summary>
        public string? MigrateLegacyTextureIfNeeded(string? currentPath, string slotPrefix)
        {
            if (string.IsNullOrWhiteSpace(currentPath)) return null;

            try
            {
                if (!File.Exists(currentPath))
                {
                    Debug.WriteLine($"[AgingTextureStorageService] Legacy texture not found, reset to default: {currentPath}");
                    return null;
                }

                string fullCurrent = Path.GetFullPath(currentPath);
                string storageDirFull = Path.GetFullPath(_storageDirectoryPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

                if (fullCurrent.StartsWith(storageDirFull, StringComparison.OrdinalIgnoreCase))
                {
                    return fullCurrent;
                }

                Debug.WriteLine($"[AgingTextureStorageService] Migrating external texture: {fullCurrent}");
                string migratedPath = SaveCustomTexture(fullCurrent, slotPrefix);
                return migratedPath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AgingTextureStorageService] MigrateLegacyTextureIfNeeded error: {ex.Message}");
                return File.Exists(currentPath) ? currentPath : null;
            }
        }

        /// <summary>
        /// 从持久化文件名中提取用户原始选择的友好展示名称
        /// </summary>
        public static string GetDisplayName(string? path, string defaultDisplayName)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return defaultDisplayName;
            }

            string fileName = Path.GetFileName(path);
            var match = s_storedFileNameRegex.Match(fileName);
            if (match.Success && match.Groups.Count >= 2)
            {
                return match.Groups[1].Value;
            }

            return fileName;
        }

        /// <summary>
        /// 安全清理未被任何槽位（纸张或墨水）引用的孤儿母本贴图，防止长期占用磁盘。
        /// 严格防御：仅清理符合本模块命名规则的孤儿母本，绝不误删用户其他文件；
        /// 跳过临时写入文件，绝不引发并发竞争删除。
        /// </summary>
        public void CleanupOrphanTextures(string? activePaperPath, string? activeInkPath)
        {
            try
            {
                if (!Directory.Exists(_storageDirectoryPath)) return;

                string? normPaper = string.IsNullOrWhiteSpace(activePaperPath) ? null : Path.GetFullPath(activePaperPath);
                string? normInk = string.IsNullOrWhiteSpace(activeInkPath) ? null : Path.GetFullPath(activeInkPath);

                var dir = new DirectoryInfo(_storageDirectoryPath);
                foreach (var file in dir.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
                {
                    // 严格跳过并发写入中的临时文件（仅清理超过 24 小时的异常断电残留孤立临时文件）
                    if (file.Name.StartsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        if (DateTime.UtcNow - file.CreationTimeUtc > TimeSpan.FromHours(24))
                        {
                            try { file.Delete(); } catch { }
                        }
                        continue;
                    }

                    // 严格仅清理符合本服务规范命名的贴图，绝不误删目录下其他用户文件
                    if (!s_storedFileNameRegex.IsMatch(file.Name))
                    {
                        continue;
                    }

                    string fileFullPath = file.FullName;
                    bool isReferenced = (normPaper != null && string.Equals(fileFullPath, normPaper, StringComparison.OrdinalIgnoreCase)) ||
                                        (normInk != null && string.Equals(fileFullPath, normInk, StringComparison.OrdinalIgnoreCase));

                    if (!isReferenced)
                    {
                        try
                        {
                            file.Delete();
                            Debug.WriteLine($"[AgingTextureStorageService] Cleaned orphan custom texture: {file.Name}");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[AgingTextureStorageService] Skip busy orphan file: {file.Name} ({ex.Message})");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AgingTextureStorageService] CleanupOrphanTextures error: {ex.Message}");
            }
        }

        private static string ValidateSlotPrefix(string slotPrefix)
        {
            string norm = (slotPrefix ?? string.Empty).Trim().ToLowerInvariant();
            if (norm is not ("paper" or "ink"))
            {
                throw new ArgumentException("Slot prefix must be 'paper' or 'ink'.", nameof(slotPrefix));
            }
            return norm;
        }

        private static FileInfo ValidateSourceFile(string sourceFilePath)
        {
            if (string.IsNullOrWhiteSpace(sourceFilePath))
                throw new ArgumentException("Source texture file path cannot be empty.", nameof(sourceFilePath));

            if (!File.Exists(sourceFilePath))
                throw new FileNotFoundException($"Source texture file not found: {sourceFilePath}", sourceFilePath);

            var fileInfo = new FileInfo(sourceFilePath);
            if (fileInfo.Length == 0)
                throw new InvalidDataException("贴图文件大小为 0 字节，无法作为有效纹理载入。");

            if (fileInfo.Length > MaxTextureSizeBytes)
                throw new InvalidDataException($"贴图文件超过最大限制 ({MaxTextureSizeBytes / (1024 * 1024)}MB)。");

            return fileInfo;
        }

        private static string ValidateExtensionAndHeader(string sourceFilePath)
        {
            string ext = Path.GetExtension(sourceFilePath).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp"))
                throw new NotSupportedException($"不支持的贴图格式 '{ext}'，仅支持 .png, .jpg, .jpeg, .bmp 格式。");

            if (!ValidateImageHeader(sourceFilePath, ext))
                throw new InvalidDataException("贴图文件不是有效的图片文件或已损坏。");

            return ext;
        }

        private string BuildAndValidateDestinationPath(string slotPrefix, string hash, string sourceFilePath, string ext)
        {
            string rawName = Path.GetFileNameWithoutExtension(sourceFilePath);
            string safeName = SanitizeFileName(rawName);
            if (string.IsNullOrWhiteSpace(safeName))
            {
                safeName = "texture";
            }
            string destinationFileName = $"{slotPrefix}_{hash}_{safeName}{ext}";
            string destinationPath = Path.Combine(_storageDirectoryPath, destinationFileName);

            // 防路径穿越验证：确保解析出的物理路径严格位于目标存储目录内
            string canonicalDest = Path.GetFullPath(destinationPath);
            string canonicalDir = Path.GetFullPath(_storageDirectoryPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!canonicalDest.StartsWith(canonicalDir, StringComparison.OrdinalIgnoreCase))
            {
                throw new System.Security.SecurityException("Destination path escapes storage directory.");
            }

            return canonicalDest;
        }

        private static string ResolveStorageDirectory(string? customPath)
        {
            if (!string.IsNullOrWhiteSpace(customPath))
            {
                EnsureDirectoryExists(customPath);
                return customPath;
            }

            try
            {
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (!string.IsNullOrWhiteSpace(docs))
                {
                    string primary = Path.Combine(docs, "JournalData", "Textures");
                    EnsureDirectoryExists(primary);
                    return primary;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AgingTextureStorageService] Documents inaccessible: {ex.Message}");
            }

            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrWhiteSpace(localAppData))
                {
                    string fallback = Path.Combine(localAppData, "Jot", "CustomTextures");
                    EnsureDirectoryExists(fallback);
                    return fallback;
                }
            }
            catch { }

            string baseFallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "JournalData", "Textures");
            EnsureDirectoryExists(baseFallback);
            return baseFallback;
        }

        private static void EnsureDirectoryExists(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        private static async Task<string> ComputeFileHash12Async(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] hashBytes = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
            string hex = Convert.ToHexString(hashBytes).ToLowerInvariant();
            return hex.Length > 12 ? hex[..12] : hex;
        }

        private static string ComputeFileHash12Sync(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] hashBytes = SHA256.HashData(stream);
            string hex = Convert.ToHexString(hashBytes).ToLowerInvariant();
            return hex.Length > 12 ? hex[..12] : hex;
        }

        private static bool ValidateImageHeader(string filePath, string extension)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length < 4) return false;

                byte[] header = new byte[8];
                int read = fs.Read(header, 0, header.Length);
                if (read < 4) return false;

                return extension switch
                {
                    ".png" => header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47,
                    ".jpg" or ".jpeg" => header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
                    ".bmp" => header[0] == 0x42 && header[1] == 0x4D,
                    _ => false
                };
            }
            catch
            {
                return false;
            }
        }

        private static string SanitizeFileName(string fileName)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (char c in fileName)
            {
                if (Array.IndexOf(invalidChars, c) < 0 && c != ' ' && c != '_' && c != '-')
                {
                    sb.Append(char.IsLetterOrDigit(c) ? c : '_');
                }
                else if (c is '-' or '_')
                {
                    sb.Append(c);
                }
                else
                {
                    sb.Append('_');
                }
            }
            string result = sb.ToString().Trim('_', '-');
            return result.Length > 32 ? result[..32] : result;
        }

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
                    System.Threading.Thread.Sleep(20);
                }
                catch (UnauthorizedAccessException) when (i < maxRetries - 1)
                {
                    System.Threading.Thread.Sleep(20);
                }
            }
            File.Move(sourceFile, destinationFile, overwrite: true);
        }
    }
}
