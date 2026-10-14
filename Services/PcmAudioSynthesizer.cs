using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Journal.Services
{
    /// <summary>
    /// 内置高保真物理声学程序化 PCM WAV 合成器：
    /// 纯数学建模生成复古打字机、Cherry青轴、Cherry茶轴与回车黄铜铃铛音效，
    /// 实现零外部资源强依赖、零文件缺失异常、100% 独立可靠。
    /// </summary>
    public static class PcmAudioSynthesizer
    {
        public const int SampleRate = 44100;
        public const short Channels = 1; // Mono
        public const short BitsPerSample = 16;

        /// <summary>
        /// 普通击键变体总数（click1.wav ~ click10.wav）
        /// </summary>
        public const int ClickVariantCount = 10;

        /// <summary>
        /// 特殊大键（空格/退格）变体总数（special.wav, special2.wav, special3.wav）
        /// </summary>
        public const int SpecialVariantCount = 3;

        // 变体编号映射表：0~2 为 click1~3, 3 为 special1, 4 为 enter, 5~11 为 click4~10, 12~13 为 special2~3
        private static readonly (string FileName, int VariantCode)[] ThemeAssetEntries =
        {
            ("click1.wav", 0),
            ("click2.wav", 1),
            ("click3.wav", 2),
            ("click4.wav", 5),
            ("click5.wav", 6),
            ("click6.wav", 7),
            ("click7.wav", 8),
            ("click8.wav", 9),
            ("click9.wav", 10),
            ("click10.wav", 11),
            ("special.wav", 3),
            ("special2.wav", 12),
            ("special3.wav", 13),
            ("enter.wav", 4)
        };

        // 模拟真实键盘不同行位（R1~R4）腔体共鸣、手指敲击力度与轴心阻尼微散度表
        private static readonly double[] ClickPitchFactors = { 1.000, 0.972, 1.024, 0.955, 1.035, 0.984, 1.015, 0.964, 1.028, 0.990 };
        private static readonly double[] ClickDecayFactors = { 1.000, 0.960, 1.040, 0.945, 1.055, 0.975, 1.025, 0.965, 1.035, 0.985 };
        private static readonly double[] ClickGainFactors  = { 1.000, 0.985, 0.990, 0.975, 0.980, 0.995, 0.990, 0.980, 0.985, 0.995 };

        private static readonly double[] SpecialPitchFactors = { 1.000, 0.965, 1.032 };
        private static readonly double[] SpecialDecayFactors = { 1.000, 0.955, 1.045 };
        private static readonly double[] SpecialGainFactors  = { 1.000, 0.985, 0.990 };

        private static bool IsSpecialVariant(int variant) => variant is 3 or 12 or 13;

        private static (double Pitch, double Decay, double Gain) GetMicroVariation(int variant)
        {
            if (variant == 4) return (1.0, 1.0, 1.0);
            if (IsSpecialVariant(variant))
            {
                int sIdx = variant switch { 12 => 1, 13 => 2, _ => 0 };
                return (SpecialPitchFactors[sIdx], SpecialDecayFactors[sIdx], SpecialGainFactors[sIdx]);
            }
            int cIdx = variant switch
            {
                0 => 0,
                1 => 1,
                2 => 2,
                >= 5 and <= 11 => variant - 2,
                _ => Math.Abs(variant) % ClickPitchFactors.Length
            };
            return (ClickPitchFactors[cIdx], ClickDecayFactors[cIdx], ClickGainFactors[cIdx]);
        }

        /// <summary>
        /// 根据主题与变体编号生成未压缩 16-bit PCM WAV 字节数组
        /// </summary>
        public static byte[] Generate(string theme, int variant)
        {
            return (theme ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "cherryblue" => GenerateCherryBlue(variant),
                "cherrybrown" => GenerateCherryBrown(variant),
                "cherryred" or "iphonekeyboard" => GenerateCherryRed(variant),
                "mahjongthock" or "hifimahjong" or "mahjong" => GenerateMahjongThock(variant),
                "raindropslate" or "raindrop" => GenerateRaindropSlate(variant),
                "oldmembrane" or "membrane" or "vintagemembrane" => GenerateOldMembrane(variant),
                "woodblock" or "woodenfish" or "datamatrixsfx" => GenerateWoodBlock(variant),
                "vintagetypewriterpro" or "classictypewriterreal" => GenerateTypewriter(variant),
                _ => GenerateTypewriter(variant)
            };
        }

        /// <summary>
        /// 根据手动保存音效预设生成 16-bit PCM WAV 字节数组（当外部实录文件缺失时的程序化兜底）
        /// </summary>
        public static byte[] GenerateSaveSound(string saveTheme)
        {
            string normalized = (saveTheme ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(normalized) || normalized == "none")
            {
                return Array.Empty<byte>();
            }
            return GenerateEnterBell();
        }

        /// <summary>
        /// 生成独立的黄铜回车/保存长铃音效（C7 2093Hz + 谐波指数衰减，约 520ms）
        /// </summary>
        public static byte[] GenerateEnterBell()
        {
            int numSamples = (int)(SampleRate * 0.52);
            short[] samples = new short[numSamples];

            for (int i = 0; i < numSamples; i++)
            {
                double t = (double)i / SampleRate;
                double strike = (t < 0.008) ? (Math.Sin(2.0 * Math.PI * 4500.0 * t) * (1.0 - t / 0.008) * 0.35) : 0.0;

                double decay = Math.Exp(-7.5 * t);
                double tone1 = Math.Sin(2.0 * Math.PI * 2093.0 * t) * 0.70;
                double tone2 = Math.Sin(2.0 * Math.PI * 4186.0 * t) * 0.25;
                double tone3 = Math.Sin(2.0 * Math.PI * 6279.0 * t) * 0.10;

                double bell = (tone1 + tone2 + tone3) * decay;
                double mixed = strike + bell;

                samples[i] = ClampToShort(mixed * 24000.0);
            }

            return BuildWavFile(samples);
        }

        private static byte[] GenerateTypewriter(int variant)
        {
            if (variant == 4)
            {
                int numSamples = (int)(SampleRate * 0.48);
                short[] samples = new short[numSamples];
                for (int i = 0; i < numSamples; i++)
                {
                    double t = (double)i / SampleRate;
                    double rack = 0.0;
                    if (t < 0.045)
                    {
                        double env = 1.0 - t / 0.045;
                        rack = (Math.Sin(2.0 * Math.PI * 1400.0 * t) * 0.5 + Math.Sin(2.0 * Math.PI * 450.0 * t) * 0.5) * env * 0.6;
                    }

                    double bell = 0.0;
                    if (t >= 0.015)
                    {
                        double bt = t - 0.015;
                        double decay = Math.Exp(-8.0 * bt);
                        bell = (Math.Sin(2.0 * Math.PI * 2093.0 * bt) * 0.75 + Math.Sin(2.0 * Math.PI * 4186.0 * bt) * 0.25) * decay;
                    }

                    samples[i] = ClampToShort((rack + bell) * 22000.0);
                }
                return BuildWavFile(samples);
            }

            bool isSpecial = IsSpecialVariant(variant);
            var (pitchMul, decayMul, gainMul) = GetMicroVariation(variant);

            double duration = isSpecial ? 0.048 : 0.038;
            int total = (int)(SampleRate * duration);
            short[] clickSamples = new short[total];
            var rng = new Random(variant * 101 + 17);

            double baseFreq = (isSpecial ? 1980.0 : 2350.0) * pitchMul;
            double platenFreq = 440.0 * (0.95 + 0.05 * pitchMul);
            double filteredNoise = 0.0;
            int fadeOutSamples = (int)(SampleRate * 0.005);

            for (int i = 0; i < total; i++)
            {
                double t = (double)i / SampleRate;
                double attack = Math.Min(1.0, (i + 1) / 30.0);
                double tail = (i >= total - fadeOutSamples)
                    ? 0.5 * (1.0 + Math.Cos(Math.PI * (i - (total - fadeOutSamples)) / fadeOutSamples))
                    : 1.0;
                double env = Math.Exp(-125.0 * decayMul * t) * attack * tail;

                double metal = Math.Sin(2.0 * Math.PI * baseFreq * t) * 0.48;
                double rawNoise = rng.NextDouble() * 2.0 - 1.0;
                filteredNoise = filteredNoise * 0.62 + rawNoise * 0.38;
                double noise = filteredNoise * Math.Exp(-180.0 * decayMul * t) * 0.30;
                double platen = Math.Sin(2.0 * Math.PI * platenFreq * t) * 0.52 * Math.Exp(-75.0 * decayMul * t);

                double wave = (metal + noise + platen) * env * gainMul;
                clickSamples[i] = ClampToShort(wave * 21500.0);
            }

            return BuildWavFile(clickSamples);
        }

        private static byte[] GenerateCherryBlue(int variant)
        {
            bool isSpecial = IsSpecialVariant(variant);
            var (pitchMul, decayMul, gainMul) = GetMicroVariation(variant);

            double duration = variant == 4 ? 0.042 : (isSpecial ? 0.036 : 0.028);
            int total = (int)(SampleRate * duration);
            short[] samples = new short[total];
            var rng = new Random(variant * 77 + 13);

            double snapFreq = (variant == 4 ? 3450.0 : (isSpecial ? 3300.0 : 3650.0)) * pitchMul;
            double bottomFreq = 950.0 * (0.93 + 0.07 * pitchMul);

            for (int i = 0; i < total; i++)
            {
                double t = (double)i / SampleRate;
                double clickSnap = 0.0;
                if (t < 0.009)
                {
                    double snapEnv = 1.0 - t / 0.009;
                    clickSnap = Math.Sin(2.0 * Math.PI * snapFreq * t) * snapEnv;
                }

                double bottomOut = 0.0;
                if (t >= 0.004)
                {
                    double bt = t - 0.004;
                    double env = Math.Exp(-110.0 * decayMul * bt);
                    bottomOut = (Math.Sin(2.0 * Math.PI * bottomFreq * bt) * 0.7 + (rng.NextDouble() * 2.0 - 1.0) * 0.2) * env;
                }

                double wave = (clickSnap * 0.65 + bottomOut * 0.55) * gainMul;
                samples[i] = ClampToShort(wave * 26000.0);
            }

            return BuildWavFile(samples);
        }

        private static byte[] GenerateCherryBrown(int variant)
        {
            bool isSpecial = IsSpecialVariant(variant);
            var (pitchMul, decayMul, gainMul) = GetMicroVariation(variant);

            double duration = variant == 4 ? 0.040 : (isSpecial ? 0.035 : 0.026);
            int total = (int)(SampleRate * duration);
            short[] samples = new short[total];
            var rng = new Random(variant * 61 + 31);

            double thockFreq = (variant == 4 ? 590.0 : (isSpecial ? 520.0 : 620.0)) * pitchMul;

            for (int i = 0; i < total; i++)
            {
                double t = (double)i / SampleRate;
                double bump = 0.0;
                if (t < 0.007)
                {
                    bump = Math.Sin(2.0 * Math.PI * 1350.0 * pitchMul * t) * (1.0 - t / 0.007) * 0.35;
                }

                double thock = Math.Sin(2.0 * Math.PI * thockFreq * t) * Math.Exp(-85.0 * decayMul * t) * 0.75;
                double plastic = (rng.NextDouble() * 2.0 - 1.0) * Math.Exp(-160.0 * decayMul * t) * 0.25;

                double wave = (bump + thock + plastic) * gainMul;
                samples[i] = ClampToShort(wave * 24000.0);
            }

            return BuildWavFile(samples);
        }

        private static byte[] GenerateCherryRed(int variant)
        {
            bool isSpecial = IsSpecialVariant(variant);
            var (pitchMul, decayMul, gainMul) = GetMicroVariation(variant);

            double duration = variant == 4 ? 0.038 : (isSpecial ? 0.032 : 0.026);
            int total = (int)(SampleRate * duration);
            short[] samples = new short[total];
            var rng = new Random(variant * 47 + 19);

            double bottomFreq = (variant == 4 ? 465.0 : (isSpecial ? 430.0 : 480.0)) * pitchMul;
            double filteredGlide = 0.0;
            int fadeOutSamples = (int)(SampleRate * 0.006);

            for (int i = 0; i < total; i++)
            {
                double t = (double)i / SampleRate;
                double attack = Math.Min(1.0, (i + 1) / 36.0);
                double tail = (i >= total - fadeOutSamples)
                    ? 0.5 * (1.0 + Math.Cos(Math.PI * (i - (total - fadeOutSamples)) / fadeOutSamples))
                    : 1.0;

                double glide = 0.0;
                if (t < 0.006)
                {
                    double raw = rng.NextDouble() * 2.0 - 1.0;
                    filteredGlide = filteredGlide * 0.80 + raw * 0.20;
                    glide = filteredGlide * (1.0 - t / 0.006) * 0.06;
                }

                double bottom = Math.Sin(2.0 * Math.PI * bottomFreq * t) * Math.Exp(-115.0 * decayMul * t) * 0.72;
                double rebound = Math.Sin(2.0 * Math.PI * 180.0 * pitchMul * t) * Math.Exp(-95.0 * decayMul * t) * 0.22;

                double wave = (glide + bottom + rebound) * attack * tail * gainMul;
                samples[i] = ClampToShort(wave * 21000.0);
            }

            return BuildWavFile(samples);
        }

        private static byte[] GenerateMahjongThock(int variant)
        {
            bool isSpecial = IsSpecialVariant(variant);
            var (pitchMul, decayMul, gainMul) = GetMicroVariation(variant);

            double duration = variant == 4 ? 0.045 : (isSpecial ? 0.036 : 0.028);
            int total = (int)(SampleRate * duration);
            short[] samples = new short[total];
            var rng = new Random(variant * 89 + 53);

            double snapFreq = (variant == 4 ? 2000.0 : (isSpecial ? 1820.0 : 2050.0)) * pitchMul;
            double thockFreq = (variant == 4 ? 395.0 : (isSpecial ? 350.0 : 410.0)) * pitchMul;

            for (int i = 0; i < total; i++)
            {
                double t = (double)i / SampleRate;
                double snap = 0.0;
                if (t < 0.005)
                {
                    snap = Math.Sin(2.0 * Math.PI * snapFreq * t) * (1.0 - t / 0.005) * 0.85;
                }

                double thock = Math.Sin(2.0 * Math.PI * thockFreq * t) * Math.Exp(-65.0 * decayMul * t) * 0.75;
                double poly = Math.Sin(2.0 * Math.PI * (thockFreq * 2.8) * t) * Math.Exp(-110.0 * decayMul * t) * 0.25;
                double air = (rng.NextDouble() * 2.0 - 1.0) * Math.Exp(-200.0 * decayMul * t) * 0.15;

                double wave = (snap + thock + poly + air) * gainMul;
                samples[i] = ClampToShort(wave * 26000.0);
            }

            return BuildWavFile(samples);
        }

        private static byte[] GenerateRaindropSlate(int variant)
        {
            bool isSpecial = IsSpecialVariant(variant);
            var (pitchMul, decayMul, gainMul) = GetMicroVariation(variant);

            double duration = variant == 4 ? 0.065 : (isSpecial ? 0.048 : 0.038);
            int total = (int)(SampleRate * duration);
            short[] samples = new short[total];

            double startFreq = (variant == 4 ? 1380.0 : (isSpecial ? 1280.0 : 1420.0)) * pitchMul;

            for (int i = 0; i < total; i++)
            {
                double t = (double)i / SampleRate;
                double freqMod = startFreq + 750.0 * pitchMul * Math.Pow(t / duration, 0.45);
                double plop = Math.Sin(2.0 * Math.PI * freqMod * t) * Math.Exp(-70.0 * decayMul * t);
                double stone = Math.Sin(2.0 * Math.PI * 620.0 * pitchMul * t) * Math.Exp(-45.0 * decayMul * t) * 0.25;

                double drop2 = 0.0;
                if (variant == 4 && t >= 0.018)
                {
                    double t2 = t - 0.018;
                    double freq2 = 1650.0 + 600.0 * Math.Pow(t2 / 0.045, 0.5);
                    drop2 = Math.Sin(2.0 * Math.PI * freq2 * t2) * Math.Exp(-75.0 * t2) * 0.65;
                }

                double wave = (plop * 0.75 + stone * 0.25 + drop2) * gainMul;
                samples[i] = ClampToShort(wave * 23000.0);
            }

            return BuildWavFile(samples);
        }

        private static byte[] GenerateOldMembrane(int variant)
        {
            bool isSpecial = IsSpecialVariant(variant);
            var (pitchMul, decayMul, gainMul) = GetMicroVariation(variant);

            double duration = variant == 4 ? 0.042 : (isSpecial ? 0.035 : 0.026);
            int total = (int)(SampleRate * duration);
            short[] samples = new short[total];
            var rng = new Random(variant * 51 + 7);

            double thudFreq = (variant == 4 ? 320.0 : (isSpecial ? 290.0 : 330.0)) * pitchMul;

            for (int i = 0; i < total; i++)
            {
                double t = (double)i / SampleRate;
                double dome = Math.Sin(2.0 * Math.PI * thudFreq * t) * Math.Exp(-95.0 * decayMul * t) * 0.80;
                double friction = (rng.NextDouble() * 2.0 - 1.0) * Math.Exp(-140.0 * decayMul * t) * 0.20;
                double wire = variant == 4 ? (Math.Sin(2.0 * Math.PI * 240.0 * t) * Math.Exp(-70.0 * t) * 0.25) : 0.0;

                double wave = (dome + friction + wire) * gainMul;
                samples[i] = ClampToShort(wave * 24000.0);
            }

            return BuildWavFile(samples);
        }

        private static byte[] GenerateWoodBlock(int variant)
        {
            bool isSpecial = IsSpecialVariant(variant);
            var (pitchMul, decayMul, gainMul) = GetMicroVariation(variant);

            double duration = variant == 4 ? 0.060 : (isSpecial ? 0.045 : 0.035);
            int total = (int)(SampleRate * duration);
            short[] samples = new short[total];

            double baseFreq = (variant == 4 ? 650.0 : (isSpecial ? 730.0 : 820.0)) * pitchMul;

            for (int i = 0; i < total; i++)
            {
                double t = (double)i / SampleRate;
                double decay = Math.Exp(-55.0 * decayMul * t);
                double tone1 = Math.Sin(2.0 * Math.PI * baseFreq * t) * 0.75;
                double tone2 = Math.Sin(2.0 * Math.PI * (baseFreq * 2.0) * t) * 0.25;
                double woodHit = (t < 0.005) ? (Math.Sin(2.0 * Math.PI * 3200.0 * pitchMul * t) * (1.0 - t / 0.005) * 0.30) : 0.0;

                double wave = ((tone1 + tone2) * decay + woodHit) * gainMul;
                samples[i] = ClampToShort(wave * 25000.0);
            }

            return BuildWavFile(samples);
        }

        /// <summary>
        /// 对 16-bit PCM WAV 应用感知音量缩放并写入目标 POH 固定缓冲区（长度一致时 100% 零重分配复用）
        /// </summary>
        public static void ApplyVolumeInto(byte[] wavBytes, double gain, ref byte[]? targetBuffer)
        {
            if (wavBytes == null || wavBytes.Length <= 44)
            {
                targetBuffer = Array.Empty<byte>();
                return;
            }

            if (targetBuffer == null || targetBuffer.Length != wavBytes.Length)
            {
                // 分配于固定对象堆（POH），保证 WinMM PlaySound(SND_MEMORY | SND_ASYNC) 异步播放期间地址绝对固定
                targetBuffer = GC.AllocateArray<byte>(wavBytes.Length, pinned: true);
                Buffer.BlockCopy(wavBytes, 0, targetBuffer, 0, 44);
            }

            ReadOnlySpan<short> srcSamples = MemoryMarshal.Cast<byte, short>(wavBytes.AsSpan(44));
            Span<short> dstSamples = MemoryMarshal.Cast<byte, short>(targetBuffer.AsSpan(44));

            for (int i = 0; i < srcSamples.Length; i++)
            {
                dstSamples[i] = ClampToShort(srcSamples[i] * gain);
            }
        }

        /// <summary>
        /// 对 16-bit PCM WAV 应用感知音量缩放（返回新的固定缓冲区）
        /// </summary>
        public static byte[] ApplyVolume(byte[] wavBytes, double gain)
        {
            byte[]? result = null;
            ApplyVolumeInto(wavBytes, gain, ref result);
            return result ?? Array.Empty<byte>();
        }

        /// <summary>
        /// 确保 Assets/Audio/ 目录中写入完整的 16-bit PCM WAV 资源文件（含 10 变体普通键、3 变体大键与保存音效）
        /// </summary>
        public static void EnsureAudioAssetsWritten(string baseAssetsDir)
        {
            try
            {
                string audioDir = Path.Combine(baseAssetsDir, "Audio");
                string[] themes =
                {
                    "Typewriter", "CherryBlue", "CherryBrown",
                    "CherryRed", "MahjongThock", "RaindropSlate",
                    "OldMembrane", "WoodBlock",
                    "VintageTypewriterPro", "ClassicTypewriterReal",
                    "IPhoneKeyboard", "DataMatrixSfx"
                };

                foreach (var theme in themes)
                {
                    string themeDir = Path.Combine(audioDir, theme);
                    if (!Directory.Exists(themeDir))
                    {
                        Directory.CreateDirectory(themeDir);
                    }

                    foreach (var (fileName, variantCode) in ThemeAssetEntries)
                    {
                        string filePath = Path.Combine(themeDir, fileName);
                        if (!File.Exists(filePath))
                        {
                            byte[] wav = Generate(theme, variantCode);
                            File.WriteAllBytes(filePath, wav);
                        }
                    }
                }

                string saveDir = Path.Combine(audioDir, "Save");
                if (!Directory.Exists(saveDir))
                {
                    Directory.CreateDirectory(saveDir);
                }
                string saveBellPath = Path.Combine(saveDir, "TypewriterBell.wav");
                if (!File.Exists(saveBellPath))
                {
                    File.WriteAllBytes(saveBellPath, GenerateSaveSound("TypewriterBell"));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[EnsureAudioAssetsWritten] Warning: {ex.Message}");
            }
        }

        /// <summary>
        /// 构建标准的未压缩 16-bit 44.1kHz 单声道 RIFF WAV 字节流（零 BinaryWriter 循环开销）
        /// </summary>
        private static byte[] BuildWavFile(short[] samples)
        {
            int subChunk2Size = samples.Length * sizeof(short);
            int chunkSize = 36 + subChunk2Size;

            byte[] wav = new byte[44 + subChunk2Size];
            Span<byte> header = wav.AsSpan(0, 44);

            Encoding.ASCII.GetBytes("RIFF", header[0..4]);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..8], chunkSize);
            Encoding.ASCII.GetBytes("WAVE", header[8..12]);

            Encoding.ASCII.GetBytes("fmt ", header[12..16]);
            BinaryPrimitives.WriteInt32LittleEndian(header[16..20], 16);
            BinaryPrimitives.WriteInt16LittleEndian(header[20..22], Channels);
            BinaryPrimitives.WriteInt16LittleEndian(header[22..24], Channels);
            BinaryPrimitives.WriteInt32LittleEndian(header[24..28], SampleRate);
            BinaryPrimitives.WriteInt32LittleEndian(header[28..32], SampleRate * Channels * BitsPerSample / 8);
            BinaryPrimitives.WriteInt16LittleEndian(header[32..34], (short)(Channels * BitsPerSample / 8));
            BinaryPrimitives.WriteInt16LittleEndian(header[34..36], BitsPerSample);

            Encoding.ASCII.GetBytes("data", header[36..40]);
            BinaryPrimitives.WriteInt32LittleEndian(header[40..44], subChunk2Size);

            Buffer.BlockCopy(samples, 0, wav, 44, subChunk2Size);
            return wav;
        }

        private static short ClampToShort(double value)
        {
            if (value > 32767.0) return 32767;
            if (value < -32768.0) return -32768;
            return (short)Math.Round(value);
        }
    }
}
