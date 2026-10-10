namespace Journal.Services
{
    /// <summary>
    /// 极简低延迟打字物理音效引擎接口
    /// </summary>
    public interface ISoundEffectService
    {
        /// <summary>
        /// 音效是否启用
        /// </summary>
        bool IsEnabled { get; }

        /// <summary>
        /// 当前音效主题标识 ("Typewriter", "CherryBlue", "CherryBrown")
        /// </summary>
        string CurrentTheme { get; }

        /// <summary>
        /// 当前音量 (0.0 ~ 1.0)
        /// </summary>
        double Volume { get; }

        /// <summary>
        /// 播放常规按键或特殊键（退格/空格）敲击音效（微秒级异步执行）
        /// </summary>
        void PlayKeyClick(bool isSpecialKey = false);

        /// <summary>
        /// 播放回车换行专属音效（打字机黄铜长铃或轴体段落）
        /// </summary>
        void PlayEnterKey();

        /// <summary>
        /// 切换音效主题
        /// </summary>
        void SetTheme(string theme);

        /// <summary>
        /// 设置音效音量 (0.0 ~ 1.0)
        /// </summary>
        void SetVolume(double volume);

        /// <summary>
        /// 开启或静音音效
        /// </summary>
        void SetEnabled(bool enabled);
    }
}
