using System;
using System.Threading.Tasks;

namespace Journal.Services
{
    /// <summary>
    /// 岁月沉淀渲染资产多级缓存服务接口
    /// 负责管理预处理灰度图、反相贴图的磁盘与内存缓存，保障极速检索与单飞防重
    /// </summary>
    public interface IAgingCacheService
    {
        /// <summary>
        /// 物理缓存目录绝对路径（通常为 %LOCALAPPDATA%\Jot\AgingCache 或备用降级路径）
        /// </summary>
        string CacheDirectoryPath { get; }

        /// <summary>
        /// 获取或生成缓存文件路径。若已有有效缓存则秒级直返，若无则执行异步生成并原子落盘。
        /// </summary>
        /// <param name="sourcePath">源贴图文件物理路径</param>
        /// <param name="mode">处理模式（"gray"、"inv" 或 "wearmask-v4"）</param>
        /// <param name="processAction">生成委托 (sourcePath, tempOutputPath) => Task</param>
        /// <returns>已就绪的缓存文件完整路径</returns>
        Task<string> GetOrCreateCachedFileAsync(string sourcePath, string mode, Func<string, string, Task> processAction);

        /// <summary>
        /// 根据源路径、文件最后修改时间及模式计算确定性 SHA256 缓存键
        /// </summary>
        string ComputeCacheKey(string sourcePath, string mode);

        /// <summary>
        /// 获取指定缓存键与模式对应的目标文件路径 (<sha256>_<mode>.png)
        /// </summary>
        string GetCachedFilePath(string cacheKey, string mode);

        /// <summary>
        /// 统计当前缓存目录所占用的总物理磁盘字节数
        /// </summary>
        long GetCacheSizeBytes();

        /// <summary>
        /// 物理清理并删除所有磁盘缓存与临时文件
        /// </summary>
        void ClearAllDiskCaches();
    }
}
