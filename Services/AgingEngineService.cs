using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Journal.Models;

namespace Journal.Services
{
    /// <summary>
    /// “岁月沉淀”（纸张底色与字迹风化）核心渲染引擎服务实现
    /// </summary>
    public sealed class AgingEngineService : IAgingEngineService
    {
        private static AgingEngineService? s_instance;
        public static AgingEngineService Instance => s_instance ??= new AgingEngineService();

        private readonly IAgingCacheService _cacheService;

        public IAgingCacheService CacheService => _cacheService;

        public event EventHandler<DateTime>? CurrentViewingDateChanged;
        public DateTime CurrentViewingDate { get; private set; } = DateTime.Today;

        public void SetCurrentViewingDate(DateTime date)
        {
            DateTime normalized = date.Date;
            if (CurrentViewingDate.Date != normalized)
            {
                CurrentViewingDate = normalized;
                CurrentViewingDateChanged?.Invoke(this, normalized);
            }
        }

        public AgingEngineService(IAgingCacheService? cacheService = null)
        {
            _cacheService = cacheService ?? AgingCacheService.Instance;
        }

        public double CalculateAgingIntensity(DateTime entryDate, double userStrengthSetting, bool isAgingEnabled)
        {
            return AgingModel.CalculateIntensity(entryDate, userStrengthSetting, isAgingEnabled);
        }

        public AgingCalculationResult GetAgingDetails(DateTime entryDate, double userStrengthSetting, bool isAgingEnabled)
        {
            return AgingModel.Calculate(entryDate, userStrengthSetting, isAgingEnabled);
        }

        public async Task<string> GetPreprocessedPaperTexturePathAsync(string? sourcePath, bool isDarkMode)
        {
            string resolvedPath = ResolveTexturePath(sourcePath, isPaper: true);
            if (!File.Exists(resolvedPath))
            {
                throw new FileNotFoundException($"Paper texture file not found: {sourcePath}", sourcePath ?? string.Empty);
            }

            string mode = isDarkMode ? "inv" : "gray";
            return await _cacheService.GetOrCreateCachedFileAsync(resolvedPath, mode,
                (src, dst) => Task.Run(() => ProcessTextureAsync(src, dst, invert: isDarkMode, autoLevels: true)));
        }

        public async Task<string> GetPreprocessedInkTexturePathAsync(string? sourcePath)
        {
            return await GetPreprocessedInkTexturePathAsync(sourcePath, isDarkMode: false);
        }

        public async Task<string> GetPreprocessedInkTexturePathAsync(string? sourcePath, bool isDarkMode)
        {
            string resolvedPath = ResolveTexturePath(sourcePath, isPaper: false);
            if (!File.Exists(resolvedPath))
            {
                throw new FileNotFoundException($"Ink texture file not found: {sourcePath}", sourcePath ?? string.Empty);
            }

            string mode = isDarkMode ? "inv" : "gray";
            return await _cacheService.GetOrCreateCachedFileAsync(resolvedPath, mode,
                (src, dst) => Task.Run(() => ProcessTextureAsync(src, dst, invert: isDarkMode, autoLevels: false)));
        }

        // The grayscale cache remains the canonical preprocessing result. The second,
        // white-on-alpha cache is only a compositor input: its alpha carries wear marks.
        public async Task<string> GetInkWeatheringMaskPathAsync(string? sourcePath)
        {
            string grayscalePath = await GetPreprocessedInkTexturePathAsync(sourcePath);
            return await _cacheService.GetOrCreateCachedFileAsync(grayscalePath, "wearmask-v4",
                (src, dst) => Task.Run(() => ProcessInkWeatheringMaskAsync(src, dst)));
        }

