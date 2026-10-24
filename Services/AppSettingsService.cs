using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Journal.Models;

namespace Journal.Services
{
    /// <summary>
    /// 全局应用设置持久化服务：原子落盘、300ms防抖、内存即时分发
    /// </summary>
    public sealed class AppSettingsService : IAppSettingsService
    {
        private static AppSettingsService? _instance;
        public static AppSettingsService Instance => _instance ??= new AppSettingsService();

        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = true
        };

        private readonly string _settingsPath;
        private readonly SemaphoreSlim _fileLock = new(1, 1);
        private readonly object _stateLock = new();
        private Timer? _debounceTimer;
        private AppSettings _currentSettings = new();
        private bool _isDisposed;

        public AppSettings CurrentSettings => _currentSettings;
        public event EventHandler<AppSettings>? SettingsChanged;

        public AppSettingsService(string? customPath = null)
        {
            if (!string.IsNullOrWhiteSpace(customPath))
            {
                _settingsPath = customPath;
                string? dir = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }
            else
            {
                string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "JournalData");
                if (!Directory.Exists(baseDir))
                {
                    Directory.CreateDirectory(baseDir);
                }
                _settingsPath = Path.Combine(baseDir, "settings.json");
            }
        }

        public async Task InitializeAsync()
        {
            if (File.Exists(_settingsPath))
            {
                try
                {
                    string json = await DiaryStorageService.ReadFileTextSharedAsync(_settingsPath);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json, s_jsonOptions);
                    if (loaded != null)
                    {
                        // 历史外部贴图路径静默自愈与平滑迁移
                        bool migrationNeeded = false;
                        if (!string.IsNullOrWhiteSpace(loaded.AgingPaperTexturePath))
                        {
                            string? migrated = await AgingTextureStorageService.Instance.MigrateLegacyTextureIfNeededAsync(loaded.AgingPaperTexturePath, "paper").ConfigureAwait(false);
                            if (!string.Equals(migrated, loaded.AgingPaperTexturePath, StringComparison.OrdinalIgnoreCase))
                            {
                                loaded.AgingPaperTexturePath = migrated;
                                migrationNeeded = true;
                            }
                        }
                        if (!string.IsNullOrWhiteSpace(loaded.AgingInkTexturePath))
                        {
                            string? migrated = await AgingTextureStorageService.Instance.MigrateLegacyTextureIfNeededAsync(loaded.AgingInkTexturePath, "ink").ConfigureAwait(false);
                            if (!string.Equals(migrated, loaded.AgingInkTexturePath, StringComparison.OrdinalIgnoreCase))
                            {
                                loaded.AgingInkTexturePath = migrated;
                                migrationNeeded = true;
                            }
                        }

                        lock (_stateLock)
                        {
                            _currentSettings = loaded;
                        }

                        if (migrationNeeded)
                        {
                            await SaveAsync();
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AppSettings] Load failed, falling back to default: {ex.Message}");
                    lock (_stateLock)
                    {
                        _currentSettings = new AppSettings();
                    }
                    await SaveAsync();
                }
            }
            else
            {
                lock (_stateLock)
                {
                    _currentSettings = new AppSettings();
                }
                await SaveAsync();
            }

            // 广播一次初始加载通知
            SettingsChanged?.Invoke(this, _currentSettings);
        }

        public void Update(Action<AppSettings> updateAction)
        {
            if (updateAction == null || _isDisposed) return;

            AppSettings snapshot;
            lock (_stateLock)
            {
                updateAction(_currentSettings);
                snapshot = _currentSettings;

                if (_debounceTimer == null)
                {
                    _debounceTimer = new Timer(_ => _ = SaveAsync(), null, 300, Timeout.Infinite);
                }
                else
                {
                    _debounceTimer.Change(300, Timeout.Infinite);
                }
            }

            // 0ms 内存极速广播
            SettingsChanged?.Invoke(this, snapshot);
        }

        public async Task SaveAsync()
        {
            if (_isDisposed) return;

            AppSettings snapshot;
            lock (_stateLock)
            {
                snapshot = _currentSettings.Clone();
            }

            await _fileLock.WaitAsync();
            string tempPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                string json = JsonSerializer.Serialize(snapshot, s_jsonOptions);
                await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8);
                File.Move(tempPath, _settingsPath, overwrite: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppSettings] Atomic save failed: {ex.Message}");
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch { }
                _fileLock.Release();
            }
        }

        public void Dispose()
        {
            lock (_stateLock)
            {
                if (_isDisposed) return;
                _isDisposed = true;
                _debounceTimer?.Dispose();
                _debounceTimer = null;
            }
        }
    }
}
