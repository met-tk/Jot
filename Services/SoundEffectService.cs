using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Journal.Services
{
    /// <summary>
    /// 超低延迟、零 GC 拟物物理音效服务：
    /// 基于 WinMM PlaySound 与内存预调音 PCM 缓冲区，达成单次微秒级极速响应；
    /// 支持双轨并发混音（普通击键与换行铜铃余音互不打断）。
    /// </summary>
    public sealed class SoundEffectService : ISoundEffectService, IDisposable
    {
        private static SoundEffectService? _instance;
        public static SoundEffectService Instance => _instance ??= new SoundEffectService();

        [DllImport("winmm.dll", EntryPoint = "PlaySoundW", SetLastError = true)]
        private static extern bool PlaySound(byte[]? pszSound, IntPtr hmod, uint fdwSound);

        private const uint SND_ASYNC = 0x0001;
        private const uint SND_NODEFAULT = 0x0002;
        private const uint SND_MEMORY = 0x0004;

        // WaveOut API P/Invoke (用于回车长铃与手动保存音效二级音频轨道，实现双轨硬件混音)
        [DllImport("winmm.dll")]
        private static extern int waveOutOpen(out IntPtr hWaveOut, uint uDeviceID, ref WAVEFORMATEX lpFormat, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);

        [DllImport("winmm.dll")]
        private static extern int waveOutPrepareHeader(IntPtr hWaveOut, IntPtr lpWaveHdr, uint uSize);

        [DllImport("winmm.dll")]
        private static extern int waveOutWrite(IntPtr hWaveOut, IntPtr lpWaveHdr, uint uSize);

        [DllImport("winmm.dll")]
        private static extern int waveOutUnprepareHeader(IntPtr hWaveOut, IntPtr lpWaveHdr, uint uSize);

        [DllImport("winmm.dll")]
        private static extern int waveOutReset(IntPtr hWaveOut);

        [DllImport("winmm.dll")]
        private static extern int waveOutClose(IntPtr hWaveOut);

        private const uint WHDR_DONE = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        /// <summary>
        /// WaveOut 异步音频缓冲区上下文：
        /// 严格保证非托管 WAVEHDR 结构体与固定 PCM 数组句柄（GCHandle）的成对完整生命周期管理
        /// </summary>
        private sealed class WaveOutBufferContext : IDisposable
        {
            private readonly IntPtr _hWaveOut;
            private IntPtr _pHeader;
            private GCHandle _pinnedData;
            private bool _isPrepared;
            private bool _isDisposed;
            private readonly object _stateLock = new();

            public WaveOutBufferContext(IntPtr hWaveOut, byte[] pcmData)
            {
                _hWaveOut = hWaveOut;
                _pinnedData = GCHandle.Alloc(pcmData, GCHandleType.Pinned);

                var hdr = new WAVEHDR
                {
                    lpData = _pinnedData.AddrOfPinnedObject(),
                    dwBufferLength = (uint)pcmData.Length,
                    dwFlags = 0
                };

                _pHeader = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());
                Marshal.StructureToPtr(hdr, _pHeader, false);
            }

            public bool Play()
            {
                lock (_stateLock)
                {
                    if (_isDisposed || _pHeader == IntPtr.Zero || _hWaveOut == IntPtr.Zero)
                        return false;

                    uint hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();
                    int prepRes = waveOutPrepareHeader(_hWaveOut, _pHeader, hdrSize);
                    if (prepRes != 0) return false;
                    _isPrepared = true;

                    int writeRes = waveOutWrite(_hWaveOut, _pHeader, hdrSize);
                    if (writeRes != 0)
                    {
                        Unprepare();
                        return false;
                    }
                    return true;
                }
            }

            public bool IsDone()
            {
                lock (_stateLock)
                {
                    if (_isDisposed || _pHeader == IntPtr.Zero) return true;
                    var hdr = Marshal.PtrToStructure<WAVEHDR>(_pHeader);
                    return (hdr.dwFlags & WHDR_DONE) != 0;
                }
            }

            public void Unprepare()
            {
                lock (_stateLock)
                {
                    if (_isPrepared && _pHeader != IntPtr.Zero && _hWaveOut != IntPtr.Zero)
                    {
                        uint hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();
                        waveOutUnprepareHeader(_hWaveOut, _pHeader, hdrSize);
                        _isPrepared = false;
                    }
                }
            }

            public void Dispose()
            {
                lock (_stateLock)
                {
                    if (_isDisposed) return;
                    _isDisposed = true;

                    Unprepare();

                    if (_pinnedData.IsAllocated)
                    {
                        _pinnedData.Free();
                    }

                    if (_pHeader != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(_pHeader);
                        _pHeader = IntPtr.Zero;
                    }
                }
            }
        }

        public bool IsEnabled { get; private set; } = true;
        public string CurrentTheme { get; private set; } = "Typewriter";
        public string CurrentSaveTheme { get; private set; } = "TypewriterBell";
        public double Volume { get; private set; } = 0.8;

        private readonly Dictionary<string, byte[]> _rawAudioTemplates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _scaledAudioCache = new(StringComparer.Ordinal);
        private readonly Random _random = new();
        private readonly object _lock = new();
        private long _lastPlayTicks;
        private long _lastEnterPlayTicks;
        private long _lastSavePlayTicks;
        private bool _isDisposed;

        // Fisher-Yates 防连续重复洗牌袋（Shuffle Bag）状态
        private readonly List<int> _clickShuffleBag = new(PcmAudioSynthesizer.ClickVariantCount);
        private readonly List<int> _specialShuffleBag = new(PcmAudioSynthesizer.SpecialVariantCount);
        private int _lastClickIndex = -1;
        private int _lastSpecialIndex = -1;

        // 二级回车与保存长铃句柄及安全生命周期跟踪池
        private IntPtr _hWaveOut = IntPtr.Zero;
        private bool _isBellWaveOutActive;
        private readonly List<WaveOutBufferContext> _activeBellBuffers = new();
        private readonly object _waveOutLock = new();
        private Timer? _bellCleanupTimer;

        public SoundEffectService()
        {
            var initialSettings = AppSettingsService.Instance.CurrentSettings;
            IsEnabled = initialSettings.IsSoundEnabled;
            CurrentTheme = string.IsNullOrWhiteSpace(initialSettings.SoundTheme) ? "Typewriter" : initialSettings.SoundTheme;
            CurrentSaveTheme = string.IsNullOrWhiteSpace(initialSettings.SaveSoundTheme) ? "TypewriterBell" : initialSettings.SaveSoundTheme;
            Volume = Math.Clamp(initialSettings.SoundVolume, 0.0, 1.0);

            LoadThemeTemplatesLocked(CurrentTheme, refreshScaled: false);
            LoadSaveTemplateLocked(CurrentSaveTheme, refreshScaled: false);
            RefreshScaledCacheLocked();
            InitWaveOutSecondaryTrack();
        }

        private void InitWaveOutSecondaryTrack()
        {
            try
            {
                var format = new WAVEFORMATEX
                {
                    wFormatTag = 1, // PCM
                    nChannels = (ushort)PcmAudioSynthesizer.Channels,
                    nSamplesPerSec = (uint)PcmAudioSynthesizer.SampleRate,
                    nAvgBytesPerSec = (uint)(PcmAudioSynthesizer.SampleRate * PcmAudioSynthesizer.Channels * PcmAudioSynthesizer.BitsPerSample / 8),
                    nBlockAlign = (ushort)(PcmAudioSynthesizer.Channels * PcmAudioSynthesizer.BitsPerSample / 8),
                    wBitsPerSample = (ushort)PcmAudioSynthesizer.BitsPerSample,
                    cbSize = 0
                };

                const uint WAVE_MAPPER = unchecked((uint)-1);
                int res = waveOutOpen(out _hWaveOut, WAVE_MAPPER, ref format, IntPtr.Zero, IntPtr.Zero, 0);
                _isBellWaveOutActive = (res == 0 && _hWaveOut != IntPtr.Zero);
            }
            catch
            {
                _isBellWaveOutActive = false;
            }
        }

        /// <summary>
        /// 从指定洗牌袋中抽取下一个不与上一次重复的变体索引（调用方需持有 _lock）
        /// </summary>
        private int NextNonRepeatingIndexLocked(List<int> bag, ref int lastIndex, int count)
        {
            if (count <= 1) return 0;

            if (bag.Count == 0)
            {
                for (int i = 0; i < count; i++)
                {
                    bag.Add(i);
                }

                // Fisher-Yates 随机洗牌
                for (int i = count - 1; i > 0; i--)
                {
                    int j = _random.Next(0, i + 1);
                    (bag[i], bag[j]) = (bag[j], bag[i]);
                }

                // 跨袋边界去重：若新洗牌袋即将弹出的首个元素与上一轮末尾相同，则与袋内前段元素交换
                if (bag[^1] == lastIndex)
                {
                    int swapIdx = _random.Next(0, count - 1);
                    (bag[^1], bag[swapIdx]) = (bag[swapIdx], bag[^1]);
                }
            }

            int next = bag[^1];
            bag.RemoveAt(bag.Count - 1);
            lastIndex = next;
            return next;
        }

        /// <summary>
        /// 极速播放按键敲击音效（支持 10 变体防连续重复随机洗牌）
        /// </summary>
        public void PlayKeyClick(bool isSpecialKey = false)
        {
            if (_isDisposed || !IsEnabled || Volume <= 0.001) return;

            // 20ms 连击与长按按键防爆破节流保护
            long now = DateTime.UtcNow.Ticks;
            if (now - Interlocked.Read(ref _lastPlayTicks) < 200_000) return;
            Interlocked.Exchange(ref _lastPlayTicks, now);

            byte[]? pcmBytes;
            lock (_lock)
            {
                string key;
                if (isSpecialKey)
                {
                    int sIdx = NextNonRepeatingIndexLocked(_specialShuffleBag, ref _lastSpecialIndex, PcmAudioSynthesizer.SpecialVariantCount);
                    key = sIdx == 0 ? "special" : $"special_{sIdx}";
                }
                else
                {
                    int cIdx = NextNonRepeatingIndexLocked(_clickShuffleBag, ref _lastClickIndex, PcmAudioSynthesizer.ClickVariantCount);
                    key = $"click_{cIdx}";
                }

                if (!_scaledAudioCache.TryGetValue(key, out pcmBytes))
                {
                    _scaledAudioCache.TryGetValue(isSpecialKey ? "special" : "click_0", out pcmBytes);
                }
            }

            if (pcmBytes != null && pcmBytes.Length > 44)
            {
                PlaySound(pcmBytes, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
            }
        }

        /// <summary>
        /// 回收已播放完成的 WaveOut 缓冲区，释放非托管 WAVEHDR 与固定句柄
        /// </summary>
        private void CleanupCompletedBellBuffersLocked(bool forceAll = false)
        {
            for (int i = _activeBellBuffers.Count - 1; i >= 0; i--)
            {
                var ctx = _activeBellBuffers[i];
                if (forceAll || ctx.IsDone())
                {
                    ctx.Dispose();
                    _activeBellBuffers.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// 播放回车换行专属音效（支持独立第二轨道并发，余音不被后续快速敲击截断）
        /// </summary>
        public void PlayEnterKey()
        {
            if (_isDisposed || !IsEnabled || Volume <= 0.001) return;

            long now = DateTime.UtcNow.Ticks;
            if (now - Interlocked.Read(ref _lastEnterPlayTicks) < 350_000) return;
            Interlocked.Exchange(ref _lastEnterPlayTicks, now);
            Interlocked.Exchange(ref _lastPlayTicks, now);

            byte[]? enterBytes;
            lock (_lock)
            {
                _scaledAudioCache.TryGetValue("enter", out enterBytes);
            }

            if (enterBytes == null || enterBytes.Length <= 44) return;
            PlayOnSecondaryWaveOutTrack(enterBytes, cleanupDelayMs: 650);
        }

        /// <summary>
        /// 播放手动保存专属提示音效（Ctrl+S 或点击保存按钮时触发）
        /// </summary>
        public void PlaySaveSound()
        {
            if (_isDisposed || !IsEnabled || Volume <= 0.001) return;
            if (string.IsNullOrWhiteSpace(CurrentSaveTheme) ||
                string.Equals(CurrentSaveTheme, "None", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            long now = DateTime.UtcNow.Ticks;
            if (now - Interlocked.Read(ref _lastSavePlayTicks) < 800_000) return;
            Interlocked.Exchange(ref _lastSavePlayTicks, now);

            byte[]? saveBytes;
            lock (_lock)
            {
                _scaledAudioCache.TryGetValue("save", out saveBytes);
            }

            if (saveBytes == null || saveBytes.Length <= 44) return;
            PlayOnSecondaryWaveOutTrack(saveBytes, cleanupDelayMs: 5200);
        }

        private void PlayOnSecondaryWaveOutTrack(byte[] wavBytes, int cleanupDelayMs)
        {
            if (_isBellWaveOutActive && _hWaveOut != IntPtr.Zero && wavBytes.Length > 44)
            {
                try
                {
                    byte[] pcmPayload = new byte[wavBytes.Length - 44];
                    Buffer.BlockCopy(wavBytes, 44, pcmPayload, 0, pcmPayload.Length);

                    int dynamicDelayMs = Math.Max(cleanupDelayMs, (int)Math.Ceiling(pcmPayload.Length / 88.2) + 350);

                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            lock (_waveOutLock)
                            {
                                if (_isDisposed || !_isBellWaveOutActive || _hWaveOut == IntPtr.Zero)
                                {
                                    if (!_isDisposed)
                                    {
                                        PlaySound(wavBytes, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
                                    }
                                    return;
                                }

                                CleanupCompletedBellBuffersLocked();

                                if (_activeBellBuffers.Count > 0)
                                {
                                    waveOutReset(_hWaveOut);
                                    CleanupCompletedBellBuffersLocked(forceAll: true);
                                }

                                var ctx = new WaveOutBufferContext(_hWaveOut, pcmPayload);
                                if (ctx.Play())
                                {
                                    _activeBellBuffers.Add(ctx);
                                    if (_bellCleanupTimer == null)
                                    {
                                        _bellCleanupTimer = new Timer(OnBellCleanupTimerCallback, null, dynamicDelayMs, Timeout.Infinite);
                                    }
                                    else
                                    {
                                        _bellCleanupTimer.Change(dynamicDelayMs, Timeout.Infinite);
                                    }
                                }
                                else
                                {
                                    ctx.Dispose();
                                    PlaySound(wavBytes, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
                                }
                            }
                        }
                        catch
                        {
                            if (!_isDisposed)
                            {
                                PlaySound(wavBytes, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
                            }
                        }
                    });
                    return;
                }
                catch
                {
                    // 异常降级
                }
            }

            if (!_isDisposed)
            {
                PlaySound(wavBytes, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
            }
        }

        private void OnBellCleanupTimerCallback(object? state)
        {
            lock (_waveOutLock)
            {
                if (_isDisposed) return;
                CleanupCompletedBellBuffersLocked();
            }
        }

        public void SetTheme(string theme)
        {
            if (string.IsNullOrWhiteSpace(theme)) theme = "Typewriter";
            lock (_lock)
            {
                if (string.Equals(CurrentTheme, theme, StringComparison.Ordinal) && _rawAudioTemplates.ContainsKey("click_0"))
                {
                    return;
                }

                CurrentTheme = theme;
                LoadThemeTemplatesLocked(theme, refreshScaled: true);
            }
        }

        public void SetSaveTheme(string saveTheme)
        {
            if (string.IsNullOrWhiteSpace(saveTheme)) saveTheme = "TypewriterBell";
            lock (_lock)
            {
                if (string.Equals(CurrentSaveTheme, saveTheme, StringComparison.Ordinal) &&
                    (_rawAudioTemplates.ContainsKey("save") || string.Equals(saveTheme, "None", StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                CurrentSaveTheme = saveTheme;
                LoadSaveTemplateLocked(saveTheme, refreshScaled: true);
            }
        }

        public void SetVolume(double volume)
        {
            double clamped = Math.Clamp(volume, 0.0, 1.0);
            lock (_lock)
            {
                if (Math.Abs(Volume - clamped) < 0.0001 && _scaledAudioCache.Count > 0) return;
                Volume = clamped;
                RefreshScaledCacheLocked();
            }
        }

        public void SetEnabled(bool enabled)
        {
            IsEnabled = enabled;
        }

        private static string ResolveAudioDirectory(params string[] subPaths)
        {
            string baseDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Audio");
            string candidate = Path.Combine(baseDir, Path.Combine(subPaths));
            if (Directory.Exists(candidate)) return candidate;

            try
            {
                string devCandidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Assets", "Audio", Path.Combine(subPaths)));
                if (Directory.Exists(devCandidate)) return devCandidate;
            }
            catch { }

            return candidate;
        }

        private static readonly int[] s_clickVariantCodes = { 0, 1, 2, 5, 6, 7, 8, 9, 10, 11 };

        private void LoadThemeTemplatesLocked(string theme, bool refreshScaled)
        {
            _clickShuffleBag.Clear();
            _specialShuffleBag.Clear();
            _lastClickIndex = -1;
            _lastSpecialIndex = -1;

            string themeDir = ResolveAudioDirectory(theme);
            double gain = Volume * Volume;

            for (int i = 0; i < PcmAudioSynthesizer.ClickVariantCount; i++)
            {
                string key = $"click_{i}";
                string fileName = $"click{i + 1}.wav";
                byte[] raw = LoadOrSynthesize(themeDir, fileName, theme, s_clickVariantCodes[i]);
                _rawAudioTemplates[key] = raw;
                if (refreshScaled) UpdateSingleScaledBufferLocked(key, raw, gain);
            }

            string[] specialKeys = { "special", "special_1", "special_2" };
            string[] specialFiles = { "special.wav", "special2.wav", "special3.wav" };
            int[] specialCodes = { 3, 12, 13 };
            for (int i = 0; i < specialKeys.Length; i++)
            {
                byte[] raw = LoadOrSynthesize(themeDir, specialFiles[i], theme, specialCodes[i]);
                _rawAudioTemplates[specialKeys[i]] = raw;
                if (refreshScaled) UpdateSingleScaledBufferLocked(specialKeys[i], raw, gain);
            }

            byte[] enterRaw = LoadOrSynthesize(themeDir, "enter.wav", theme, 4);
            _rawAudioTemplates["enter"] = enterRaw;
            if (refreshScaled) UpdateSingleScaledBufferLocked("enter", enterRaw, gain);
        }

        private void LoadSaveTemplateLocked(string saveTheme, bool refreshScaled)
        {
            if (string.IsNullOrWhiteSpace(saveTheme) || string.Equals(saveTheme, "None", StringComparison.OrdinalIgnoreCase))
            {
                _rawAudioTemplates.Remove("save");
                _scaledAudioCache.Remove("save");
                return;
            }

            byte[]? resolvedData = null;
            string saveDir = ResolveAudioDirectory("Save");
            string fullPath = Path.Combine(saveDir, $"{saveTheme}.wav");
            if (File.Exists(fullPath))
            {
                try
                {
                    byte[] data = File.ReadAllBytes(fullPath);
                    if (data.Length > 44)
                    {
                        resolvedData = data;
                    }
                }
                catch { }
            }

            resolvedData ??= PcmAudioSynthesizer.GenerateSaveSound(saveTheme);
            if (resolvedData.Length > 44)
            {
                _rawAudioTemplates["save"] = resolvedData;
                if (refreshScaled)
                {
                    UpdateSingleScaledBufferLocked("save", resolvedData, Volume * Volume);
                }
            }
            else
            {
                _rawAudioTemplates.Remove("save");
                _scaledAudioCache.Remove("save");
            }
        }

        private static byte[] LoadOrSynthesize(string dir, string file, string theme, int variant)
        {
            string fullPath = Path.Combine(dir, file);
            if (File.Exists(fullPath))
            {
                try
                {
                    byte[] data = File.ReadAllBytes(fullPath);
                    if (data.Length > 44) return data;
                }
                catch { }
            }

            return PcmAudioSynthesizer.Generate(theme, variant);
        }

        private void UpdateSingleScaledBufferLocked(string key, byte[] rawBytes, double gain)
        {
            _scaledAudioCache.TryGetValue(key, out byte[]? existingBuffer);
            PcmAudioSynthesizer.ApplyVolumeInto(rawBytes, gain, ref existingBuffer);
            if (existingBuffer != null)
            {
                _scaledAudioCache[key] = existingBuffer;
            }
        }

        private void RefreshScaledCacheLocked()
        {
            double gain = Volume * Volume;
            foreach (var kvp in _rawAudioTemplates)
            {
                UpdateSingleScaledBufferLocked(kvp.Key, kvp.Value, gain);
            }
        }

        public void Dispose()
        {
            lock (_waveOutLock)
            {
                if (_isDisposed) return;
                _isDisposed = true;
                _isBellWaveOutActive = false;

                _bellCleanupTimer?.Dispose();
                _bellCleanupTimer = null;

                if (_hWaveOut != IntPtr.Zero)
                {
                    waveOutReset(_hWaveOut);
                    CleanupCompletedBellBuffersLocked(forceAll: true);
                    waveOutClose(_hWaveOut);
                    _hWaveOut = IntPtr.Zero;
                }
            }
        }
    }
}
