using System;
using System.Threading.Tasks;
using Journal.Models;

namespace Journal.Services
{
    /// <summary>
    /// 全局应用设置持久化与分发服务接口
    /// </summary>
    public interface IAppSettingsService
    {
        /// <summary>
        /// 当前全局设置快照
        /// </summary>
        AppSettings CurrentSettings { get; }

        /// <summary>
        /// 初始化加载磁盘配置，若不存在则创建默认配置
        /// </summary>
        Task InitializeAsync();

        /// <summary>
        /// 立即原子保存当前配置至磁盘
        /// </summary>
        Task SaveAsync();

        /// <summary>
        /// 内存即时更新并触发 300ms 磁盘防抖写入
        /// </summary>
        void Update(Action<AppSettings> updateAction);

        /// <summary>
        /// 设置变更事件（内存即时触发）
        /// </summary>
        event EventHandler<AppSettings>? SettingsChanged;
    }
}