        private static async Task ProcessInkWeatheringMaskAsync(string inputPath, string outputPath)
        {
            using var inputFile = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var inputStream = inputFile.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(inputStream);
            var pixelData = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied, new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            byte[] pixels = pixelData.DetachPixelData();
            // Use source alpha for transparent drawings. For opaque images, treat
            // the minority contrast marks against the dominant light/dark base as wear.
            int[] histogram = new int[256];
            int[] alphaHistogram = new int[256];
            int opaquePixels = 0;
            int transparentPixels = 0;
            int highestAlpha = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int sourceAlpha = pixels[i + 3];
                if (sourceAlpha < 255) transparentPixels++;
                alphaHistogram[sourceAlpha]++;
                highestAlpha = Math.Max(highestAlpha, sourceAlpha);
                if (sourceAlpha == 0) continue;
                int gray = Math.Min(255, (pixels[i] * 255 + sourceAlpha / 2) / sourceAlpha);
                histogram[gray]++;
                opaquePixels++;
            }
            bool useSourceAlpha = transparentPixels > pixels.Length / 400;
            int lowPercentileCount = Math.Max(1, opaquePixels / 5);
            int lowExtremeCount = Math.Max(1, opaquePixels / 100);
            int medianCount = Math.Max(1, opaquePixels / 2);
            int highPercentileCount = Math.Max(1, (int)((long)opaquePixels * 4 / 5));
            int highExtremeCount = Math.Max(1, (int)((long)opaquePixels * 99 / 100));
            int cumulative = 0;
            int lowPercentile = 0;
            int lowExtreme = 0;
            int median = 0;
            int highPercentile = 255;
            int highExtreme = 255;
            bool lowFound = false;
            bool lowExtremeFound = false;
            bool medianFound = false;
            bool highFound = false;
            for (int gray = 0; gray < histogram.Length; gray++)
            {
                cumulative += histogram[gray];
                if (!lowExtremeFound && cumulative >= lowExtremeCount)
                {
                    lowExtreme = gray;
                    lowExtremeFound = true;
                }
                if (!lowFound && cumulative >= lowPercentileCount)
                {
                    lowPercentile = gray;
                    lowFound = true;
                }
                if (!medianFound && cumulative >= medianCount)
                {
                    median = gray;
                    medianFound = true;
                }
                if (!highFound && cumulative >= highPercentileCount)
                {
                    highPercentile = gray;
                    highFound = true;
                }
                if (cumulative >= highExtremeCount)
                {
                    highExtreme = gray;
                    break;
                }
            }
            bool darkMarks = median >= 128;
            int cutoff = darkMarks
                ? Math.Min(lowPercentile + 15, median - 5)
                : Math.Max(highPercentile - 15, median + 5);
            cutoff = Math.Clamp(cutoff, 0, 255);
            int contrastRange = darkMarks
                ? Math.Max(1, cutoff - lowExtreme)
                : Math.Max(1, highExtreme - cutoff);

            // Alpha-only artwork can also be faint. Let 100% aging use the
            // texture's full alpha range, while keeping fully clear gaps clear.
            int alphaCount = 0;
            int strongAlpha = highestAlpha;
            int highAlphaCount = Math.Max(1, (int)((long)(pixels.Length / 4) * 99 / 100));
            for (int alpha = 0; alpha < alphaHistogram.Length; alpha++)
            {
                alphaCount += alphaHistogram[alpha];
                if (alphaCount >= highAlphaCount)
                {
                    if (alpha > 0) strongAlpha = alpha;
                    break;
                }
            }
            byte[] wearByGray = new byte[256];
            byte[] wearByAlpha = new byte[256];
            for (int value = 0; value < 256; value++)
            {
                int contrast = darkMarks ? cutoff - value : value - cutoff;
                double grayStrength = Math.Clamp((double)Math.Max(0, contrast) / contrastRange, 0.0, 1.0);
                double alphaStrength = strongAlpha == 0 ? 0.0
                    : Math.Clamp((double)value / strongAlpha, 0.0, 1.0);
                wearByGray[value] = (byte)Math.Round(255.0 * Math.Pow(grayStrength, 0.75));
                wearByAlpha[value] = (byte)Math.Round(255.0 * Math.Pow(alphaStrength, 0.75));
            }
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int sourceAlpha = pixels[i + 3];
                int gray = sourceAlpha == 0 ? 255
                    : Math.Min(255, (pixels[i] * 255 + sourceAlpha / 2) / sourceAlpha);
                // The lookup curve preserves fine marks without doing a power
                // calculation for every pixel of a large user texture.
                byte alpha = useSourceAlpha ? wearByAlpha[sourceAlpha]
                    : (byte)((wearByGray[gray] * sourceAlpha + 127) / 255);
                pixels[i] = alpha;
                pixels[i + 1] = alpha;
                pixels[i + 2] = alpha;
                pixels[i + 3] = alpha;
            }

