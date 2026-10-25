using System;
using System.Threading.Tasks;
using Journal.Models;

namespace Journal.Services
{
    /// <summary>
    /// “岁月沉淀”（纸张底色与字迹风化）核心渲染引擎服务接口
    /// </summary>
    public interface IAgingEngineService
    {
        /// <summary>
        /// 获取缓存管理服务子实例
        /// </summary>
        IAgingCacheService CacheService { get; }

        /// <summary>
        /// 计算指定日期的老化强度 (0.0 ~ 0.85)
        /// </summary>
        /// <param name="entryDate">日记记录日期</param>
        /// <param name="userStrengthSetting">用户全局强度倍率 (0.0 ~ 1.0)</param>
        /// <param name="isAgingEnabled">老化总开关</param>
        /// <returns>最终渲染强度</returns>
        double CalculateAgingIntensity(DateTime entryDate, double userStrengthSetting, bool isAgingEnabled);

        /// <summary>
        /// 获取指定日期的完整老化动力学分析报告
        /// </summary>
        AgingCalculationResult GetAgingDetails(DateTime entryDate, double userStrengthSetting, bool isAgingEnabled);

        /// <summary>
        /// 异步获取/预处理纸张底色纹理磁盘缓存路径
        /// </summary>
        /// <param name="sourcePath">自定义或默认纹理文件物理路径</param>
        /// <param name="invertTexture">是否反相贴图灰度（反相滤色管线需要）</param>
        /// <returns>处理后用于 XAML / Direct2D 绑定的本地缓存文件绝对路径</returns>
        Task<string> GetPreprocessedPaperTexturePathAsync(string? sourcePath, bool invertTexture);

        /// <summary>
        /// 异步获取/预处理字迹风化纹理磁盘缓存路径
        /// </summary>
        /// <param name="sourcePath">自定义或默认风化纹理文件物理路径</param>
        /// <returns>去色处理后的本地缓存文件绝对路径</returns>
        Task<string> GetPreprocessedInkTexturePathAsync(string? sourcePath);

        /// <summary>
        /// 异步获取/预处理字迹风化纹理磁盘缓存路径（支持深色模式反相）
        /// </summary>
        /// <param name="sourcePath">自定义或默认风化纹理文件物理路径</param>
        /// <param name="isDarkMode">当前是否处于暗黑主题</param>
        /// <returns>处理后用于 XAML / DirectComposition 绑定的本地缓存文件绝对路径</returns>
        Task<string> GetPreprocessedInkTexturePathAsync(string? sourcePath, bool isDarkMode);

        /// <summary>获取由灰度纹理派生的透明字迹风化遮罩。</summary>
        Task<string> GetInkWeatheringMaskPathAsync(string? sourcePath);

        /// <summary>
        /// 当前正在查看的日记日期改变事件
        /// </summary>
        event EventHandler<DateTime>? CurrentViewingDateChanged;

        /// <summary>
        /// 当前正在查看的日记日期
        /// </summary>
        DateTime CurrentViewingDate { get; }

        /// <summary>
        /// 更新当前正在查看的日记日期并通知全软件底色层
        /// </summary>
        void SetCurrentViewingDate(DateTime date);

    }
}
