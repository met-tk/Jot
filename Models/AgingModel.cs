using System;

namespace Journal.Models
{
    /// <summary>
    /// 岁月沉淀动力学常量定义
    /// </summary>
    public static class AgingConstants
    {
        /// <summary>
        /// 一级动力学特征时间常数 tau（天）
        /// </summary>
        public const double DefaultTimeConstantDays = 260.0;

        /// <summary>
        /// 最大老化饱和度上限（85%）
        /// </summary>
        public const double MaxAgingSaturation = 0.85;

        /// <summary>
        /// 预设默认纸张纹理相对文件名
        /// </summary>
        public const string DefaultPaperTextureFileName = "default-paper.png";

        /// <summary>
        /// 预设默认墨水风化纹理相对文件名
        /// </summary>
        public const string DefaultInkTextureFileName = "default-ink.png";

        /// <summary>
        /// 本地多级磁盘缓存子目录名称
        /// </summary>
        public const string CacheSubdirectoryName = "AgingCache";
    }

    /// <summary>
    /// 单篇日记老化计算结果不可变数据包
    /// </summary>
    public sealed class AgingCalculationResult
    {
        /// <summary>
        /// 日记记录日期
        /// </summary>
        public DateTime EntryDate { get; init; }

        /// <summary>
        /// 距今已过去的天数差 Delta t (基于自然日 00:00:00 计算)
        /// </summary>
        public int DaysDifference { get; init; }

        /// <summary>
        /// 未经用户强度缩放的纯动力学强度 (0.0 ~ 0.85)
        /// </summary>
        public double RawKineticsIntensity { get; init; }

        /// <summary>
        /// 经用户滑块设定与功能开关调节后的最终渲染强度 (0.0 ~ 0.85)
        /// </summary>
        public double FinalIntensity { get; init; }

        /// <summary>
        /// 是否属于首日免老化豁免状态 (Delta t <= 0)
        /// </summary>
        public bool IsExempt { get; init; }

        /// <summary>
        /// 年代阶段语义标签（如“今日随笔”、“初染风华”、“韶华旧忆”等）
        /// </summary>
        public string AgeCategory { get; init; } = string.Empty;

        /// <summary>
        /// 是否启用了老化渲染
        /// </summary>
        public bool IsEnabled { get; init; }
    }

    /// <summary>
    /// 岁月沉淀核心数学模型与纯静态动力学计算器
    /// </summary>
    public static class AgingModel
    {
        /// <summary>
        /// 直接计算最终渲染老化强度 (0.0 ~ 0.85)
        /// </summary>
        public static double CalculateIntensity(
            DateTime entryDate,
            double userStrength,
            bool isAgingEnabled,
            DateTime? referenceToday = null)
        {
            if (!isAgingEnabled) return 0.0;
            if (double.IsNaN(userStrength) || userStrength <= 0.0) return 0.0;

            DateTime today = (referenceToday ?? DateTime.Today).Date;
            DateTime targetDate = entryDate.Date;
            int deltaDays = (today - targetDate).Days;

            // F03: Day-0 Aging Immunity - 当日及未来日记强度严格恒为 0.0
            if (deltaDays <= 0) return 0.0;

            // F02: 一级动力学模型 I = S_user * 0.85 * (1 - e^(-deltaDays / 260))
            double decayFactor = 1.0 - Math.Exp(-deltaDays / AgingConstants.DefaultTimeConstantDays);
            double sUser = Math.Clamp(userStrength, 0.0, 1.0);
            double intensity = sUser * AgingConstants.MaxAgingSaturation * decayFactor;

            return Math.Clamp(intensity, 0.0, AgingConstants.MaxAgingSaturation);
        }

        /// <summary>
        /// 计算日记老化动力学参数并返回完整状态报告包
        /// </summary>
        public static AgingCalculationResult Calculate(
            DateTime entryDate,
            double userStrength,
            bool isAgingEnabled,
            DateTime? referenceToday = null)
        {
            DateTime today = (referenceToday ?? DateTime.Today).Date;
            DateTime targetDate = entryDate.Date;
            int deltaDays = (today - targetDate).Days;

            // 首日豁免及未来日记防御：Delta t <= 0 强度强制归零
            if (!isAgingEnabled || deltaDays <= 0 || double.IsNaN(userStrength) || userStrength <= 0.0)
            {
                return new AgingCalculationResult
                {
                    EntryDate = targetDate,
                    DaysDifference = Math.Max(0, deltaDays),
                    RawKineticsIntensity = 0.0,
                    FinalIntensity = 0.0,
                    IsExempt = deltaDays <= 0,
                    AgeCategory = deltaDays <= 0 ? "今日随笔" : "未开启",
                    IsEnabled = isAgingEnabled
                };
            }

            // 一级反应动力学：Raw = MaxSaturation * (1 - e^(-days / tau))
            double decayFactor = 1.0 - Math.Exp(-deltaDays / AgingConstants.DefaultTimeConstantDays);
            double rawIntensity = AgingConstants.MaxAgingSaturation * decayFactor;

            double clampedUserStrength = Math.Clamp(userStrength, 0.0, 1.0);
            double finalIntensity = Math.Clamp(rawIntensity * clampedUserStrength, 0.0, AgingConstants.MaxAgingSaturation);

            string category = deltaDays switch
            {
                <= 7 => "墨痕尚新",
                <= 30 => "初染风华",
                <= 90 => "岁月微痕",
                <= 180 => "半载陈墨",
                <= 365 => "韶华旧忆",
                <= 730 => "沧海桑田",
                _ => "陈年旧卷"
            };

            return new AgingCalculationResult
            {
                EntryDate = targetDate,
                DaysDifference = deltaDays,
                RawKineticsIntensity = rawIntensity,
                FinalIntensity = finalIntensity,
                IsExempt = false,
                AgeCategory = category,
                IsEnabled = true
            };
        }
    }
}