            using var outputFile = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using var outputStream = outputFile.AsRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outputStream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                decoder.OrientedPixelWidth, decoder.OrientedPixelHeight, decoder.DpiX, decoder.DpiY, pixels);
            await encoder.FlushAsync();
        }

        public static async Task ProcessTextureAsync(string inputPath, string outputPath, bool invert, bool autoLevels)
        {
            using var fileStream = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var randomAccessStream = fileStream.AsRandomAccessStream();

            var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
            var pixelDataProvider = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);

            byte[] pixels = pixelDataProvider.DetachPixelData();
            uint width = decoder.OrientedPixelWidth;
            uint height = decoder.OrientedPixelHeight;
            double dpiX = decoder.DpiX;
            double dpiY = decoder.DpiY;

            ProcessPixelData(pixels, invert, autoLevels);

            // 保存输出为 PNG 缓存文件
            string outDir = Path.GetDirectoryName(outputPath)!;
            if (!Directory.Exists(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            using var outStream = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using var outRandomStream = outStream.AsRandomAccessStream();

            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outRandomStream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, width, height, dpiX, dpiY, pixels);
            await encoder.FlushAsync();
        }

        private static void ProcessPixelData(byte[] pixels, bool invert, bool autoLevels)
        {
            float scale = 1.0f;
            if (autoLevels)
            {
                int maxSeen = 0;
                int step = Math.Max(4, (pixels.Length / 4000) * 4);
                for (int i = 0; i < pixels.Length; i += step)
                {
                    int alpha = pixels[i + 3];
                    if (alpha == 0) continue;
                    int luma = (19595 * pixels[i + 2] + 38469 * pixels[i + 1] + 7472 * pixels[i] + 32768) >> 16;
                    luma = Math.Min(255, (luma * 255 + alpha / 2) / alpha);
                    maxSeen = Math.Max(maxSeen, luma);
                }
                if (maxSeen >= 180 && maxSeen < 254) scale = 255.0f / maxSeen;
            }

            for (int i = 0; i < pixels.Length; i += 4)
            {
                int alpha = pixels[i + 3];
                if (alpha == 0)
                {
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = 0;
                    continue;
                }
                int luma = (19595 * pixels[i + 2] + 38469 * pixels[i + 1] + 7472 * pixels[i] + 32768) >> 16;
                luma = Math.Min(255, (luma * 255 + alpha / 2) / alpha);
                luma = Math.Min(255, (int)(luma * scale));
                int value = invert ? 255 - luma : luma;
                byte premultiplied = (byte)((value * alpha + 127) / 255);
                pixels[i] = pixels[i + 1] = pixels[i + 2] = premultiplied;
            }
        }
        public static string ResolveTexturePath(string? customPath, bool isPaper)
        {
            if (!string.IsNullOrWhiteSpace(customPath))
            {
                if (File.Exists(customPath))
                {
                    return Path.GetFullPath(customPath);
                }

                try
                {
                    string storageDir = AgingTextureStorageService.Instance.StorageDirectoryPath;
                    string safeFileName = Path.GetFileName(customPath);
                    string inStorage = Path.Combine(storageDir, safeFileName);
                    if (File.Exists(inStorage))
                    {
                        return Path.GetFullPath(inStorage);
                    }
                }
                catch
                {
                    // 忽略路径解析异常，平滑降级
                }
            }

            string defaultFileName = isPaper ? AgingConstants.DefaultPaperTextureFileName : AgingConstants.DefaultInkTextureFileName;

            // 1. Assets/Textures/
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string inAssets = Path.Combine(baseDir, "Assets", "Textures", defaultFileName);
            if (File.Exists(inAssets)) return Path.GetFullPath(inAssets);

            return customPath ?? string.Empty;
        }
    }
}
