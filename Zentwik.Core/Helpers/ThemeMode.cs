namespace Zentwik.Core.Helpers
{
    /// <summary>
    /// 主题模式：跟随系统 / 浅色 / 深色。
    /// 注意：.NET 9/10 的 WPF 新增了 System.Windows.ThemeMode，为避免冲突命名为 AppThemeMode。
    /// </summary>
    public enum AppThemeMode
    {
        System,
        Light,
        Dark
    }
}
