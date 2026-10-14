using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace Journal.Services
{
    public class FontService
    {
        private static FontService? _instance;
        public static FontService Instance => _instance ??= new FontService();

        private List<string>? _cachedSystemFonts;
        private readonly object _lock = new();

        private static readonly string[] s_styleSuffixes =
        {
            " ExtraBold", " SemiBold", " SemiLight", " Bold", " Italic", " Light", " Black", " Medium", " Thin", " Regular"
        };

        /// <summary>
        /// 精选推荐字体（优先置顶）
        /// </summary>
        public static readonly List<string> FeaturedFonts = new()
        {
            "霞鹜文楷",
            "思源宋体",
            "Segoe UI Variable Text",
            "Microsoft YaHei UI",
            "Cascadia Code",
            "楷体",
            "仿宋",
            "黑体",
            "宋体"
        };

        /// <summary>
        /// 获取系统中安装的全部字体族名称（去重、按常用置顶排序）
        /// </summary>
        public List<string> GetInstalledFontFamilies()
        {
            lock (_lock)
            {
                if (_cachedSystemFonts != null && _cachedSystemFonts.Count > 0)
                {
                    return _cachedSystemFonts;
                }

                var fontSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 1. 先加入精选字体
                foreach (var f in FeaturedFonts)
                {
                    fontSet.Add(f);
                }

                // 2. 从注册表 HKLM 与 HKCU 扫描系统字体
                ScanFontsFromRegistryKey(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts", fontSet);
                ScanFontsFromRegistryKey(Registry.CurrentUser, @"Software\Microsoft\Windows NT\CurrentVersion\Fonts", fontSet);

                // 3. 构建排好序的字体列表（精选在前，其余拼音/字母排序）
                var featured = FeaturedFonts.Where(f => fontSet.Contains(f)).ToList();
                var remaining = fontSet.Where(f => !FeaturedFonts.Contains(f, StringComparer.OrdinalIgnoreCase))
                                       .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
                                       .ToList();

                _cachedSystemFonts = new List<string>(featured.Count + remaining.Count);
                _cachedSystemFonts.AddRange(featured);
                _cachedSystemFonts.AddRange(remaining);

                return _cachedSystemFonts;
            }
        }

        private static void ScanFontsFromRegistryKey(RegistryKey rootKey, string subKeyPath, HashSet<string> fontSet)
        {
            try
            {
                using var key = rootKey.OpenSubKey(subKeyPath);
                if (key == null) return;

                foreach (var valueName in key.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(valueName)) continue;

                    // 剥离后缀，如 "Arial (TrueType)", "Segoe UI Bold (TrueType)", "SimSun & NSimSun (TrueType)"
                    ReadOnlySpan<char> rawSpan = valueName.AsSpan();
                    int parenIdx = rawSpan.IndexOf('(');
                    if (parenIdx > 0)
                    {
                        rawSpan = rawSpan[..parenIdx].Trim();
                    }

                    string rawName = rawSpan.ToString();
                    var subNames = rawName.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var name in subNames)
                    {
                        string cleaned = StripStyleSuffixes(name);
                        if (cleaned.Length > 1)
                        {
                            fontSet.Add(cleaned);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FontService] Registry scan warning: {ex.Message}");
            }
        }

        private static string StripStyleSuffixes(string fontName)
        {
            ReadOnlySpan<char> span = fontName.AsSpan().Trim();
            bool strippedAny;
            do
            {
                strippedAny = false;
                foreach (var style in s_styleSuffixes)
                {
                    if (span.EndsWith(style.AsSpan(), StringComparison.OrdinalIgnoreCase))
                    {
                        span = span[..^style.Length].TrimEnd();
                        strippedAny = true;
                        break;
                    }
                }
            } while (strippedAny && span.Length > 0);

            return span.ToString();
        }
    }
}
