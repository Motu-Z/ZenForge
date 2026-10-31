namespace Zentwik.Core.Helpers
{
    /// <summary>
    /// 调校配置（CO 每核负压 + FMax）的持久化。
    /// 只保存「已成功下发并回读校验通过」的设定，供开机静默自启重新应用。
    /// </summary>
    public interface ITuningStore
    {
        TuningProfile Profile { get; }

        void Load();

        /// <summary>覆盖当前配置并落盘，同时刷新 AppliedAt。</summary>
        void Save(TuningProfile profile);

        /// <summary>
        /// 全局重置：CO 归零、FMax 回到出厂基线。
        /// 返回实际生效的 FMax；返回 0 表示基线未知，调用方应跳过 FMax 写回。
        /// </summary>
        uint ResetToFactory();
    }
}
