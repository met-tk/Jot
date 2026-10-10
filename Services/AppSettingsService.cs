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
    public class AppSettingsService : IAppSettingsService
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
        private readonly object _timerLock = new();
        private Timer? _debounceTimer;
        private AppSettings _currentSettings = new();

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
                    string json = await File.ReadAllTextAsync(_settingsPath, Encoding.UTF8);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json, s_jsonOptions);
                    if (loaded != null)
                    {
                        _currentSettings = loaded;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AppSettings] Load failed, falling back to default: {ex.Message}");
                    _currentSettings = new AppSettings();
                    await SaveAsync();
                }
            }
            else
            {
                _currentSettings = new AppSettings();
                await SaveAsync();
            }

            // 广播一次初始加载通知
            SettingsChanged?.Invoke(this, _currentSettings);
        }

        public void Update(Action<AppSettings> updateAction)
        {
            if (updateAction == null) return;
            updateAction(_currentSettings);

            // 1. 0ms 内存极速广播
            SettingsChanged?.Invoke(this, _currentSettings);

            // 2. 300ms 磁盘防抖写入
            lock (_timerLock)
            {
                if (_debounceTimer == null)
                {
                    _debounceTimer = new Timer(_ => _ = SaveAsync(), null, 300, Timeout.Infinite);
                }
                else
                {
                    _debounceTimer.Change(300, Timeout.Infinite);
                }
            }
        }

        public async Task SaveAsync()
        {
            await _fileLock.WaitAsync();
            try
            {
                string json = JsonSerializer.Serialize(_currentSettings, s_jsonOptions);
                string tempPath = _settingsPath + ".tmp";
                await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8);
                File.Move(tempPath, _settingsPath, overwrite: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppSettings] Atomic save failed: {ex.Message}");
            }
            finally
            {
                _fileLock.Release();
            }
        }
    }
}
