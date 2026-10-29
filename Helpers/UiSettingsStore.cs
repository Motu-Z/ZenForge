using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZenForge.Helpers
{
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
        /// 后台常驻调度：为 true 时进程常驻，按 bindings.json 的规则给新进程自动绑核。
        ///
        /// 注意它不是总闸 —— 要不要有后台由 <see cref="BackendService"/> 决定，
        /// 这一项只是「后台要不要干调度这件事」。
        /// </summary>
        public bool WatchProcessBinding { get; set; } = true;

        /// <summary>
        /// 【总闸】开启后台服务。
        ///
        /// 关掉 = 用户根本不需要后台：点 X 直接彻底退出进程，
        /// 监听 / 托盘 / 单实例锁一并收摊，不搞任何隐藏。
        ///
        /// 打开后才会露出「后台常驻调度 / 仅留服务进程 / 显示托盘图标」。
        /// 默认 false：没人希望装完就被一个退不掉的常驻进程缠上。
        /// </summary>
        public bool BackendService { get; set; }

        /// <summary>
        /// 仅留服务进程（界面上就这么叫）。
        ///
        /// 开启：点 X 后先隐藏，倒计时结束销毁前台界面，进程只剩后台服务；
        /// 关闭：点 X 只是隐藏窗口，界面完整保留，回来是瞬间的。
        ///
        /// 曾经还有一个「仅清空页面」的并行选项，实测两者内存表现一致
        /// （工作集都被压到个位数 MB），留两个只增加理解成本，已删。
        /// </summary>
        public bool LightMode { get; set; }

        /// <summary>
        /// 轻量模式延迟（秒）：关闭前台（点 X / 最小化）之后等多久才销毁界面。
        ///
        /// 目前**固定 30 秒、界面不暴露**，等换 UI 之后再决定要不要做成可调。
        /// 保留这个字段是为了让 MainWindow 读一个值而不是写死字面量，
        /// 将来加回输入框时不用再动调用方。
        /// </summary>
        public int LightModeDelaySeconds { get; set; } = 30;

        /// <summary>
        /// 显示托盘图标。
        ///
        /// 关掉后进程仍然常驻（调度照跑），但没有可见入口 ——
        /// 想打开界面只能双击 ZenForge.exe。
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
                }
                else
                {
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
            }
            catch
            {
                Settings = new UiSettings();
            }

            Normalize();
        }

        /// <summary>
        /// 把数值类字段拉回合法区间。
        ///
        /// 配置文件是纯文本，人手改坏、或从旧版本继承一个 0 / 负数都是常事。
        /// 在这里统一收口，读出来的 Settings 就一定是能直接用、不用每处再判一遍的值。
        /// </summary>
        private static void Normalize()
        {
            UiSettings s = Settings;

            // 延迟目前不可调，默认 30 秒。旧配置里可能是 1（上一版的最小值），
            // 那会让「关掉前台」看起来像「立刻消失」，这里统一抬回 30。
            if (s.LightModeDelaySeconds < 5 || s.LightModeDelaySeconds > 3600)
                s.LightModeDelaySeconds = 30;
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
