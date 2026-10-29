using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZenForge.Helpers
{
    /// <summary>点关闭按钮时的去向。</summary>
    public enum CloseBehavior
    {
        /// <summary>有后台职责（绑核监听在跑）才收进托盘，否则直接退出。</summary>
        Smart,

        /// <summary>总是收进托盘。</summary>
        AlwaysTray,

        /// <summary>总是直接退出。</summary>
        Exit
    }

    /// <summary>
    /// 界面设置（主题 / 强调色），持久化到 %APPDATA%\ZenForge\ui.json
    /// </summary>
    public sealed class UiSettings
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public AppThemeMode ThemeMode { get; set; } = AppThemeMode.System;

        /// <summary>
        /// 强调色是否跟随 Windows 系统强调色。
        /// **默认 true**：首次安装、或配置文件里没有这个键时，一律跟随系统。
        /// </summary>
        public bool FollowSystemAccent { get; set; } = true;

        /// <summary>自定义强调色（十六进制，如 #00B4D8）。仅 FollowSystemAccent=false 时生效。</summary>
        public string CustomAccentHex { get; set; } = "#00B4D8";

        /// <summary>
        /// 配置文件里是否**显式写过** followSystemAccent 这个键（旧配置迁移用）。
        /// 不参与序列化，只在 Load 时置位。
        /// </summary>
        [JsonIgnore]
        public bool FollowSystemAccentExplicit { get; set; }

        // ==================== S4/S5：启动与后台（新增，不影响旧字段） ====================

        /// <summary>
        /// 开机静默自启：是否已注册 Windows 计划任务 ZenForgeAutoStart。
        /// 真实状态以计划任务是否存在为准，本字段仅作缓存与界面回显。
        /// </summary>
        public bool AutoStart { get; set; }

        /// <summary>
        /// 启动时自动应用调校：静默模式（-silent）下是否把 tuning.json 的 CO / FMax 写回 SMU。
        /// </summary>
        public bool ApplyTuningOnStartup { get; set; } = true;

        /// <summary>
        /// 后台常驻绑核：为 true 时进程常驻，按 bindings.json 的规则给新进程自动绑核。
        /// 关掉窗口也不停 —— 这是它唯一有意义的存在方式。
        ///
        /// 默认 true：绑核规则一旦存了，用户要的就是「一直生效」，
        /// 而不是「每次开机记得先手动打开软件」。
        /// </summary>
        public bool WatchProcessBinding { get; set; } = true;

        /// <summary>
        /// 关闭窗口时的去向，见 <see cref="Helpers.CloseBehavior"/>。
        /// 默认 Smart：有后台监听才留驻，没活干就别占内存。
        /// </summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public CloseBehavior CloseBehavior { get; set; } = CloseBehavior.Smart;

        /// <summary>
        /// 后台常驻时是否显示托盘图标。
        ///
        /// 关掉后进程仍然常驻（绑核照跑），但没有任何可见入口 ——
        /// 想打开界面只能双击 ZenForge.exe，而它会请求管理员权限。
        /// 默认 true：用户至少得能看见它在跑，也能就地退出。
        /// </summary>
        public bool ShowTrayIcon { get; set; } = true;
    }

    public static class UiSettingsStore
    {
        private static readonly string Directory =
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZenForge");

        private static readonly string FilePath = System.IO.Path.Combine(Directory, "ui.json");

        public static UiSettings Settings { get; private set; } = new();

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    // 首次运行：默认「跟随系统」强调色 + 跟随系统主题。
                    // 这里给的是出厂默认值，不是"猜"出来的。
                    Settings = new UiSettings();
                    return;
                }

                string json = File.ReadAllText(FilePath);
                Settings = JsonSerializer.Deserialize<UiSettings>(json) ?? new UiSettings();

                // ---------- 兼容旧版 ui.json ----------
                // 旧版字段只有 themeMode / followSystemAccent / customAccentHex，
                // 且 followSystemAccent 的默认值曾经是 false（还配过一个写死的青色），
                // 于是所有人打开程序看到的都是硬编码青，而不是"跟随系统"。
                //
                // 判定规则：文件里**根本没有** followSystemAccent 这个键 → 视为从未设置过，
                // 强制回到「跟随系统」；只要用户明确写过这个键（无论 true/false），就尊重他。
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty(nameof(UiSettings.FollowSystemAccent), out _))
                {
                    Settings.FollowSystemAccentExplicit = true;
                }
                else
                {
                    Settings.FollowSystemAccentExplicit = false;
                    Settings.FollowSystemAccent = true;
                }
            }
            catch
            {
                Settings = new UiSettings();
            }
        }

        public static void Save()
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, FilePath, true);
            }
            catch
            {
                // 设置保存失败不影响主流程
            }
        }
    }
}
