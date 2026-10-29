using System.Text.RegularExpressions;

namespace Journal.Services
{
    /// <summary>
    /// 统一的中日韩（CJK）与西文分词/字符判定工具，确保字数统计与词云分词口径完全一致
    /// </summary>
    public static class CjkTextHelper
    {
        /// <summary>
        /// 匹配方括号标签（如时间戳 [2026-03-31 20:00:00] 或 [20:00:00]）
        /// </summary>
        public static readonly Regex BracketTagRegex = new(@"\[[^\]]+\]", RegexOptions.Compiled);

        /// <summary>
        /// 匹配连续英文/数字单词
        /// </summary>
        public static readonly Regex EnglishWordRegex = new(@"\b[a-zA-Z0-9_-]+\b", RegexOptions.Compiled);

        /// <summary>
        /// 匹配纯英文单词（至少 2 个字母，用于词云提取）
        /// </summary>
        public static readonly Regex EnglishAlphaWordRegex = new(@"\b[a-zA-Z]{2,}\b", RegexOptions.Compiled);

        /// <summary>
        /// 匹配非 CJK 表意文字/假名/谚文的分隔符（覆盖基本汉字、扩展 A 区、兼容汉字、日文假名与韩文谚文）
        /// </summary>
        public static readonly Regex NonCjkSplitRegex = new(
            @"[^\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff\u3040-\u30ff\uac00-\ud7af]+",
            RegexOptions.Compiled);

        /// <summary>
        /// 判定字符是否属于中日韩（CJK）统计范畴（基本汉字、扩展 A 区、兼容表意文字、日文假名、韩文谚文）
        /// </summary>
        public static bool IsCjkCharacter(char c)
        {
            return (c >= 0x4E00 && c <= 0x9FFF) ||
                   (c >= 0x3400 && c <= 0x4DBF) ||
                   (c >= 0xF900 && c <= 0xFAFF) ||
                   (c >= 0x3040 && c <= 0x30FF) ||
                   (c >= 0xAC00 && c <= 0xD7AF);
        }

        /// <summary>
        /// 移除文本中的方括号标签（如时间戳）
        /// </summary>
        public static string StripBracketTags(string content)
        {
            if (string.IsNullOrEmpty(content)) return string.Empty;
            return content.Contains('[') ? BracketTagRegex.Replace(content, " ") : content;
        }
    }
}
