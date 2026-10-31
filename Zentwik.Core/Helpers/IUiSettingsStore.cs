namespace Zentwik.Core.Helpers
{
    /// <summary>界面设置（主题 / 强调色 / 后台行为）的持久化。</summary>
    public interface IUiSettingsStore
    {
        UiSettings Settings { get; }

        void Load();

        void ResetToFactory();

        void Save();
    }
}
